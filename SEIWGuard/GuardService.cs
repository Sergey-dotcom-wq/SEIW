using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SEIWCore;

namespace SEIWGuard;

/// <summary>
/// Windows-служба «замок сверху». Работает под SYSTEM, следит за интерактивными
/// сессиями и запускает агент замка (LockAgent) в каждой активной сессии.
/// Учитывает расписание (SEIWCore.ScheduleInterval): в интервале Lock агенты держат
/// замок, в Shutdown — выключает компьютер, в Message — шлёт уведомление.
/// НЕ заменяет стандартный вход Windows — только добавляет замок поверх рабочего стола;
/// стандартный вход и Ctrl+Alt+Del остаются рабочими, а служба может быть остановлена.
/// </summary>
public sealed class GuardService : IHostedService, IDisposable
{
    private readonly ILogger<GuardService> _log;
    private readonly LockManager _lock;
    private WtsNotification? _notifier;
    private Timer? _scheduleTimer;
    private readonly object _scheduleSync = new();
    private List<ScheduleInterval> _schedule = new();

    public GuardService(ILogger<GuardService> log)
    {
        _log = log;
        _lock = new LockManager(log);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("SEIWGuard service starting (замок поверх рабочего стола).");
        _lock.Start();
        _notifier = new WtsNotification(
            sessionId => { try { _lock.PingSession(sessionId); } catch (Exception ex) { _log.LogError(ex, "Launch agent session {s}", sessionId); } },
            _log);
        _notifier.Start();

        // Обеспечиваем запуск агента в уже активных сессиях (не только по будущим событиям).
        foreach (var sid in WtsApi.EnumerateInteractiveSessions())
        {
            try { _lock.PingSession((int)sid); }
            catch (Exception ex) { _log.LogError(ex, "Launch agent on start in session {s}", sid); }
        }

        ReloadSchedule();
        _scheduleTimer = new Timer(_ => CheckSchedule(), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    private void ReloadSchedule() => _schedule = SeiwStore.LoadSchedule();

    private void CheckSchedule()
    {
        try
        {
            ReloadSchedule();
            ScheduleInterval? active = null;
            lock (_scheduleSync)
                active = _schedule.FirstOrDefault(iv => iv.IsActive(DateTime.Now));
            if (active is null) return;

            foreach (var sid in WtsApi.EnumerateInteractiveSessions())
            {
                switch (active.Action.ToUpperInvariant())
                {
                    case "SHUTDOWN":
                        _lock.SendCommand((int)sid, "SHUTDOWN");
                        break;
                    case "MESSAGE":
                        if (!string.IsNullOrEmpty(active.Message))
                            _lock.SendCommand((int)sid, "MESSAGE", active.Message);
                        break;
                    default: // LOCK
                        _lock.SendCommand((int)sid, "LOCK");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CheckSchedule error");
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("SEIWGuard service stopping.");
        _scheduleTimer?.Dispose();
        _notifier?.Dispose();
        _lock.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() { _scheduleTimer?.Dispose(); _notifier?.Dispose(); _lock?.Dispose(); }
}