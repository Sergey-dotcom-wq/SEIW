using System.IO.Pipes;

namespace SEIWCore;

/// <summary>Ответ IPC: код и пояснение.</summary>
public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Action { get; set; } = ""; // "", "lock", "unlock", "shutdown", "message"
    public string Payload { get; set; } = "";
}

/// <summary>
/// Лёгкий IPC между службой SEIWGuard и агентом-замком LockAgent.
/// Канал: «SEIWGuard\cmd». Формат сообщения — строка: «cmd аргумент».
/// Команды (от службы к агенту): LOCK, UNLOCK, SHUTDOWN, MESSAGE текст.
/// Команды (от агента к службе): PING (проверка живости), STATUS.
/// </summary>
public static class SeiwIpc
{
    public const string PipeName = "SEIWGuard\\cmd";

    public static string Compose(string cmd, string arg = "")
        => string.IsNullOrEmpty(arg) ? cmd.ToUpperInvariant() : cmd.ToUpperInvariant() + " " + arg;

    public static bool Parse(string line, out string cmd, out string arg)
    {
        cmd = ""; arg = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        var sp = line.IndexOf(' ');
        if (sp < 0) { cmd = line.Trim().ToUpperInvariant(); return true; }
        cmd = line.Substring(0, sp).Trim().ToUpperInvariant();
        arg = line.Substring(sp + 1).Trim();
        return true;
    }

    /// <summary>Читает одну строку из канала (максимум 30 сек). Возвращает null при таймауте/ошибке.</summary>
    public static string? ReadLineTimeout(PipeStream stream, int timeoutMs = 30000)
    {
        try
        {
            stream.ReadTimeout = timeoutMs;
            var s = stream as Stream;
            using var reader = new StreamReader(s, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
            return reader.ReadLine();
        }
        catch { return null; }
    }

    public static void WriteLine(PipeStream stream, string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }
}