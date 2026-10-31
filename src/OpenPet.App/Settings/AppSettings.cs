using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace OpenPet.App.Settings;

/// <summary>用户设置：独立于 pet.jsonc，保存在 %LocalAppData%\OpenPet\settings.json。</summary>
public sealed class AppSettings
{
    public const string DefaultStateUrl = "http://127.0.0.1:18900";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int Version { get; set; } = 1;

    // —— 连接 ——
    public string StateUrl { get; set; } = DefaultStateUrl;
    public int PollSeconds { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 3;
    public int BackoffSeconds { get; set; } = 15;

    // —— 显示 ——
    public string NameOverride { get; set; } = "";
    public bool UseRemoteName { get; set; } = true;
    public bool ShowStatusCapsule { get; set; } = true;
    public bool ShowCover { get; set; } = true;
    public bool ShowBiliProgress { get; set; } = true;
    public bool ShowNextProactive { get; set; } = true;
    public bool ShowChatter { get; set; } = true;
    public bool ShowDaysTogether { get; set; } = true;
    public bool ShowIdleDetail { get; set; } = true;

    // —— 外观 ——
    public int PetSize { get; set; } = 170;
    public int MotionRangeX { get; set; } = 75;
    public double WalkSpeedScale { get; set; } = 1.0;

    // —— 行为 ——
    public bool WalkingEnabled { get; set; } = true;
    public double WalkChance { get; set; } = 1.0;
    public bool EdgeRestEnabled { get; set; } = true;
    public int EdgeRestMinSeconds { get; set; } = 60;
    public int EdgeRestMaxSeconds { get; set; } = 180;
    public bool RespectRemoteSleeping { get; set; } = true;
    public bool Topmost { get; set; } = true;

    // —— 启动 ——
    public bool AutoStart { get; set; }
    public string DisplayTarget { get; set; } = "";

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPet");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load(AppSettings defaults, out List<string> warnings)
    {
        var notes = new List<string>();
        string path = FilePath;
        AppSettings settings;

        if (!File.Exists(path))
        {
            settings = defaults.Clone();
            settings.Clamp(notes);
            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                notes.Add("创建设置文件失败：" + ex.Message);
            }

            warnings = notes;
            return settings;
        }

        try
        {
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? defaults.Clone();
            FillMissing(settings, doc.RootElement, defaults);
        }
        catch (Exception ex)
        {
            string backup = path + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            try
            {
                File.Copy(path, backup, overwrite: true);
            }
            catch
            {
                // 备份失败也不能阻止程序启动。
            }

            notes.Add($"设置文件损坏，已备份到 {backup}：" + ex.Message);
            settings = defaults.Clone();
        }

        settings.Clamp(notes);
        warnings = notes;
        return settings;
    }

