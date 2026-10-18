using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace SEIWGuard;

/// <summary>
/// Следит за событиями сессий Windows через SystemEvents.SessionSwitch
/// (logon/unlock/lock/disconnect) и вызывает callback для запуска агента замка.
/// </summary>
internal sealed class WtsNotification : IDisposable
{
    private readonly Action<int> _onSessionActivity;
    private readonly ILogger _log;

    public WtsNotification(Action<int> onSessionActivity, ILogger log)
    {
        _onSessionActivity = onSessionActivity;
        _log = log;
    }

    public void Start()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _log.LogInformation("WtsNotification: слушаем события сессий Windows.");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try
        {
            _log.LogInformation("SessionSwitch {reason}", e.Reason);
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLogon:
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.RemoteDisconnect:
                case SessionSwitchReason.ConsoleConnect:
                    // Пропингуем все активные интерактивные сессии; агент уже запущенный — не дублируется.
                    foreach (var sid in WtsApi.EnumerateInteractiveSessions())
                        _onSessionActivity((int)sid);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка обработки события сессии");
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}