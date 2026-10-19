using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SEIWCore;

namespace LockAgent;

public partial class MainWindow : Window
{
    // ===== Принудительная английская раскладка в поле пароля =====
    [DllImport("user32.dll")]
    private static extern bool ActivateKeyboardLayout(IntPtr hkl, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

    private static readonly IntPtr EnUs = LoadKeyboardLayout("00000409", 0x00000001);

    private static void ForceEnglishLayout()
    {
        try { ActivateKeyboardLayout(EnUs, 0); } catch { }
    }

    private List<SeiwUser> _users;
    private SeiwUser? _current;
    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _maskTimer;
    private readonly List<string> _typed = new();
    private bool _showPassword;

    // КВП (круговой ввод): текущий набор секторов
    private readonly List<int> _dial = new();
    private int _dialSectors = 12;
    private readonly List<System.Windows.Shapes.Path> _dialShapes = new();

    public MainWindow()
    {
        InitializeComponent();

        // Если установка не активирована — показываем окно активации и не открываем замок.
        if (!License.IsActivated(AppContext.BaseDirectory))
        {
            ShowActivation();
            return;
        }

        _users = SeiwStore.LoadUsers();
        _current = _users.FirstOrDefault();
        if (_current == null)
        {
            // Нет настроенного пользователя — замок некому открыть; показываем сообщение
            // и не блокируем (стандартный вход остаётся рабочим).
            UserNameLabel.Text = "Not configured";
            StatusText.Text = "Set up SEIW in your account first, then enable the lock.";
            EnterBtn.IsEnabled = false;
            return;
        }
        AvatarLetter.Text = _current.Name.Length > 0 ? _current.Name.Substring(0, 1).ToUpper() : "U";
        UserNameLabel.Text = _current.Name;

        InitCircularIfNeeded();

        _maskTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _maskTimer.Tick += (_, _) => { _maskTimer.Stop(); RenderPassword(false); };
        StartClock();

        Loaded += (_, _) => FocusPassword();
        Activated += (_, _) => FocusPassword();
        Deactivated += (_, _) => { Topmost = false; Topmost = true; Activate(); FocusPassword(); };
        Closing += (_, e) =>
        {
            if (!_allowClose) e.Cancel = true;
        };

        StartIpcServer();
    }

    // ===== IPC-сервер: приём команд от службы (LOCK/UNLOCK/SHUTDOWN/MESSAGE) =====
    private CancellationTokenSource _ipcCts = new();
    private readonly object _closeLock = new();

    private void StartIpcServer()
    {
        Task.Run(async () =>
        {
            while (!_ipcCts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(SeiwIpc.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_ipcCts.Token);
                    while (server.IsConnected)
                    {
                        var line = SeiwIpc.ReadLineTimeout(server, 5000);
                        if (line == null) break;
                        if (SeiwIpc.Parse(line, out var cmd, out var arg))
                            Dispatcher.Invoke(() => HandleIpc(cmd, arg));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* канал временно занят — ждём и пробуем снова */ }
                await Task.Delay(500, _ipcCts.Token);
            }
        }, _ipcCts.Token);
    }

    private void HandleIpc(string cmd, string arg)
    {
        switch (cmd)
        {
            case "LOCK":
                // Замок уже показан всегда; при LOCK просто остаёмся на экране.
                Show();
                Activate();
                FocusPassword();
                break;
            case "UNLOCK":
                // Служба разрешила вход (например, по расписанию) — снимаем замок.
                RequestClose();
                break;
            case "SHUTDOWN":
                StatusText.Text = "Work time expired. The computer will shut down.";
                try { System.Diagnostics.Process.Start("shutdown", "/s /t 30"); }
                catch { }
                break;
            case "MESSAGE":
                if (!string.IsNullOrEmpty(arg)) StatusText.Text = arg;
                break;
        }
    }

    private void RequestClose()
    {
        lock (_closeLock)
        {
            if (_allowClose) return;
            _allowClose = true;
            Close();
        }
    }

    private void StopIpc() => _ipcCts.Cancel();

    private bool _allowClose;

    private void FocusPassword() { ForceEnglishLayout(); PasswordBox.Focus(); PasswordBox.CaretIndex = PasswordBox.Text.Length; }

    private void StartClock()
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { UpdateClock(); ForceEnglishLayout(); };
        _clockTimer.Start();
        UpdateClock();
        ForceEnglishLayout();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("HH:mm");
        ClockDate.Text = now.ToString("dddd, d MMMM", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    }

    private void PasswordBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        foreach (var ch in e.Text)
            if (ch != '\r' && ch != '\n') _typed.Add(ch.ToString());
        RenderPassword(true);
    }

