using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SEIWCore;

namespace SEIWGuard;

/// <summary>
/// Управляет агентами замка: запускает LockAgent.exe в каждой интерактивной сессии
/// через WTSQueryUserToken + CreateProcessAsUser. Сессия с уже запущенным агентом не дублируется.
/// </summary>
internal sealed class LockManager : IDisposable
{
    // ===== Win32 для запуска процесса в чужой сессии (под SYSTEM) =====
    [DllImport("wtsapi32.dll")]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput, hStdOutput, hStdErr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    private const int MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    private readonly ILogger _log;
    private readonly HashSet<int> _sessionsWithAgent = new();
    private readonly object _sync = new();

    public LockManager(ILogger log) { _log = log; }

    public void Start() { _log.LogInformation("LockManager started."); }

    /// <summary>Обеспечивает запуск агента в указанной сессии.</summary>
    public void PingSession(int sessionId) => EnsureAgentInSession(sessionId);
    public void EnsureAgentInSession(int sessionId)
    {
        lock (_sync)
        {
            if (sessionId == 0) return; // session 0 = система/non-interactive
            if (_sessionsWithAgent.Contains(sessionId)) return;

            if (LaunchInSession(sessionId))
            {
                _sessionsWithAgent.Add(sessionId);
                _log.LogInformation("Agent launched in session {s}", sessionId);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500); // дать агенту подняться
                    SendCommand(sessionId, "LOCK");
                });
            }
            else
            {
                _log.LogWarning("Could not launch agent in session {s}", sessionId);
            }
        }
    }

    private static string AgentPath()
    {
        var dir = AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, "LockAgent.exe");
        return File.Exists(candidate) ? candidate : Path.Combine(dir, "..", "LockAgent.exe");
    }

    /// <summary>Отправляет команду IPC агенту (по сессионному каналу). Используется для UNLOCK/SHUTDOWN/MESSAGE.</summary>
    public void SendCommand(int sessionId, string cmd, string arg = "")
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SeiwIpc.PipeName, PipeDirection.InOut);
            client.Connect(2000);
            SeiwIpc.WriteLine(client, SeiwIpc.Compose(cmd, arg));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IPC send to session {s} failed", sessionId);
        }
    }

    /// <summary>
    /// Запускает LockAgent.exe в целевой сессии через WTSQueryUserToken + CreateProcessAsUser.
    /// </summary>
    private bool LaunchInSession(int sessionId)
    {
        var agent = AgentPath();
        if (!File.Exists(agent)) { _log.LogError("LockAgent не найден: {p}", agent); return false; }

        if (!WTSQueryUserToken((uint)sessionId, out IntPtr userToken))
        {
            _log.LogWarning("WTSQueryUserToken failed session {s} err={e}", sessionId, Marshal.GetLastWin32Error());
            return false;
        }
        try
        {
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr primaryToken))
            {
                _log.LogWarning("DuplicateTokenEx failed err={e}", Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                IntPtr env = IntPtr.Zero;
                CreateEnvironmentBlock(out env, primaryToken, false);

                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                var pi = default(PROCESS_INFORMATION);
                var cmdline = "\"" + agent + "\" --session " + sessionId;
                bool ok = CreateProcessAsUser(primaryToken, null, cmdline, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE, env, Path.GetDirectoryName(agent), ref si, out pi);
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                if (ok)
                {
                    if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                    if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                    _log.LogInformation("LockAgent запущен в сессии {s}, pid={pid}", sessionId, pi.dwProcessId);
                    return true;
                }
                _log.LogWarning("CreateProcessAsUser failed session {s} err={e}", sessionId, Marshal.GetLastWin32Error());
                return false;
            }
            finally { CloseHandle(primaryToken); }
        }
        finally { CloseHandle(userToken); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Stop()
    {
        // Агенты завершатся сами при выходе из сессий; на остановке службы просто очищаем список.
        lock (_sync) _sessionsWithAgent.Clear();
    }

    public void Dispose() => Stop();
}