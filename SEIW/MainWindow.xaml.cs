using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SEIW;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ===== Достать имя раскладки по её HKL (например «Русская»/«US») =====
    private static string GetLayoutLabel()
    {
        try
        {
            var tid = GetWindowThreadProcessId(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle, out _);
            var hkl = (uint)GetKeyboardLayout(tid).ToInt64();
            int langId = (int)(hkl & 0xFFFF);
            var name = System.Globalization.CultureInfo.GetCultureInfo(langId).NativeName;
            return name.Contains("рус", StringComparison.OrdinalIgnoreCase) || langId == 0x0419
                ? "RU" : "ENG";
        }
        catch { return "ENG"; }
    }
    private readonly List<SeiwUser> _users = new();
    private SeiwUser? _current;      // выбранный пользователь на экране входа
    private DispatcherTimer? _clockTimer;
    private DispatcherTimer _maskTimer = new();
    private DispatcherTimer? _scheduleTimer;
    private List<ScheduleInterval> _schedule = new();
    private bool _showPassword;
    private bool _rendering;         // защита от реентерабельности TextChanged
    private readonly List<string> _typed = new(); // реальные введённые символы
    private int _failedAttempts;                    // подряд неверных попыток ввода пароля

    public MainWindow()
    {
        InitializeComponent();
        _users = UserStore.Load();

        EnsureFirstUser();
        ApplyWallpaper();
        StartClock();

        _schedule = ScheduleStore.Load();
        StartSchedule();

        // Таймер скрытия «показанных» знаков: по истечении знаки снова становятся точками.
        _maskTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _maskTimer.Tick += MaskTimer_Tick;

        Loaded += (_, _) => FocusPassword();

        // Удержание блокировки: при попытке переключиться или увести окно на
        // задний план — возвращаем его на передний план и восстанавливаем фокус.
        // Это делает блокировку рабочего стола надёжной (окно не «уходит»).
        Activated += (_, _) => FocusPassword();
        Deactivated += (_, _) =>
        {
            Topmost = false;
            Topmost = true;
            Activate();
            FocusPassword();
        };

        // Запрет Alt+F4 закрыть окно (блокировку).
        Closing += (_, e) =>
        {
            if (!_allowClose) e.Cancel = true;
        };
    }

    private bool _allowClose;

    // ===== Первичная настройка: при пустой базе создаём администратора =====
    private void EnsureFirstUser()
    {
        if (_users.Count == 0)
        {
            // База пустая — создаём администратора, но пароль и код восстановления
            // пользователь задаёт сам в мастере первого входа.
            var admin = new SeiwUser { Name = "Administrator" };
            admin.ForceChange = true;   // помечаем: ещё не настроен
            _users.Add(admin);
            UserStore.Save(_users);
        }

        _current = _users[0];
        SelectUser(_current);

        // Если пароль ещё не задан (первый вход) — показываем мастер настройки.
        if (string.IsNullOrEmpty(_current.Hash) || _current.ForceChange)
        {
            ShowSetup();
        }
    }

    // ===== Мастер первого входа: задание пароля и кодового слова восстановления =====
    private void ShowSetup()
    {
        SetupPass.Clear(); SetupPass2.Clear(); SetupCode.Clear(); SetupStatus.Text = "";
        LoginRoot.Visibility = Visibility.Collapsed;
        RecoverRoot.Visibility = Visibility.Collapsed;
        ChangePassRoot.Visibility = Visibility.Collapsed;
        SetupRoot.Visibility = Visibility.Visible;
        SetupPass.Focus();
    }

    private void SetupConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        string p1 = SetupPass.Text;
        string p2 = SetupPass2.Text;
        string code = SetupCode.Text.Trim();

        if (p1.Length < 4) { SetupStatus.Text = "Password must be at least 4 characters."; return; }
        if (p1 != p2) { SetupStatus.Text = "Passwords do not match."; return; }
        if (string.IsNullOrEmpty(code)) { SetupStatus.Text = "Set a recovery code."; return; }

        _current.SetPassword(p1);
        _current.SetRecoveryCode(code);
        _current.ForceChange = false;
        UserStore.Save(_users);

        SetupRoot.Visibility = Visibility.Collapsed;
        LoginRoot.Visibility = Visibility.Visible;
        StatusText.Text = "Protection enabled. Sign in with the new password.";
        FocusPassword();
    }

    // ===== Обои: сменные, заполнение, не влияет на раскладку =====
    private void ApplyWallpaper()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "seiw_wallpaper.jpg"),
            Path.Combine(AppContext.BaseDirectory, "seiw_wallpaper.png"),
        };

        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(p);
                bmp.EndInit();
                WallpaperImage.Source = bmp;
                return;
            }
            catch { /* пробуем следующий */ }
        }

        // Нет файла — тёмно-синий градиент как у экрана блокировки Windows.
        var brush = new LinearGradientBrush(
            Color.FromRgb(0x1B, 0x2B, 0x44),
            Color.FromRgb(0x0D, 0x16, 0x27), 90);
        WallpaperImage.Source = GradientToImage(brush);
    }

    private static BitmapSource GradientToImage(LinearGradientBrush brush)
    {
        var rtb = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(brush, null, new System.Windows.Rect(0, 0, 1920, 1080));
        }
        rtb.Render(dv);
        return rtb;
    }

    // ===== Часы =====
    private void StartClock()
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { UpdateClock(); UpdateKbdLayout(); };
        _clockTimer.Start();
        UpdateClock();
        UpdateKbdLayout();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("HH:mm");
        ClockDate.Text = now.ToString("dddd, d MMMM");
    }

    private void UpdateKbdLayout() => KbdLayout.Text = GetLayoutLabel();

    // ===== Расписание с лимитами времени =====
    private void StartSchedule()
    {
        _schedule = ScheduleStore.Load();
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _scheduleTimer.Tick += (_, _) => CheckSchedule();
        _scheduleTimer.Start();
        CheckSchedule();
    }

    private void ReloadSchedule() => _schedule = ScheduleStore.Load();

    private static bool MatchesInterval(DateTime now, ScheduleInterval iv)
    {
        if (iv.DayOfWeek >= 0 && iv.DayOfWeek != (int)now.DayOfWeek) return false;
        if (!iv.TryGetRange(out var start, out var end)) return false;
        var t = now.TimeOfDay;
        if (start == end) return false;
        // Обработка интервала, пересекающего полночь (start > end).
        return start <= end ? (t >= start && t < end) : (t >= start || t < end);
    }

    private ScheduleInterval? ActiveInterval(DateTime now)
        => _schedule.FirstOrDefault(iv => MatchesInterval(now, iv));

    private bool InLockInterval()
    {
        var active = ActiveInterval(DateTime.Now);
        return active != null && active.Action == "Lock";
    }

    private void CheckSchedule()
    {
        var active = ActiveInterval(DateTime.Now);
        if (active is null) return;

        // Message — показать уведомление на экране.
        if (active.Action == "Message" && !string.IsNullOrEmpty(active.Message))
        {
            StatusText.Text = active.Message;
        }
        // Shutdown — выключить компьютер после короткого предупреждения.
        else if (active.Action == "Shutdown")
        {
            TriggerShutdown();
        }
    }

    private bool _shutdownTriggered;

    private void TriggerShutdown()
    {
        if (_shutdownTriggered) return;
        _shutdownTriggered = true;
        StatusText.Text = "Work time expired. The computer will shut down.";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try { System.Diagnostics.Process.Start("shutdown", "/s /t 0 /f"); }
            catch { _shutdownTriggered = false; }
        };
        t.Start();
    }

    // ===== Выбор пользователя =====
    private void SelectUser(SeiwUser u)
    {
        _current = u;
        AvatarLetter.Text = u.Name.Length > 0 ? u.Name.Substring(0, 1).ToUpper() : "U";
        UserNameLabel.Text = u.Name;
        _typed.Clear();
        PasswordBox.Text = "";
        PasswordBox.CaretIndex = 0;
        StatusText.Text = "";
        FocusPassword();
    }

    private void FocusPassword()
    {
        PasswordBox.Focus();
        PasswordBox.CaretIndex = PasswordBox.Text.Length;
    }

    // ===== Поле пароля: короткий показ знаков при вводе =====
    // Реальные символы хранятся в _typed; на экране последний введённый знак
    // виден короткое время, остальные — точками (как «спящие»).
    private void PasswordBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        foreach (var ch in e.Text)
        {
            if (ch == '\r' || ch == '\n') continue;
            _typed.Add(ch.ToString());
        }
        RenderPassword(true);
    }

    private void PasswordBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DoLogin();
        }
        else if (e.Key == Key.Back && _typed.Count > 0)
        {
            e.Handled = true;
            _typed.RemoveAt(_typed.Count - 1);
            RenderPassword(false);
        }
        else if (e.Key == Key.Delete && _typed.Count > 0)
        {
            e.Handled = true;
            _typed.RemoveAt(_typed.Count - 1);
            RenderPassword(false);
        }
        else if (e.Key == Key.Space)
        {
            e.Handled = true;
            _typed.Add(" ");
            RenderPassword(true);
        }
    }

    private void RenderPassword(bool temporarilyReveal)
    {
        // Временный показ последнего знака отключён: поле всегда показывает
        // точки (кроме явного режима «показать пароль» глазом). Наблюдателю
        // нечего повторять — видимость знаков отсутствует.
        if (_rendering) return;
        _rendering = true;
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _typed.Count; i++)
            {
                bool reveal = _showPassword;
                sb.Append(reveal ? _typed[i] : "●");
            }
            PasswordBox.Text = sb.ToString();
            PasswordBox.CaretIndex = PasswordBox.Text.Length;
        }
        finally
        {
            _rendering = false;
        }
    }

    private void MaskTimer_Tick(object? sender, EventArgs e)
    {
        _maskTimer.Stop();
        RenderPassword(false);
    }

    private void EnterBtn_Click(object sender, RoutedEventArgs e) => DoLogin();

    // ===== «Забыли пароль?» — встроенная форма восстановления =====
    private void ForgotPassBtn_Click(object sender, RoutedEventArgs e)
    {
        RecoveryBox.Clear(); RecoveryNewPass.Clear(); RecoveryConfirmPass.Clear(); RecoveryStatus.Text = "";
        LoginRoot.Visibility = Visibility.Collapsed;
        RecoverRoot.Visibility = Visibility.Visible;
        RecoveryBox.Focus();
    }

    // ===== Подтверждение восстановления пароля по коду =====
    private void RecoveryConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        string code = RecoveryBox.Text.Trim();
        string np = RecoveryNewPass.Text;
        string cp = RecoveryConfirmPass.Text;

        if (!_current.HasRecoveryCode || !_current.CheckRecoveryCode(code))
        {
            RecoveryStatus.Text = "Invalid recovery code.";
            return;
        }
        if (np.Length < 4)
        {
            RecoveryStatus.Text = "New password must be at least 4 characters.";
            return;
        }
        if (np != cp)
        {
            RecoveryStatus.Text = "New password and confirmation do not match.";
            return;
        }

        _current.SetPassword(np);
        _current.ForceChange = false;
        UserStore.Save(_users);
        RecoveryStatus.Text = "Password recovered.";

        RecoverRoot.Visibility = Visibility.Collapsed;
        LoginRoot.Visibility = Visibility.Visible;
        _typed.Clear();
        PasswordBox.Text = "";
        StatusText.Text = "Password recovered. Sign in with the new password.";
        FocusPassword();
    }

    private void RecoveryCancel_Click(object sender, RoutedEventArgs e)
    {
        RecoverRoot.Visibility = Visibility.Collapsed;
        LoginRoot.Visibility = Visibility.Visible;
        PasswordBox.Clear();
        FocusPassword();
    }

    // ===== Настройка расписания =====
    private static readonly string[] DayNames = { "Every day", "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
    private static readonly string[] ActionNames = { "Block (Lock)", "Shut down computer (Shutdown)", "Show message (Message)" };

    // ===== КЧИД: календарно-часовой интерфейс доступа (сетка 7 дней x 24 часа) =====
    private Border[,] _kcidCells = new Border[7, 24]; // [день 0..6][час 0..23]
    private bool[,] _kcidState = new bool[7, 24];
    private const int KcidCellW = 34, KcidCellH = 18;

    private void ScheduleBtn_Click(object sender, RoutedEventArgs e)
    {
        SchedDay.Items.Clear();
        foreach (var d in DayNames) SchedDay.Items.Add(d);
        SchedAction.Items.Clear();
        foreach (var a in ActionNames) SchedAction.Items.Add(a);
        SchedDay.SelectedIndex = 0;
        SchedAction.SelectedIndex = 0;
        SchedStatus.Text = "";
        RefreshScheduleList();

        LoginRoot.Visibility = Visibility.Collapsed;
        ScheduleRoot.Visibility = Visibility.Visible;
    }

    private void RefreshScheduleList()
    {
        SchedList.Items.Clear();
        foreach (var iv in _schedule)
            SchedList.Items.Add(DescribeInterval(iv));
    }

    private static string DescribeInterval(ScheduleInterval iv)
    {
        string day = iv.DayOfWeek < 0 ? "Every day"
            : DayNames[(iv.DayOfWeek % 7) + 1];
        string act = iv.Action switch
        {
            "Lock" => "block",
            "Shutdown" => "shut down",
            _ => iv.Message,
        };
        return $"{day}: {iv.Start}–{iv.End} → {act}";
    }

    private void SchedAdd_Click(object sender, RoutedEventArgs e)
    {
        if (SchedDay.SelectedIndex < 0 || SchedAction.SelectedIndex < 0)
        {
            SchedStatus.Text = "Select a day and an action.";
            return;
        }
        if (!TimeSpan.TryParse(SchedStart.Text, out _) || !TimeSpan.TryParse(SchedEnd.Text, out _))
        {
            SchedStatus.Text = "Enter time in HH:mm format (e.g. 09:00).";
            return;
        }
        var iv = new ScheduleInterval
        {
            DayOfWeek = SchedDay.SelectedIndex == 0 ? -1 : (SchedDay.SelectedIndex - 1) % 7,
            Start = SchedStart.Text.Trim(),
            End = SchedEnd.Text.Trim(),
            Action = SchedAction.SelectedIndex switch { 0 => "Lock", 1 => "Shutdown", _ => "Message" },
            Message = ActionNames[SchedAction.SelectedIndex] + " (\"access limited\")"
        };
        _schedule.Add(iv);
        ScheduleStore.Save(_schedule);
        RefreshScheduleList();
        SchedStatus.Text = "Interval added.";
    }

    private void SchedRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SchedList.SelectedIndex < 0 || SchedList.SelectedIndex >= _schedule.Count)
        {
            SchedStatus.Text = "Select an interval in the list first.";
            return;
        }
        _schedule.RemoveAt(SchedList.SelectedIndex);
        ScheduleStore.Save(_schedule);
        RefreshScheduleList();
        SchedStatus.Text = "Interval removed.";
    }

    private void SchedDone_Click(object sender, RoutedEventArgs e)
    {
        ScheduleRoot.Visibility = Visibility.Collapsed;
        LoginRoot.Visibility = Visibility.Visible;
        PasswordBox.Clear();
        FocusPassword();
    }

    // ===== КЧИД: построение сетки 7 дней x 24 часа =====
    private void BuildKcidGrid()
    {
        KcidGrid.Children.Clear();
        KcidGrid.RowDefinitions.Clear();
        KcidGrid.ColumnDefinitions.Clear();

        for (int r = 0; r < 25; r++) KcidGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KcidCellH) });
        for (int c = 0; c < 8; c++) KcidGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c == 0 ? 46 : KcidCellW) });

        // Шапка: часы сверху в первой строке
        var head = new TextBlock { Text = "Hour", Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(head, 0); Grid.SetColumn(head, 0); KcidGrid.Children.Add(head);
        for (int h = 0; h < 24; h++)
        {
            var t = new TextBlock { Text = h.ToString("00"), Foreground = Brushes.White, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, 0); Grid.SetColumn(t, h + 1); KcidGrid.Children.Add(t);
        }

        // Слева названия дней, дальше клетки
        string[] days = { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };
        for (int d = 0; d < 7; d++)
        {
            var lbl = new TextBlock { Text = days[d], Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lbl, d + 1); Grid.SetColumn(lbl, 0); KcidGrid.Children.Add(lbl);

            var dayIdx = d; // локальная копия для замыкания
            for (int h = 0; h < 24; h++)
            {
                var cell = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x33, 0x3A, 0x55)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x2B, 0x2B, 0x40, 0x60)),
                    BorderThickness = new Thickness(0.5),
                    Cursor = Cursors.Hand,
                    Tag = new Point(dayIdx, h)
                };
                cell.MouseLeftButtonDown += KcidCell_Click;
                _kcidCells[dayIdx, h] = cell;
                Grid.SetRow(cell, d + 1); Grid.SetColumn(cell, h + 1); KcidGrid.Children.Add(cell);
            }
        }

        // Залить сохранённым состоянием из расписания
        LoadKcidState();
        RenderKcidCells();
    }

    private void KcidCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is Point p)
        {
            int d = (int)p.X, h = (int)p.Y;
            _kcidState[d, h] = !_kcidState[d, h];
            RenderKcidCells();
        }
    }

    private void RenderKcidCells()
    {
        for (int d = 0; d < 7; d++)
            for (int h = 0; h < 24; h++)
                _kcidCells[d, h].Background = _kcidState[d, h]
                    ? new SolidColorBrush(Color.FromArgb(0x8A, 0x1E, 0x88, 0xE5))
                    : new SolidColorBrush(Color.FromArgb(0x30, 0x33, 0x3A, 0x55));
    }

    // Заполняет _kcidState из текущего расписания (доступ разрешён, где НЕ Lock-интервал)
    private void LoadKcidState()
    {
        for (int d = 0; d < 7; d++)
            for (int h = 0; h < 24; h++) _kcidState[d, h] = true; // по умолчанию доступ разрешён

        foreach (var iv in _schedule)
            if (iv.Action == "Lock" && iv.TryGetRange(out var s, out var en))
                foreach (var d in EnumerateDays(iv.DayOfWeek))
                    MarkInterval(d, s, en, false);
    }

    private static IEnumerable<int> EnumerateDays(int day)
    {
        if (day < 0) { for (int d = 0; d < 7; d++) yield return d; }
        else yield return day;
    }

    private void MarkInterval(int day, TimeSpan s, TimeSpan en, bool value)
    {
        if (s == en) return;
        if (s <= en) { for (int h = (int)s.TotalHours; h < (int)en.TotalHours; h++) _kcidState[day, h] = value; }
        else { for (int h = (int)s.TotalHours; h < 24; h++) _kcidState[day, h] = value; for (int h = 0; h < (int)en.TotalHours; h++) _kcidState[day, h] = value; }
    }

    // Из сетки строим интервалы Lock (где доступ запрещён) и сохраняем расписание
    private void KcidSave_Click(object sender, RoutedEventArgs e)
    {
        var newIntervals = new List<ScheduleInterval>();
        for (int d = 0; d < 7; d++)
        {
            int start = -1;
            for (int h = 0; h <= 24; h++)
            {
                bool locked = h < 24 && !_kcidState[d, h];
                if (locked && start < 0) start = h;
                if (!locked && start >= 0)
                {
                    newIntervals.Add(new ScheduleInterval { DayOfWeek = d, Start = start.ToString("00") + ":00", End = h.ToString("00") + ":00", Action = "Lock", Message = "Access limited" });
                    start = -1;
                }
            }
        }
        _schedule = newIntervals;
        ScheduleStore.Save(_schedule);
        KcidStatus.Text = "Schedule saved (" + newIntervals.Count + " restriction intervals).";
    }

    private void KcidClear_Click(object sender, RoutedEventArgs e)
    {
        for (int d = 0; d < 7; d++)
            for (int h = 0; h < 24; h++) _kcidState[d, h] = true;
        RenderKcidCells();
        KcidStatus.Text = "Grid cleared: access allowed at all times.";
    }

    private void KcidCancel_Click(object sender, RoutedEventArgs e)
    {
        KcidRoot.Visibility = Visibility.Collapsed;
        LoginRoot.Visibility = Visibility.Visible;
        PasswordBox.Clear();
        FocusPassword();
    }

    // Открывает КЧИД из панели расписания
    private void OpenKcid()
    {
        BuildKcidGrid();
        ScheduleRoot.Visibility = Visibility.Collapsed;
        KcidRoot.Visibility = Visibility.Visible;
        KcidStatus.Text = "";
    }

    private void KcidBtn_Click(object sender, RoutedEventArgs e)
    {
        LoginRoot.Visibility = Visibility.Collapsed;
        OpenKcid();
    }

    private void ShowPasswordBtn_Click(object sender, RoutedEventArgs e)
    {
        _showPassword = !_showPassword;
        ShowPasswordBtn.Content = _showPassword ? "\u2716" : "\U0001F441"; // ✖ / 👁
        RenderPassword(false);
        FocusPassword();
    }

    // ===== Вход с «принципом спящих знаков» =====
    private void DoLogin()
    {
        if (_current is null) return;

        string typed = string.Join("", _typed);

        // Допуск по подпоследовательности живых знаков: из всех введённых
        // символов остаются только те, что участвуют в пароле (живые), и их
        // последовательность сверяется с паролем. Лишние/спящие знаки можно
        // набирать в любом месте и любом порядке — они не мешают. Видимость
        // знаков отсутствует (поле показывает точки), поэтому наблюдатель не
        // знает, какие знаки живые, и не может составить последовательность.
        var liveChars = new HashSet<string>(_current.LiveChars, StringComparer.Ordinal);
        string liveOnly = new string(typed.Where(c => liveChars.Contains(c.ToString())).ToArray());

        var realOk = _current.CheckPassword(liveOnly);

        if (realOk)
        {
            _failedAttempts = 0;
            // В интервале Lock компьютер не разблокируется даже с верным паролем
            // (например, учебные часы или лимит времени работы).
            var active = ActiveInterval(DateTime.Now);
            if (active != null && active.Action == "Lock")
            {
                StatusText.Text = !string.IsNullOrEmpty(active.Message)
                    ? active.Message
                    : "Access limited by schedule. Try again after the interval ends.";
                _typed.Clear();
                PasswordBox.Text = "";
                FocusPassword();
                return;
            }

            // Правильный доступ — принудительная смена при первом входе.
            if (_current.ForceChange)
            {
                _current.ForceChange = false;
                UserStore.Save(_users);
                ShowChangePass(_current);
            }
            else
            {
                Unlock();
            }
        }
        else
        {
            _failedAttempts++;
            if (_failedAttempts >= 5)
            {
                // Слишком много неверных попыток: блокируем поле на 30 секунд.
                StatusText.Text = "Too many failed attempts. Try again in 30 seconds.";
                _typed.Clear();
                PasswordBox.Text = "";
                PasswordBox.IsEnabled = false;
                EnterBtn.IsEnabled = false;
                var unlock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                unlock.Tick += (_, _) =>
                {
                    unlock.Stop();
                    _failedAttempts = 0;
                    PasswordBox.IsEnabled = true;
                    EnterBtn.IsEnabled = true;
                    StatusText.Text = "";
                    FocusPassword();
                };
                unlock.Start();
            }
            else
            {
                StatusText.Text = "Wrong password. Try again.";
                _typed.Clear();
                PasswordBox.Text = "";
                FocusPassword();
            }
        }
    }

    /// <summary>Правильный вход: снимаем блокировку — закрываем окно, открывая рабочий стол.</summary>
    private void Unlock()
    {
        // Небольшая пауза, чтобы пользователь увидел подтверждение, затем снять блокировку.
        StatusText.Text = "Access granted";
        EnterBtn.IsEnabled = false;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) => { t.Stop(); _allowClose = true; Close(); };
        t.Start();
    }

    // ===== СМЕНА ПАРОЛЯ (как в Windows) =====
    private void ShowChangePass(SeiwUser u)
    {
        ChangeUserRun.Text = u.Name;
        CurPass.Clear(); NewPass.Clear(); ConfirmPass.Clear(); ChangeStatus.Text = "";
        LoginRoot.Visibility = Visibility.Collapsed;
        ChangePassRoot.Visibility = Visibility.Visible;
        CurPass.Focus();
    }

    private void ChangePassCancel_Click(object sender, RoutedEventArgs e)
    {
        LoginRoot.Visibility = Visibility.Visible;
        ChangePassRoot.Visibility = Visibility.Collapsed;
        FocusPassword();
    }

    private void ChangePassConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        string cur = CurPass.Text;
        string np = NewPass.Text;
        string cp = ConfirmPass.Text;

        if (!_current.CheckPassword(cur))
        {
            ChangeStatus.Text = "Current password is incorrect.";
            return;
        }
        if (np.Length < 4)
        {
            ChangeStatus.Text = "New password must be at least 4 characters.";
            return;
        }
        if (np != cp)
        {
            ChangeStatus.Text = "New password and confirmation do not match.";
            return;
        }

        _current.SetPassword(np);
        UserStore.Save(_users);
        ChangeStatus.Text = "Password changed.";

        // Возврат на экран входа.
        LoginRoot.Visibility = Visibility.Visible;
        ChangePassRoot.Visibility = Visibility.Collapsed;
        _typed.Clear();
        PasswordBox.Text = "";
        StatusText.Text = "Password changed. Sign in with the new password.";
        FocusPassword();
    }
}