    private void PasswordBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; DoLogin(); }
        else if ((e.Key == Key.Back || e.Key == Key.Delete) && _typed.Count > 0)
        { e.Handled = true; _typed.RemoveAt(_typed.Count - 1); RenderPassword(false); }
    }

    private void RenderPassword(bool revealLast)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _typed.Count; i++)
            sb.Append(_showPassword || (revealLast && i == _typed.Count - 1) ? _typed[i] : "●");
        PasswordBox.Text = sb.ToString();
        PasswordBox.CaretIndex = PasswordBox.Text.Length;
        if (revealLast && !_showPassword) { _maskTimer!.Stop(); _maskTimer.Start(); }
    }

    private void ShowPasswordBtn_Click(object sender, RoutedEventArgs e)
    {
        _showPassword = !_showPassword;
        ShowPasswordBtn.Content = _showPassword ? "\u2716" : "\U0001F441";
        RenderPassword(false);
    }

    private void EnterBtn_Click(object sender, RoutedEventArgs e) => DoLogin();

    // ===== Активация по ключу =====
    private void ShowActivation()
    {
        ActivatePanel.Visibility = Visibility.Visible;
        LoginPanel.Visibility = Visibility.Collapsed;
        KeyBox.Focus();
    }

    private void ActivateBtn_Click(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Text.Trim();
        var dir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(key)) { ActivateStatus.Text = "Enter the license key."; return; }

        if (License.TryActivate(key, dir))
        {
            License.SetActivated(dir);
            ActivateStatus.Text = "Activation successful!";
            ActivatePanel.Visibility = Visibility.Collapsed;
            // Повторно инициализируем замок этой же логикой (после активации — обычный вход).
            ActivateAndContinue();
        }
        else
        {
            ActivateStatus.Text = "Invalid or already used key.";
        }
    }

    private void ActivateAndContinue()
    {
        // После успешной активации загружаем базу и показываем замок.
        _users = SeiwStore.LoadUsers();
        _current = _users.FirstOrDefault();
        if (_current == null)
        {
            UserNameLabel.Text = "Not configured";
            StatusText.Text = "Set up SEIW first, then enable the lock.";
            EnterBtn.IsEnabled = false;
        }
        else
        {
AvatarLetter.Text = _current.Name.Length > 0 ? _current.Name.Substring(0, 1).ToUpper() : "U";
            UserNameLabel.Text = _current.Name;
        }
        InitCircularIfNeeded();
        LoginPanel.Visibility = Visibility.Visible;
        FocusPassword();
    }

    // ===== КВП: круговой циферблат =====
    private void InitCircularIfNeeded()
    {
        if (_current != null && _current.UseCircular)
        {
            _dialSectors = _current.CircularSectors >= 4 ? _current.CircularSectors : 12;
            DialCanvas.Visibility = Visibility.Visible;
            BuildDial();
            UpdateDialText();
        }
        else
        {
            DialCanvas.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildDial()
    {
        DialCanvas.Children.Clear();
        _dialShapes.Clear();
        double cx = DialCanvas.Width / 2.0, cy = DialCanvas.Height / 2.0, R = Math.Min(DialCanvas.Width, DialCanvas.Height) / 2.0 - 6;

        for (int i = 0; i < _dialSectors; i++)
        {
            double start = i * 2 * Math.PI / _dialSectors - Math.PI / 2;
            double sweep = 2 * Math.PI / _dialSectors;

            var seg = new System.Windows.Media.PathSegmentCollection();
            var fig = new System.Windows.Media.PathFigure
            {
                StartPoint = new Point(cx, cy),
                IsClosed = true,
                IsFilled = true
            };
            fig.Segments.Add(new System.Windows.Media.LineSegment(new Point(cx + R * Math.Cos(start), cy + R * Math.Sin(start)), true));
            bool large = sweep > Math.PI;
            fig.Segments.Add(new System.Windows.Media.ArcSegment
            {
                Point = new Point(cx + R * Math.Cos(start + sweep), cy + R * Math.Sin(start + sweep)),
                Size = new Size(R, R),
                IsLargeArc = large,
                SweepDirection = System.Windows.Media.SweepDirection.Clockwise
            });
            var p = new System.Windows.Shapes.Path
            {
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x5A, 0x81, 0x9A)),
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                Data = new System.Windows.Media.PathGeometry(new[] { fig }),
                Tag = i
            };
            p.MouseLeftButtonDown += DialSegment_Click;
            var segColl = new System.Windows.Media.PathGeometry();
            _dialShapes.Add(p);
            DialCanvas.Children.Add(p);

            // Подпись сектора (0..N-1)
            double mid = start + sweep / 2;
            var lbl = new TextBlock
            {
                Text = i.ToString(),
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCF, 0xE0, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(lbl, cx + (R * 0.7) * Math.Cos(mid) - 7);
            Canvas.SetTop(lbl, cy + (R * 0.7) * Math.Sin(mid) - 9);
            DialCanvas.Children.Add(lbl);
        }
    }

    private Point LastDialPoint;
    private void DialSegment_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Shapes.Path p && p.Tag is int idx)
        {
            _dial.Add(idx);
            LastDialPoint = e.GetPosition(DialCanvas);
            UpdateDialText();
        }
    }

    private void DialCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(DialCanvas);
        double cx = DialCanvas.Width / 2.0, cy = DialCanvas.Height / 2.0;
        double dx = pos.X - cx, dy = pos.Y - cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double R = Math.Min(DialCanvas.Width, DialCanvas.Height) / 2.0 - 6;
        if (dist > R) return; // клик вне круга

        double ang = Math.Atan2(dy, dx) + Math.PI / 2;
        if (ang < 0) ang += 2 * Math.PI;
        int idx = (int)(ang / (2 * Math.PI / _dialSectors)) % _dialSectors;
        _dial.Add(idx);
        UpdateDialText();
    }

    private void UpdateDialText()
    {
        DialProgress.Text = "Sequence: " + string.Join("·", _dial);
        if (_dial.Count >= 4)
        {
            // Введено достаточно — проверяем вход
            CheckDialLogin();
        }
    }

    private void DialClear_Click(object sender, RoutedEventArgs e)
    {
        _dial.Clear();
        UpdateDialText();
        StatusText.Text = "";
    }

    private void CheckDialLogin()
    {
        if (_current is null) return;
        if (_current.CheckCircularPassword(_dialSectors, _dial))
        {
            StatusText.Text = "Access granted";
            EnterBtn.IsEnabled = false;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (_, _) => { t.Stop(); _allowClose = true; Close(); };
            t.Start();
        }
        else
        {
            StatusText.Text = "Wrong sequence. Try again.";
            _dial.Clear();
            UpdateDialText();
        }
    }

    // Кнопка «Удалить»: стирает последний введённый символ пароля.
    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_typed.Count > 0)
        {
            _typed.RemoveAt(_typed.Count - 1);
            RenderPassword(false);
        }
        StatusText.Text = "";
    }

    // Кнопка «Назад»: очищает всё поле пароля (сброс ввода).
    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        _typed.Clear();
        RenderPassword(false);
        StatusText.Text = "";
    }

    private void DoLogin()
    {
        if (_current is null) return;
        string typed = string.Join("", _typed);

        // 1) Аварийный код ПВ — открывает мастер смены пароля (восстановление).
        var fireHash = SeiwStore.GetFireExitHash();
        bool fireOk = !string.IsNullOrEmpty(fireHash)
                      && SeiwUser.HashPassword(typed, "").Equals(fireHash, StringComparison.OrdinalIgnoreCase);
        if (fireOk)
        {
            ShowChangePassPanel();
            return;
        }

        // 2) Обычный пароль пользователя (с учётом «живых» знаков).
        var liveChars = new HashSet<string>(_current.LiveChars, StringComparer.Ordinal);
        string liveOnly = new string(typed.Where(c => liveChars.Contains(c.ToString())).ToArray());
        bool ok = _current.CheckPassword(typed) || _current.CheckPassword(liveOnly);

        if (ok)
        {
            // Успешный вход — снимаем замок (закрываем окно агента).
            StatusText.Text = "Access granted";
            EnterBtn.IsEnabled = false;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (_, _) => { t.Stop(); _allowClose = true; Close(); };
            t.Start();
        }
        else
        {
            StatusText.Text = "Wrong password. Try again.";
            _typed.Clear();
            PasswordBox.Text = "";
            FocusPassword();
        }
    }

    // ===== Мастер смены пароля =====
    private void ShowChangePassPanel()
    {
        NewPassBox.Clear(); NewPassBox2.Clear(); ChangeStatusText.Text = "";
        LoginPanel.Visibility = Visibility.Collapsed;
        ChangePassPanel.Visibility = Visibility.Visible;
        NewPassBox.Focus();
    }

    private void CancelChange_Click(object sender, RoutedEventArgs e)
    {
        ChangePassPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        _typed.Clear();
        PasswordBox.Text = "";
        StatusText.Text = "";
        FocusPassword();
    }

    private void ApplyPass_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        string p1 = NewPassBox.Text;
        string p2 = NewPassBox2.Text;
        if (p1.Length < 4) { ChangeStatusText.Text = "Password must be at least 4 characters."; return; }
        if (p1 != p2) { ChangeStatusText.Text = "Passwords do not match."; return; }

        _current.SetPassword(p1);
        // Сохраняем изменённого пользователя в ту же базу (список пользователей).
        var all = SeiwStore.LoadUsers();
        var idx = all.FindIndex(u => u.Id == _current.Id);
        if (idx >= 0) all[idx] = _current; else all.Add(_current);
        SeiwStore.SaveUsers(all);
        ChangeStatusText.Text = "Password saved. Press Sign in on the lock screen.";
        // Возвращаемся на замок с новым паролем.
        ChangePassPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        _typed.Clear();
        PasswordBox.Text = "";
        StatusText.Text = "Password changed. Sign in with the new password.";
        FocusPassword();
    }
}