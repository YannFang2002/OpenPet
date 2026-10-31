using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenPet.App.Services;
using OpenPet.App.Settings;
using Forms = System.Windows.Forms;

namespace OpenPet.App;

public partial class SettingsWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly PetRuntime _runtime;
    private bool _loading;

    public SettingsWindow(PetRuntime runtime)
    {
        InitializeComponent();
        _runtime = runtime;
        LoadDisplays();
        Populate(runtime.Settings.Clone());
        SettingsPathText.Text = "设置文件：" + PathMasker.Mask(AppSettings.FilePath);
    }

    private void LoadDisplays()
    {
        DisplayCombo.Items.Clear();
        DisplayCombo.Items.Add(new ComboBoxItem { Content = "自动（非主屏）", Tag = "" });
        Forms.Screen[] screens = Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            Forms.Screen screen = screens[i];
            string primary = screen.Primary ? " 主屏" : "";
            DisplayCombo.Items.Add(new ComboBoxItem
            {
                Content = $"显示器{i + 1} ({screen.Bounds.Width}×{screen.Bounds.Height}{primary})",
                Tag = i.ToString(),
            });
        }
    }

    private void Populate(AppSettings settings)
    {
        _loading = true;
        StateUrlBox.Text = settings.StateUrl;
        UpdateUrlSummary();
        PollBox.Text = settings.PollSeconds.ToString();
        TimeoutBox.Text = settings.TimeoutSeconds.ToString();
        BackoffBox.Text = settings.BackoffSeconds.ToString();

        UseRemoteNameCheck.IsChecked = settings.UseRemoteName;
        NameOverrideBox.Text = settings.NameOverride;
        NameOverrideBox.IsEnabled = settings.UseRemoteName;
        ShowStatusCapsuleCheck.IsChecked = settings.ShowStatusCapsule;
        ShowCoverCheck.IsChecked = settings.ShowCover;
        ShowBiliProgressCheck.IsChecked = settings.ShowBiliProgress;
        ShowNextProactiveCheck.IsChecked = settings.ShowNextProactive;
        ShowChatterCheck.IsChecked = settings.ShowChatter;
        ShowDaysTogetherCheck.IsChecked = settings.ShowDaysTogether;
        ShowIdleDetailCheck.IsChecked = settings.ShowIdleDetail;
        TopmostCheck.IsChecked = settings.Topmost;

        PetSizeSlider.Value = Math.Clamp(settings.PetSize, 100, 320);
        double maxRange = Math.Max(0, (320 - PetSizeSlider.Value) / 2.0);
        MotionRangeSlider.Maximum = maxRange;
        MotionRangeSlider.Value = Math.Clamp(settings.MotionRangeX, 0, maxRange);
        WalkSpeedSlider.Value = Math.Clamp(settings.WalkSpeedScale * 100, 50, 200);
        PetSizeText.Text = $"{PetSizeSlider.Value:0} px · 占 {PetSizeSlider.Value / 320 * 100:0}%";
        MotionRangeText.Text = $"±{MotionRangeSlider.Value:0} px";
        WalkSpeedText.Text = $"{WalkSpeedSlider.Value / 100:0.0}×";
        RangeClampHint.Visibility = Visibility.Collapsed;

        WalkingCheck.IsChecked = settings.WalkingEnabled;
        WalkChanceSlider.Value = Math.Round(Math.Clamp(settings.WalkChance, 0, 1) * 100);
        WalkChanceText.Text = $"{WalkChanceSlider.Value:0}%";
        EdgeRestCheck.IsChecked = settings.EdgeRestEnabled;
        EdgeMinBox.Text = settings.EdgeRestMinSeconds.ToString();
        EdgeMaxBox.Text = settings.EdgeRestMaxSeconds.ToString();
        RespectSleepCheck.IsChecked = settings.RespectRemoteSleeping;

        AutoStartCheck.IsChecked = settings.AutoStart;
        DisplayCombo.SelectedIndex = 0;
        foreach (object item in DisplayCombo.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == settings.DisplayTarget)
            {
                DisplayCombo.SelectedItem = item;
                break;
            }
        }

        ClearErrors();
        TestResultText.Text = "";
        StatusText.Text = "";
        _loading = false;
    }

    private void UseRemoteName_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            NameOverrideBox.IsEnabled = UseRemoteNameCheck.IsChecked == true;
        }
    }

    private void StateUrl_TextChanged(object sender, TextChangedEventArgs e) => UpdateUrlSummary();

    private void UpdateUrlSummary()
    {
        if (StateUrlSummary == null || StateUrlBox == null)
        {
            return;
        }

        string raw = (StateUrlBox.Text ?? "").Trim();
        StateUrlSummary.Text = raw.Length == 0
            ? ""
            : "对外显示：" + PathMasker.MaskUrl(raw);
    }

    private void WalkChance_Changed(object sender, RoutedEventArgs e)
    {
        if (WalkChanceText != null)
        {
            WalkChanceText.Text = $"{WalkChanceSlider.Value:0}%";
        }
    }

    private void PetSize_Changed(object sender, RoutedEventArgs e)
    {
        if (PetSizeText == null || MotionRangeSlider == null)
        {
            return;
        }

        double size = PetSizeSlider.Value;
        PetSizeText.Text = $"{size:0} px · 占 {size / 320 * 100:0}%";

        double maxRange = Math.Max(0, (320 - size) / 2.0);
        double previousRange = MotionRangeSlider.Value;
        MotionRangeSlider.Maximum = maxRange;
        bool clamped = previousRange > maxRange;
        if (clamped)
        {
            MotionRangeSlider.Value = maxRange;
        }

        if (!_loading)
        {
            RangeClampHint.Visibility = clamped ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void MotionRange_Changed(object sender, RoutedEventArgs e)
    {
        if (MotionRangeText != null)
        {
            MotionRangeText.Text = $"±{MotionRangeSlider.Value:0} px";
        }
    }

    private void WalkSpeed_Changed(object sender, RoutedEventArgs e)
    {
        if (WalkSpeedText != null)
        {
            WalkSpeedText.Text = $"{WalkSpeedSlider.Value / 100:0.0}×";
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        ClearErrors();
        string url = (StateUrlBox.Text ?? "").Trim().TrimEnd('/');
        if (!IsHttpUrl(url))
        {
            ShowError(UrlError, "地址必须是 http/https 开头。");
            return;
        }

        TestResultText.Foreground = System.Windows.Media.Brushes.Gray;
        TestResultText.Text = "正在测试…";
        string message;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url + "/health");
            message = response.IsSuccessStatusCode
                ? $"✓ 已连接，{_runtime.DisplayName}在线"
                : $"✗ 连不上：HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            message = "✗ 连不上：" + ex.Message;
        }

        TestResultText.Text = PathMasker.Mask(PathMasker.MaskUrl(message));
        TestResultText.Foreground = message.StartsWith("✓")
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.OrangeRed;
        _runtime.AddLog("测试连接：" + PathMasker.MaskUrl(message));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ClearErrors();
        if (!TryBuild(out AppSettings settings))
        {
            StatusText.Text = "请先修正标红的设置";
            return;
        }

        if (!_runtime.SaveSettings(settings, out string error))
        {
            StatusText.Text = PathMasker.Mask(error);
            return;
        }

        StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        StatusText.Text = "已保存并应用";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            this,
            "恢复默认只会把控件填回默认值，点「保存并应用」后才真正生效。继续吗？",
            "OpenPet 设置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            Populate(new AppSettings());
        }
    }

    private bool TryBuild(out AppSettings settings)
    {
        settings = new AppSettings();
        bool ok = true;

        string url = (StateUrlBox.Text ?? "").Trim().TrimEnd('/');
        if (!IsHttpUrl(url))
        {
            ShowError(UrlError, "地址必须是合法的 http/https URL。");
            ok = false;
        }

        int poll = 0, timeout = 0, backoff = 0;
        if (!int.TryParse(PollBox.Text, out poll) || poll < 3 || poll > 60 ||
            !int.TryParse(TimeoutBox.Text, out timeout) || timeout < 1 || timeout > 15 ||
            !int.TryParse(BackoffBox.Text, out backoff) || backoff < 5 || backoff > 120)
        {
            ShowError(NumberError, "刷新间隔 3~60 秒，超时 1~15 秒，退避 5~120 秒。");
            ok = false;
        }

        int edgeMin = 0, edgeMax = 0;
        if (!int.TryParse(EdgeMinBox.Text, out edgeMin) || edgeMin < 10 || edgeMin > 600 ||
            !int.TryParse(EdgeMaxBox.Text, out edgeMax) || edgeMax < 10 || edgeMax > 600)
        {
            ShowError(EdgeError, "休息时长必须是 10~600 秒。");
            ok = false;
        }
        else if (edgeMin > edgeMax)
        {
            (edgeMin, edgeMax) = (edgeMax, edgeMin);
            EdgeMinBox.Text = edgeMin.ToString();
            EdgeMaxBox.Text = edgeMax.ToString();
        }

        if (!ok)
        {
            return false;
        }

        settings.StateUrl = url;
        settings.PollSeconds = poll;
        settings.TimeoutSeconds = timeout;
        settings.BackoffSeconds = backoff;
        settings.UseRemoteName = UseRemoteNameCheck.IsChecked == true;
        settings.NameOverride = settings.UseRemoteName ? (NameOverrideBox.Text ?? "").Trim() : "";
        settings.ShowStatusCapsule = ShowStatusCapsuleCheck.IsChecked == true;
        settings.ShowCover = ShowCoverCheck.IsChecked == true;
        settings.ShowBiliProgress = ShowBiliProgressCheck.IsChecked == true;
        settings.ShowNextProactive = ShowNextProactiveCheck.IsChecked == true;
        settings.ShowChatter = ShowChatterCheck.IsChecked == true;
        settings.ShowDaysTogether = ShowDaysTogetherCheck.IsChecked == true;
        settings.ShowIdleDetail = ShowIdleDetailCheck.IsChecked == true;
        settings.Topmost = TopmostCheck.IsChecked == true;

        settings.PetSize = (int)Math.Round(PetSizeSlider.Value);
        settings.MotionRangeX = (int)Math.Round(MotionRangeSlider.Value);
        settings.WalkSpeedScale = Math.Clamp(WalkSpeedSlider.Value / 100.0, 0.5, 2.0);

        settings.WalkingEnabled = WalkingCheck.IsChecked == true;
        settings.WalkChance = Math.Clamp(WalkChanceSlider.Value / 100.0, 0, 1);
        settings.EdgeRestEnabled = EdgeRestCheck.IsChecked == true;
        settings.EdgeRestMinSeconds = edgeMin;
        settings.EdgeRestMaxSeconds = edgeMax;
        settings.RespectRemoteSleeping = RespectSleepCheck.IsChecked == true;

        settings.AutoStart = AutoStartCheck.IsChecked == true;
        settings.DisplayTarget = (DisplayCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        settings.Clamp(new System.Collections.Generic.List<string>());
        return true;
    }

    private static bool IsHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void ClearErrors()
    {
        HideError(UrlError);
        HideError(NumberError);
        HideError(EdgeError);
        StatusText.Text = "";
    }

    private static void ShowError(TextBlock block, string message)
    {
        block.Text = message;
        block.Visibility = Visibility.Visible;
    }

    private static void HideError(TextBlock block)
    {
        block.Text = "";
        block.Visibility = Visibility.Collapsed;
    }
}
