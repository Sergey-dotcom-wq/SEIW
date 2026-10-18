using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SEIWGuard;

internal static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--interactive":
                    RunInteractive();
                    return;
                case "--install":
                    ServiceInstaller.Install();
                    return;
                case "--uninstall":
                    ServiceInstaller.Uninstall();
                    return;
            }
        }

        var host = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options => options.ServiceName = "SEIWGuard")
            .ConfigureServices((_, s) => s.AddHostedService<GuardService>())
            .ConfigureLogging(l => l.AddEventLog())
            .Build();

        host.Run();
    }

    /// <summary>Запуск в обычном процессе для отладки/проверки (без прав службы).</summary>
    private static void RunInteractive()
    {
        using var cts = new CancellationTokenSource();
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, s) => s.AddHostedService<GuardService>())
            .Build();
        var log = host.Services.GetRequiredService<ILogger<object>>();
        log.LogInformation("SEIWGuard running in interactive mode. Press Ctrl+C to stop.");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        host.Start();
        Console.WriteLine("Running... (Ctrl+C to stop)");
        System.Threading.Thread.Sleep(Timeout.InfiniteTimeSpan);
    }
}