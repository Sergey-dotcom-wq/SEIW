using System.Windows;

namespace LockAgent;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Single-instance проверку отключаем для надёжного запуска окна замка.
        // (Оставлен комментарий; при работе через службу дубликаты не создаются.)
    }
}