    /// <summary>JSON 里缺字段时用 pet.jsonc 的当前值补齐。</summary>
    private static void FillMissing(AppSettings settings, JsonElement root, AppSettings defaults)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            present.Add(property.Name);
        }

        foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            if (!present.Contains(property.Name))
            {
                property.SetValue(settings, property.GetValue(defaults));
            }
        }
    }

    public void Save()
    {
        string path = FilePath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        string tmp = path + ".tmp";
        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(tmp, json);
        if (File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, null);
            }
            catch
            {
                File.Move(tmp, path, overwrite: true);
            }
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public AppSettings Clone() => new()
    {
        Version = Version,
        StateUrl = StateUrl,
        PollSeconds = PollSeconds,
        TimeoutSeconds = TimeoutSeconds,
        BackoffSeconds = BackoffSeconds,
        NameOverride = NameOverride,
        UseRemoteName = UseRemoteName,
        ShowStatusCapsule = ShowStatusCapsule,
        ShowCover = ShowCover,
        ShowBiliProgress = ShowBiliProgress,
        ShowNextProactive = ShowNextProactive,
        ShowChatter = ShowChatter,
        ShowDaysTogether = ShowDaysTogether,
        ShowIdleDetail = ShowIdleDetail,
        PetSize = PetSize,
        MotionRangeX = MotionRangeX,
        WalkSpeedScale = WalkSpeedScale,
        WalkingEnabled = WalkingEnabled,
        WalkChance = WalkChance,
        EdgeRestEnabled = EdgeRestEnabled,
        EdgeRestMinSeconds = EdgeRestMinSeconds,
        EdgeRestMaxSeconds = EdgeRestMaxSeconds,
        RespectRemoteSleeping = RespectRemoteSleeping,
        Topmost = Topmost,
        AutoStart = AutoStart,
        DisplayTarget = DisplayTarget,
    };

    /// <summary>把所有数值夹到允许范围内；越界时写入一条 warning。</summary>
    public void Clamp(List<string> warnings)
    {
        Version = Version <= 0 ? 1 : Version;

        string url = (StateUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            warnings.Add($"设置项 stateUrl 不合法（{StateUrl}），已回落到 {DefaultStateUrl}。");
            url = DefaultStateUrl;
        }

        StateUrl = url;
        PollSeconds = ClampInt(PollSeconds, 3, 60, "pollSeconds", warnings);
        TimeoutSeconds = ClampInt(TimeoutSeconds, 1, 15, "timeoutSeconds", warnings);
        BackoffSeconds = ClampInt(BackoffSeconds, 5, 120, "backoffSeconds", warnings);

        NameOverride = (NameOverride ?? "").Trim();

        PetSize = ClampInt(PetSize, 100, 320, "petSize", warnings);
        int maxRange = Math.Min(170, Math.Max(0, (320 - PetSize) / 2));
        if (MotionRangeX < 0 || MotionRangeX > maxRange)
        {
            warnings.Add($"设置项 motionRangeX={MotionRangeX} 越界，已夹到 0~{maxRange}。");
            MotionRangeX = Math.Clamp(MotionRangeX, 0, maxRange);
        }

        if (!double.IsFinite(WalkSpeedScale) || WalkSpeedScale < 0.5 || WalkSpeedScale > 2.0)
        {
            warnings.Add($"设置项 walkSpeedScale={WalkSpeedScale} 越界，已夹到 0.5~2.0。");
            WalkSpeedScale = Math.Clamp(double.IsFinite(WalkSpeedScale) ? WalkSpeedScale : 1.0, 0.5, 2.0);
        }

        if (WalkChance < 0 || WalkChance > 1 || double.IsNaN(WalkChance))
        {
            warnings.Add($"设置项 walkChance={WalkChance} 越界，已夹到 0~1。");
            WalkChance = Math.Clamp(double.IsFinite(WalkChance) ? WalkChance : 1, 0, 1);
        }

        EdgeRestMinSeconds = ClampInt(EdgeRestMinSeconds, 10, 600, "edgeRestMinSeconds", warnings);
        EdgeRestMaxSeconds = ClampInt(EdgeRestMaxSeconds, 10, 600, "edgeRestMaxSeconds", warnings);
        if (EdgeRestMaxSeconds < EdgeRestMinSeconds)
        {
            warnings.Add("设置项 edgeRestMaxSeconds 小于 min，已自动对调。");
            (EdgeRestMinSeconds, EdgeRestMaxSeconds) = (EdgeRestMaxSeconds, EdgeRestMinSeconds);
        }

        DisplayTarget = (DisplayTarget ?? "").Trim();
        if (DisplayTarget.Length > 0 &&
            (!int.TryParse(DisplayTarget, out int index) || index < 0 || index > 16))
        {
            warnings.Add($"设置项 displayTarget={DisplayTarget} 不合法，已改为自动。");
            DisplayTarget = "";
        }
    }

    private static int ClampInt(int value, int min, int max, string name, List<string> warnings)
    {
        if (value < min || value > max)
        {
            warnings.Add($"设置项 {name}={value} 越界，已夹到 {min}~{max}。");
            return Math.Clamp(value, min, max);
        }

        return value;
    }
}
