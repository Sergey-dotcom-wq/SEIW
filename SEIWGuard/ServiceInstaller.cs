using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SEIWGuard;

/// <summary>Установка/удаление службы SEIWGuard (требует прав администратора).</summary>
internal static class ServiceInstaller
{
    public static void Install()
    {
        var exe = Environment.ProcessPath
                  ?? Path.Combine(AppContext.BaseDirectory, "SEIWGuard.exe");
        var binDir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;

        // Копируем агента + общую библиотеку + базу в каталог службы рядом с SEIWGuard.exe.
        CopyAgentFiles();

        RunSc($"create SEIWGuard start= auto binPath= \"{exe}\"");
        RunSc("description SEIWGuard \"Замок поверх рабочего стола Windows (не заменяет вход в систему)\"");
        Console.WriteLine("Служба SEIWGuard установлена. Запуск: sc start SEIWGuard");
    }

    private static void CopyAgentFiles()
    {
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(exeDir)) return;
        var binDir = exeDir;

        // Агент ищется рядом со службой; если уже на месте — не копируем сам в себя.
        var targetAgent = Path.Combine(binDir, "LockAgent.exe");
        if (File.Exists(targetAgent))
        {
            Console.WriteLine($"LockAgent.exe уже рядом со службой: {targetAgent}");
        }
        else
        {
            var agentPaths = new[]
            {
                Path.Combine(binDir, "..", "..", "..", "LockAgent", "bin", "Debug", "net8.0-windows", "LockAgent.exe"),
                Path.Combine(binDir, "..", "LockAgent.exe"),
            };
            var foundAgent = agentPaths.FirstOrDefault(File.Exists);
            if (foundAgent != null)
            {
                try { File.Copy(foundAgent, targetAgent, overwrite: true); Console.WriteLine($"Скопирован LockAgent.exe в {binDir}"); }
                catch (Exception ex) { Console.WriteLine("Не удалось скопировать LockAgent.exe: " + ex.Message); }
            }
            else
            {
                Console.WriteLine("ВНИМАНИЕ: LockAgent.exe не найден — замок будет неактивен. Соберите LockAgent рядом со службой.");
            }
        }

        // База пользователей/расписания (из каталога установки службы или основной программы).
        foreach (var db in new[] { "seiw_users.json", "seiw_schedule.json" })
        {
            var src = Path.Combine(AppContext.BaseDirectory, db);
            var dst = Path.Combine(binDir, db);
            if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
        }
    }

    public static void Uninstall()
    {
        RunSc("stop SEIWGuard");
        RunSc("delete SEIWGuard");
        Console.WriteLine("Служба SEIWGuard удалена.");
    }

    private static void RunSc(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        if (p is null) return;
        string outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (!string.IsNullOrEmpty(outp)) Console.WriteLine(outp);
    }
}