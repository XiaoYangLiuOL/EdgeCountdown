using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("贴边倒计时")]
[assembly: AssemblyDescription("Windows 11 桌面倒计时")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8")]

internal static class Countdown
{
    [STAThread]
    private static void Main()
    {
        var application = new Application();
        var assembly = Assembly.GetExecutingAssembly();
        using (var layout = assembly.GetManifestResourceStream("TimerWindow.xaml"))
        {
            var window = (Window)XamlReader.Load(layout);
            var controller = new TimerController(window);
            using (var image = Drawing.Icon.ExtractAssociatedIcon(assembly.Location) ??
                               (Drawing.Icon)Drawing.SystemIcons.Application.Clone())
            using (var menu = new Forms.ContextMenuStrip())
            using (var tray = new Forms.NotifyIcon { Icon = image, Text = window.Title, ContextMenuStrip = menu })
            {
                menu.Items.Add("显示计时器", null, delegate { controller.ShowFromTray(); });
                menu.Items.Add("退出", null, delegate { controller.ExitFromTray(); });
                tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
                {
                    if (e.Button == Forms.MouseButtons.Left)
                        controller.ShowFromTray();
                };
                tray.Visible = true;
                application.Run(window);
            }
        }
    }
}

internal sealed class TimerController
{
    private const string InputHint = "1.39 = 1 小时 39 分钟";
    private static readonly Size MiniSize = new Size(152, 42);
    private static readonly Size DockSize = new Size(152, 18);

    private enum State { Ready, Running, Paused, Finished }

    private readonly Window window;
    private readonly TextBox durationBox;
    private readonly Button startButton;
    private readonly Button pauseButton;
    private readonly Button miniModeButton;
    private readonly Button miniView;
    private readonly ToggleButton pin;
    private readonly FrameworkElement fullView;
    private readonly FrameworkElement entryControls;
    private readonly FrameworkElement runControls;
    private readonly TextBlock timeText;
    private readonly TextBlock statusText;
    private readonly TextBlock hintText;
    private readonly TextBlock miniTime;
    private readonly ProgressBar progress;
    private readonly ProgressBar dockProgress;
    private readonly Stopwatch elapsed = new Stopwatch();
    private readonly DispatcherTimer refresh;
    private readonly DispatcherTimer savePreferences;
    private readonly TimerPreferences preferences;
    private readonly Size fullSize;
    private Point? miniDragOrigin;
    private bool miniDragging;
    private bool noticeOpen;
    private bool exitRequested;
    private TimeSpan duration;
    private State state;

    internal TimerController(Window window)
    {
        this.window = window;
        durationBox = (TextBox)window.FindName("DurationBox");
        startButton = (Button)window.FindName("StartButton");
        pauseButton = (Button)window.FindName("PauseButton");
        miniModeButton = (Button)window.FindName("MiniModeButton");
        miniView = (Button)window.FindName("MiniView");
        pin = (ToggleButton)window.FindName("PinButton");
        fullView = (FrameworkElement)window.FindName("FullView");
        fullSize = new Size(window.Width, window.Height);
        entryControls = (FrameworkElement)window.FindName("EntryControls");
        runControls = (FrameworkElement)window.FindName("RunControls");
        timeText = (TextBlock)window.FindName("TimeText");
        statusText = (TextBlock)window.FindName("StatusText");
        hintText = (TextBlock)window.FindName("HintText");
        miniTime = (TextBlock)window.FindName("MiniTime");
        progress = (ProgressBar)window.FindName("Progress");
        dockProgress = (ProgressBar)window.FindName("DockProgress");
        refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        refresh.Tick += delegate { RefreshCountdown(); };

        preferences = TimerPreferences.Load();
        if (preferences.DurationText != null)
            durationBox.Text = preferences.DurationText;
        pin.IsChecked = preferences.Topmost ?? true;
        savePreferences = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        savePreferences.Tick += delegate { SavePreferences(); };
        pin.Checked += delegate { QueuePreferencesSave(); };
        pin.Unchecked += delegate { QueuePreferencesSave(); };

        window.SetBinding(Window.TopmostProperty, new Binding("IsChecked") { Source = pin });
        window.SourceInitialized += delegate { WorkAreaSnap.Attach(window, OnWindowDocked); };
        window.Loaded += OnLoaded;
        window.Closing += OnClosing;
        window.Closed += delegate { refresh.Stop(); };
        ((Button)window.FindName("CloseButton")).Click += delegate { HideToTray(); };
        ((Button)window.FindName("ResetButton")).Click += delegate { Reset(); };
        startButton.Click += delegate { Start(); };
        pauseButton.Click += delegate { TogglePause(); };
        miniModeButton.Click += delegate { SetMiniMode(true); };
        miniView.Click += delegate { SetMiniMode(false); };
        miniView.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
        {
            miniDragOrigin = e.GetPosition(window);
        };
        miniView.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
        {
            if (miniDragging)
            {
                e.Handled = true;
                WorkAreaSnap.MoveFromPointer(window, miniDragOrigin.Value);
                miniView.ReleaseMouseCapture();
            }
        };
        miniView.LostMouseCapture += delegate
        {
            bool wasDragging = miniDragging;
            miniDragOrigin = null;
            miniDragging = false;
            if (wasDragging && miniView.IsVisible)
            {
                WorkAreaSnap.SnapOnDrop(window);
                UpdateMiniAppearance();
            }
        };
        miniView.PreviewMouseMove += OnMiniMouseMove;
        durationBox.TextChanged += delegate
        {
            SetHint(InputHint, "Muted");
            QueuePreferencesSave();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        window.Loaded -= OnLoaded;
        WorkAreaSnap.Restore(window, preferences.Placement);
        FocusDuration();
        window.LocationChanged += delegate { QueuePreferencesSave(); };
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (exitRequested)
        {
            SavePreferences();
            return;
        }

        e.Cancel = true;
        // WPF completes the canceled close before the window can be hidden.
        window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(HideToTray));
    }

    private void HideToTray()
    {
        if (exitRequested || !window.IsVisible)
            return;

        SavePreferences();
        window.Hide();
    }

    internal void ShowFromTray()
    {
        if (noticeOpen)
            return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (!window.IsVisible)
            window.Show();
        if (miniView.IsVisible)
            SetMiniMode(false);
        window.Activate();
    }

    internal void ExitFromTray()
    {
        if (noticeOpen)
            return;

        exitRequested = true;
        window.Close();
    }

    private void QueuePreferencesSave()
    {
        savePreferences.Stop();
        savePreferences.Start();
    }

    private void SavePreferences()
    {
        savePreferences.Stop();
        preferences.DurationText = durationBox.Text;
        preferences.Topmost = pin.IsChecked == true;
        if (window.IsVisible && window.WindowState == WindowState.Normal)
            preferences.Placement = WorkAreaSnap.Capture(window) ?? preferences.Placement;
        preferences.Save();
    }

    private void SetMiniMode(bool mini)
    {
        fullView.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        miniView.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;

        if (mini)
        {
            UpdateMiniAppearance();
            miniView.Focus();
        }
        else
        {
            WorkAreaSnap.ResizeKeepingEdge(window, fullSize);
            if (state == State.Running || state == State.Paused)
                pauseButton.Focus();
            else
                FocusDuration();
        }
    }

    private void OnWindowDocked()
    {
        if (fullView.IsVisible && (state == State.Running || state == State.Paused) && WorkAreaSnap.IsDocked(window))
            SetMiniMode(true);
    }

    private void UpdateMiniAppearance()
    {
        bool docked = WorkAreaSnap.IsDocked(window);
        if (!docked && window.Height != MiniSize.Height)
        {
            WorkAreaSnap.ResizeKeepingEdge(window, MiniSize);
            docked = WorkAreaSnap.IsDocked(window);
        }
        if (docked && window.Height != DockSize.Height)
            WorkAreaSnap.ResizeKeepingEdge(window, DockSize);

        miniTime.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        dockProgress.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMiniMouseMove(object sender, MouseEventArgs e)
    {
        if (!miniDragOrigin.HasValue || e.LeftButton != MouseButtonState.Pressed)
            return;

        Vector distance = e.GetPosition(window) - miniDragOrigin.Value;
        if (!miniDragging &&
            Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        miniDragging = true;
        e.Handled = true;
        WorkAreaSnap.MoveFromPointer(window, miniDragOrigin.Value);
    }

    private void Start()
    {
        string error;
        if (!DurationInput.TryParse(durationBox.Text, out duration, out error))
        {
            SetHint(error, "Warning");
            FocusDuration();
            return;
        }

        elapsed.Restart();
        ShowState(State.Running);
        RefreshCountdown();
        refresh.Start();
        SetMiniMode(true);
    }

    private void TogglePause()
    {
        if (state == State.Running)
        {
            elapsed.Stop();
            refresh.Stop();
            RefreshCountdown();
            if (state == State.Running)
                ShowState(State.Paused);
        }
        else if (state == State.Paused)
        {
            elapsed.Start();
            ShowState(State.Running);
            refresh.Start();
        }
    }

    private void Reset()
    {
        refresh.Stop();
        elapsed.Reset();
        duration = TimeSpan.Zero;
        timeText.Text = FormatTime(0);
        progress.Value = 0;
        ShowState(State.Ready);
        FocusDuration();
    }

    private void RefreshCountdown()
    {
        double remaining = Math.Max(0, (duration - elapsed.Elapsed).TotalMinutes);
        string display = FormatTime((int)Math.Ceiling(remaining));
        if (timeText.Text != display)
            timeText.Text = display;
        progress.Value = remaining / duration.TotalMinutes;

        if (remaining == 0)
        {
            elapsed.Stop();
            refresh.Stop();
            ShowState(State.Finished);
            noticeOpen = true;
            try { CompletionNotice.Show(window); }
            finally { noticeOpen = false; }
        }
    }

    private void ShowState(State next)
    {
        bool active = next == State.Running || next == State.Paused;
        state = next;
        entryControls.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        runControls.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        miniModeButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        startButton.IsDefault = !active;
        startButton.Content = next == State.Finished ? "再开始" : "开始";
        pauseButton.Content = next == State.Paused ? "继续" : "暂停";
        timeText.Foreground = Brush(next == State.Finished ? "Accent" : "Text");
        statusText.Foreground = Brush(next == State.Running || next == State.Finished ? "Accent" : "Muted");
        dockProgress.Background = Brush(next == State.Finished ? "Warning" : "Track");

        switch (next)
        {
            case State.Running:
                statusText.Text = "倒计时中";
                break;
            case State.Paused:
                statusText.Text = "已暂停";
                break;
            case State.Finished:
                statusText.Text = "时间到";
                break;
            default:
                statusText.Text = "就绪";
                break;
        }

        SetHint(active ? "拖到屏幕边缘，松手后收为进度条" : InputHint, "Muted");
    }

    private void FocusDuration()
    {
        durationBox.Focus();
        durationBox.SelectAll();
    }

    private void SetHint(string message, string color)
    {
        hintText.Text = message;
        hintText.Foreground = Brush(color);
    }

    private Brush Brush(string key)
    {
        return (Brush)window.Resources[key];
    }

    private static string FormatTime(int minutes)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", minutes / 60, minutes % 60);
    }
}

internal static class CompletionNotice
{
    private const uint IconInformation = 0x00000040;
    private const uint SetForeground = 0x00010000;
    private const uint Topmost = 0x00040000;

    internal static void Show(Window owner)
    {
        MessageBox(new WindowInteropHelper(owner).Handle, "倒计时已结束。", "时间到 · 贴边倒计时",
            IconInformation | SetForeground | Topmost);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string message, string title, uint options);
}

internal static class DurationInput
{
    private const int MaxMinutes = 99 * 60 + 59;
    private static readonly Regex Pattern = new Regex(@"\A([0-9]+)(?:\.([0-9]{1,2}))?\z");
    private static readonly Regex FullWidth = new Regex("[０-９．]");

    internal static bool TryParse(string text, out TimeSpan duration, out string error)
    {
        duration = TimeSpan.Zero;
        string normalized = FullWidth.Replace(text.Trim(), digitOrDot => ((char)(digitOrDot.Value[0] - 0xFEE0)).ToString());
        Match match = Pattern.Match(normalized);
        if (!match.Success)
        {
            error = "请输入分钟，或输入 1.39 这样的时长";
            return false;
        }

        int leading;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out leading))
        {
            error = "最长可计时 99 小时 59 分钟";
            return false;
        }

        int totalMinutes = leading;
        if (match.Groups[2].Success)
        {
            int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (minutes >= 60)
            {
                error = "小数点后的分钟应为 0 到 59";
                return false;
            }

            if (leading > 99)
            {
                error = "最长可计时 99 小时 59 分钟";
                return false;
            }
            totalMinutes = leading * 60 + minutes;
        }

        if (totalMinutes == 0)
        {
            error = "请至少输入 1 分钟";
            return false;
        }
        if (totalMinutes > MaxMinutes)
        {
            error = "最长可计时 99 小时 59 分钟";
            return false;
        }

        duration = TimeSpan.FromMinutes(totalMinutes);
        error = null;
        return true;
    }
}

