using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenPet.App.Models;
using OpenPet.App.Pet;
using OpenPet.App.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace OpenPet.App;

public partial class MainWindow : Window
{
    private readonly PetRuntime _runtime;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _uiTimer;

    private volatile PreviewFrame? _latestPreview;
    private WriteableBitmap? _previewBitmap;
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayStreamItem;
    private Forms.ToolStripMenuItem? _trayShowItem;
    private bool _allowClose;
    private bool _trayAvailable;
    private bool _trayHintShown;
    private bool _uiReady;
    private bool _suppressAutostartEvent = true;
    private LibraryWindow? _libraryWindow;
    private SettingsWindow? _settingsWindow;

    private bool _draggingPreview;
    private double _lastSceneX;
    private double _lastSceneY;
    private double _lastVelocityX;
    private double _lastVelocityY;
    private DateTime _lastPointerAt;

    public MainWindow()
    {
        InitializeComponent();

        _runtime = new PetRuntime();
        _runtime.LogAdded += OnLogAdded;
        _runtime.StatusChanged += OnStatusChanged;
        _runtime.PreviewReady += frame => _latestPreview = frame;
        _runtime.Reloaded += () => Dispatcher.BeginInvoke(new Action(RefreshUi));
        _runtime.SettingsApplied += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            Topmost = _runtime.Settings.Topmost;
            RefreshUi();
        }));

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _previewTimer.Tick += (_, _) => PumpPreview();
        _previewTimer.Start();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _uiTimer.Tick += (_, _) => RefreshUi();

        Closed += (_, _) => ShutdownRuntime();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int rounded = 2;
            DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch
        {
            // 旧版 Windows 没有这些属性，保持系统默认标题栏即可。
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _runtime.Initialize();
            _uiReady = true;
            NameText.Text = _runtime.DisplayName;
            ConfigPathText.Text = PathMasker.Mask(_runtime.ConfigPath);
            ConfigPathText.ToolTip = PathMasker.Mask(_runtime.ConfigPath);

            _suppressAutostartEvent = true;
            AutostartCheck.IsChecked = AutostartService.IsEnabled();
            _suppressAutostartEvent = false;

            InitializeTray();
            _uiTimer.Start();
            RefreshUi();
            Topmost = _runtime.Settings.Topmost;
            SideScroll.ScrollToTop();
            MessageText.Text = "准备就绪。点击宠物会回应，按住可以拖动。";

            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.BeginInvoke(new Action(ShowSettings));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            MessageText.Text = $"启动失败：{PathMasker.Mask(ex.Message)}";
            MessageBox.Show(this, PathMasker.Mask(ex.Message), "OpenPet 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- runtime events

    private void OnStatusChanged(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnStatusChanged(message)));
            return;
        }

        MessageText.Text = PathMasker.Mask(message);
    }

    private void OnLogAdded(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLogAdded(message)));
            return;
        }

        LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {PathMasker.Mask(message)}");
        while (LogList.Items.Count > 220)
        {
            LogList.Items.RemoveAt(0);
        }

        // 不自动 ScrollIntoView：ListBox 的 BringIntoView 会连带把外侧
        // ScrollViewer 滚到底部，导致右上角的宠物卡片被顶出视野。
    }

    private void PumpPreview()
    {
        PreviewFrame? frame = _latestPreview;
        if (frame == null)
        {
            return;
        }

        _latestPreview = null;
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length < frame.Width * frame.Height * 4)
        {
            return;
        }

        if (_previewBitmap == null
            || _previewBitmap.PixelWidth != frame.Width
            || _previewBitmap.PixelHeight != frame.Height)
        {
            _previewBitmap = new WriteableBitmap(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null);
            PreviewImage.Source = _previewBitmap;
        }

        _previewBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Bgra,
            frame.Width * 4,
            0);
    }

    // ---------------------------------------------------------------- ui refresh

    private void RefreshUi()
    {
        if (!_uiReady || _runtime.Engine == null)
        {
            return;
        }

        string displayName = _runtime.DisplayName;
        NameText.Text = displayName;
        Title = $"{displayName} · OpenPet";
        SubtitleText.Text = string.IsNullOrWhiteSpace(displayName)
            ? "OpenPet · 2.8 英寸副屏"
            : $"{displayName} 的 2.8 英寸副屏 · 独立版";
        XiaowaiView? remote = _runtime.RemoteState;
        StatusText.Text = remote is null
            ? $"等待{displayName}状态…"
            : $"{remote.Label} · {remote.State}";

        bool streaming = _runtime.IsStreaming;
        bool connected = _runtime.IsConnected;
        StreamToggle.Content = streaming ? "投屏中" : "投屏关";
        StatusDot.Fill = (Brush)FindResource(connected && streaming ? "Good" : streaming ? "Warn" : "TextMuted");
        DeviceStatusText.Text = !streaming
            ? "仅预览"
            : connected
                ? "屏幕已连接"
                : "未连接 · 仅预览";

        if (_trayStreamItem != null)
        {
            _trayStreamItem.Checked = streaming;
        }

        if (_tray != null)
        {
            _tray.Text = displayName;
        }

        if (_trayShowItem != null)
        {
            _trayShowItem.Text = $"显示{displayName}";
        }

        PerfText.Text = $"渲染 {_runtime.RenderFps} fps · 推送 {_runtime.SendFps} fps";
    }

    // ---------------------------------------------------------------- actions

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _runtime.Reload();
            ConfigPathText.Text = PathMasker.Mask(_runtime.ConfigPath);
            ConfigPathText.ToolTip = PathMasker.Mask(_runtime.ConfigPath);
            MessageText.Text = "配置已重载。";
            RefreshUi();
        }
        catch (Exception ex)
        {
            Log.Write("重载失败：" + ex);
            MessageText.Text = $"重载失败：{PathMasker.Mask(ex.Message)}";
            OnLogAdded($"重载失败：{ex.Message}");
        }
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        _runtime.OpenConfigFile();
        ConfigPathText.Text = PathMasker.Mask(_runtime.ConfigPath);
        ConfigPathText.ToolTip = PathMasker.Mask(_runtime.ConfigPath);
        MessageText.Text = "修改配置后点「重载配置」即可生效。";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPet"));
            Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPet"))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageText.Text = $"打开目录失败：{PathMasker.Mask(ex.Message)}";
        }
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        if (_libraryWindow != null)
        {
            _libraryWindow.Activate();
            return;
        }

        _libraryWindow = new LibraryWindow(_runtime)
        {
            Owner = this,
        };
        _libraryWindow.Closed += (_, _) => _libraryWindow = null;
        _libraryWindow.Show();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ForceClose();

    private void StreamToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        bool enabled = StreamToggle.IsChecked == true;
        _runtime.SetStreaming(enabled);
        MessageText.Text = enabled ? "正在投屏到屏幕…" : "已关闭投屏，仅保留窗口预览。";
        RefreshUi();
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutostartEvent || !_uiReady)
        {
            return;
        }

        bool enabled = AutostartCheck.IsChecked == true;
        bool ok = AutostartService.SetEnabled(enabled);
        MessageText.Text = ok
            ? (enabled ? "已设置开机自启。" : "已取消开机自启。")
            : "设置开机自启失败（注册表不可写）。";
    }

    // ---------------------------------------------------------------- preview input

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryMapPreview(e.GetPosition(PreviewImage), out double x, out double y))
        {
            return;
        }

        if (!_runtime.PointerDown(x, y))
        {
            return;
        }

        _draggingPreview = true;
        _lastSceneX = x;
        _lastSceneY = y;
        _lastVelocityX = 0;
        _lastVelocityY = 0;
        _lastPointerAt = DateTime.UtcNow;
        PreviewImage.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingPreview || !TryMapPreview(e.GetPosition(PreviewImage), out double x, out double y))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        double seconds = Math.Max(0.001, (now - _lastPointerAt).TotalSeconds);
        _lastVelocityX = (x - _lastSceneX) / seconds;
        _lastVelocityY = (y - _lastSceneY) / seconds;
        _lastSceneX = x;
        _lastSceneY = y;
        _lastPointerAt = now;

        _runtime.PointerMove(x, y);
        e.Handled = true;
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingPreview)
        {
            return;
        }

        _draggingPreview = false;
        if (PreviewImage.IsMouseCaptured)
        {
            PreviewImage.ReleaseMouseCapture();
        }

        if (TryMapPreview(e.GetPosition(PreviewImage), out double x, out double y))
        {
            _runtime.PointerUp(x, y, _lastVelocityX, _lastVelocityY);
        }
        else
        {
            _runtime.PointerUp(_lastSceneX, _lastSceneY, 0, 0);
        }

        RefreshUi();
        e.Handled = true;
    }

    private bool TryMapPreview(Point point, out double x, out double y)
    {
        x = 0;
        y = 0;
        double width = PreviewImage.ActualWidth;
        double height = PreviewImage.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return false;
        }

        double scale = Math.Min(width / 320.0, height / 240.0);
        double drawnWidth = 320 * scale;
        double drawnHeight = 240 * scale;
        double offsetX = (width - drawnWidth) / 2;
        double offsetY = (height - drawnHeight) / 2;
        x = (point.X - offsetX) / scale;
        y = (point.Y - offsetY) / scale;
        return x >= 0 && x <= 320 && y >= 0 && y <= 240;
    }

    // ---------------------------------------------------------------- keyboard

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.R)
        {
            Reload_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            ShowSettings();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.L:
                OpenLibrary_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_trayAvailable)
                {
                    Hide();
                }

                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- tray / closing

    private void InitializeTray()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "icon", "app.ico");
            _trayIcon = File.Exists(iconPath)
                ? new Drawing.Icon(iconPath)
                : Drawing.SystemIcons.Application;

            _tray = new Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Visible = true,
                Text = "OpenPet · 屏幕宠物",
            };

            var menu = new Forms.ContextMenuStrip();
            var show = new Forms.ToolStripMenuItem("显示窗口");
            _trayShowItem = show;
            show.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));

            var settings = new Forms.ToolStripMenuItem("设置…");
            settings.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings));

            _trayStreamItem = new Forms.ToolStripMenuItem("投屏到屏幕")
            {
                CheckOnClick = true,
                Checked = _runtime.IsStreaming,
            };
            _trayStreamItem.Click += (_, _) =>
            {
                bool enabled = _trayStreamItem.Checked;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StreamToggle.IsChecked = enabled;
                    _runtime.SetStreaming(enabled);
                    RefreshUi();
                }));
            };

            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ForceClose));

            menu.Items.Add(show);
            menu.Items.Add(settings);
            menu.Items.Add(_trayStreamItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exit);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));

            _trayAvailable = true;
        }
        catch (Exception ex)
        {
            _trayAvailable = false;
            OnLogAdded($"托盘初始化失败：{ex.Message}");
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = _runtime.Settings.Topmost;
    }

    private void SettingsGear_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            _settingsWindow.Topmost = true;
            _settingsWindow.Topmost = false;
            _runtime.AddLog("设置窗口已打开，激活现有窗口（单例）");
            return;
        }

        _settingsWindow = new SettingsWindow(_runtime);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        _runtime.AddLog("打开设置窗口（单例）");
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || !_trayAvailable)
        {
            _uiTimer.Stop();
            _previewTimer.Stop();
            return;
        }

        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloonTip(2500, "OpenPet 还在陪着你", "窗口已隐藏到托盘，宠物仍在屏幕上生活。", Forms.ToolTipIcon.Info);
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void ShutdownRuntime()
    {
        _uiTimer.Stop();
        _previewTimer.Stop();
        _runtime.Dispose();

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        System.Windows.Application.Current.Shutdown();
    }
}