internal static class WorkAreaSnap
{
    private const int EnterSizeMove = 0x0231;
    private const int ExitSizeMove = 0x0232;
    private const int CornerPreference = 33;
    private const int RoundSmall = 3;
    private const int BorderColor = 34;
    private const int OutlineColorRef = 0x00534135;
    private const uint NearestMonitor = 2;
    private const int SnapDistanceDip = 10;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    internal static void Attach(Window window, Action onDocked)
    {
        var source = (HwndSource)PresentationSource.FromVisual(window);
        int corners = RoundSmall;
        int outline = OutlineColorRef;
        DwmSetWindowAttribute(source.Handle, CornerPreference, ref corners, sizeof(int));
        DwmSetWindowAttribute(source.Handle, BorderColor, ref outline, sizeof(int));
        NativeRect dragStart = new NativeRect();
        bool dragStarted = false;
        source.AddHook(delegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == EnterSizeMove)
                dragStarted = GetWindowRect(hwnd, out dragStart);
            else if (message == ExitSizeMove && dragStarted)
            {
                dragStarted = false;
                NativeRect rect;
                if (GetWindowRect(hwnd, out rect) &&
                    (rect.Left != dragStart.Left || rect.Top != dragStart.Top) &&
                    SnapOnDrop(hwnd, rect))
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Input, onDocked);
            }
            return IntPtr.Zero;
        });
    }

    internal static bool IsDocked(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        NativeRect rect;
        MonitorInfo monitor;
        if (!GetWindowRect(hwnd, out rect) || !TryGetMonitorInfo(MonitorFromWindow(hwnd, NearestMonitor), out monitor))
            return false;

        NativeRect work = monitor.Work;
        return SameEdge(rect.Left, work.Left) || SameEdge(rect.Right, work.Right) ||
               SameEdge(rect.Top, work.Top) || SameEdge(rect.Bottom, work.Bottom);
    }

    internal static SavedPlacement Capture(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        NativeRect rect;
        MonitorInfo monitor;
        if (!GetWindowRect(hwnd, out rect) || !TryGetMonitorInfo(MonitorFromWindow(hwnd, NearestMonitor), out monitor))
            return null;

        double scale = GetDpiForWindow(hwnd) / 96.0;
        NativeRect work = monitor.Work;
        return new SavedPlacement
        {
            Monitor = monitor.Device,
            LeftDip = SameEdge(rect.Left, work.Left) ? 0 : Math.Max(0, rect.Left - work.Left) / scale,
            TopDip = SameEdge(rect.Top, work.Top) ? 0 : Math.Max(0, rect.Top - work.Top) / scale,
            OnRight = SameEdge(rect.Right, work.Right),
            OnBottom = SameEdge(rect.Bottom, work.Bottom)
        };
    }

    internal static void Restore(Window window, SavedPlacement saved)
    {
        if (saved == null)
            return;

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        NativeRect rect;
        MonitorInfo monitor;
        if (!GetWindowRect(hwnd, out rect) || !TryGetMonitorInfo(MonitorFromWindow(hwnd, NearestMonitor), out monitor))
            return;

        NativeRect work = monitor.Work;
        MonitorInfo savedMonitor;
        if (!string.IsNullOrEmpty(saved.Monitor) &&
            !string.Equals(saved.Monitor, monitor.Device, StringComparison.OrdinalIgnoreCase) &&
            TryGetMonitorInfo(FindMonitor(saved.Monitor), out savedMonitor))
        {
            work = savedMonitor.Work;
            // Let WPF update the window's DPI on the saved monitor before aligning its edges.
            SetWindowPos(hwnd, IntPtr.Zero,
                work.Left + Math.Max(0, (work.Width - rect.Width) / 2),
                work.Top + Math.Max(0, (work.Height - rect.Height) / 2),
                0, 0, NoSize | NoZOrder | NoActivate);
            if (!GetWindowRect(hwnd, out rect))
                return;
        }

        double scale = GetDpiForWindow(hwnd) / 96.0;
        int horizontalSpace = Math.Max(0, work.Width - rect.Width);
        int verticalSpace = Math.Max(0, work.Height - rect.Height);
        int x = saved.OnRight ? horizontalSpace : RestoreOffset(saved.LeftDip, scale, horizontalSpace);
        int y = saved.OnBottom ? verticalSpace : RestoreOffset(saved.TopDip, scale, verticalSpace);
        SetWindowPos(hwnd, IntPtr.Zero, work.Left + x, work.Top + y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    internal static void ResizeKeepingEdge(Window window, Size size)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        NativeRect current;
        MonitorInfo monitor;
        if (GetWindowRect(hwnd, out current) &&
            TryGetMonitorInfo(MonitorFromWindow(hwnd, NearestMonitor), out monitor))
        {
            NativeRect work = monitor.Work;
            double scale = GetDpiForWindow(hwnd) / 96.0;
            int width = (int)Math.Round(size.Width * scale);
            int height = (int)Math.Round(size.Height * scale);
            int rightmost = work.Right - width;
            int bottommost = work.Bottom - height;
            // Grow toward the interior near an edge, keeping any gap left by a drag.
            int x = SameEdge(current.Right, work.Right) || current.Left > rightmost ? current.Right - width : current.Left;
            int y = SameEdge(current.Bottom, work.Bottom) || current.Top > bottommost ? current.Bottom - height : current.Top;
            x = Math.Max(work.Left, Math.Min(x, rightmost));
            y = Math.Max(work.Top, Math.Min(y, bottommost));

            // Move and resize together so an attached window stays on its current monitor.
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, NoZOrder | NoActivate);
        }
        window.Width = size.Width;
        window.Height = size.Height;
    }

    internal static void MoveFromPointer(Window window, Point grabPoint)
    {
        NativePoint cursor;
        if (!GetCursorPos(out cursor))
            return;

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        uint dpi = GetDpiForWindow(hwnd);
        int x = cursor.X - (int)Math.Round(grabPoint.X * dpi / 96.0);
        int y = cursor.Y - (int)Math.Round(grabPoint.Y * dpi / 96.0);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    internal static void SnapOnDrop(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        NativeRect rect;
        if (GetWindowRect(hwnd, out rect))
            SnapOnDrop(hwnd, rect);
    }

    private static bool SnapOnDrop(IntPtr hwnd, NativeRect rect)
    {
        NativePoint cursor;
        IntPtr handle = GetCursorPos(out cursor)
            ? MonitorFromPoint(cursor, NearestMonitor)
            : MonitorFromWindow(hwnd, NearestMonitor);
        MonitorInfo monitor;
        if (!TryGetMonitorInfo(handle, out monitor))
            return false;

        NativeRect work = monitor.Work;
        int threshold = (int)Math.Round(SnapDistanceDip * GetDpiForWindow(hwnd) / 96.0);
        int? horizontal = SnapOffset(rect.Left - work.Left, work.Right - rect.Right, threshold);
        int? vertical = SnapOffset(rect.Top - work.Top, work.Bottom - rect.Bottom, threshold);
        if (!horizontal.HasValue && !vertical.HasValue)
            return false;

        int dx = horizontal.GetValueOrDefault();
        int dy = vertical.GetValueOrDefault();
        return (dx == 0 && dy == 0) ||
            SetWindowPos(hwnd, IntPtr.Zero, rect.Left + dx, rect.Top + dy, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    private static bool SameEdge(int windowEdge, int workAreaEdge)
    {
        return Math.Abs(windowEdge - workAreaEdge) <= 1;
    }

    private static int RestoreOffset(double distance, double scale, int available)
    {
        if (double.IsNaN(distance))
            return 0;
        return (int)Math.Round(Math.Max(0, Math.Min(available, distance * scale)));
    }

    private static IntPtr FindMonitor(string device)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, IntPtr rect, IntPtr data)
        {
            MonitorInfo info;
            if (TryGetMonitorInfo(handle, out info) && string.Equals(info.Device, device, StringComparison.OrdinalIgnoreCase))
            {
                found = handle;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool TryGetMonitorInfo(IntPtr monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static int? SnapOffset(int startGap, int endGap, int threshold)
    {
        // Negative gaps mean the window has already crossed the edge.
        if (startGap <= threshold && (endGap > threshold || Math.Abs(startGap) <= Math.Abs(endGap)))
            return -startGap;
        return endGap <= threshold ? (int?)endGap : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Screen, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
