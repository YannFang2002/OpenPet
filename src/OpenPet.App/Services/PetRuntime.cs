using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenPet.App.Models;
using OpenPet.App.Pet;
using OpenPet.App.Settings;

namespace OpenPet.App.Services;

/// <summary>
/// 把配置、素材、引擎、渲染器与推流服务组装在一起。
/// UI 只和这个门面打交道；初始化/重载过程中的每一行日志都会转发给界面。
/// </summary>
public sealed class PetRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly string _configPathOverride;
    private AnimationLibrary? _library;
    private MemeLibrary? _memes;
    private FrameCache? _frameCache;
    private PetSceneRenderer? _renderer;
    private DisplayService? _display;
    private XiaowaiClient? _xiaowai;
    private System.Threading.Timer? _xiaowaiTimer;
    private DateTime _xiaowaiBackoffUntil = DateTime.MinValue;
    private readonly object _xiaowaiLock = new();
    private string _lastRemoteState = "";
    private bool? _lastSleeping;
    private string? _coverBvid;
    private byte[]? _coverJpeg;
    private int _xiaowaiPolling;
    private bool _initialized;
    private bool _disposed;
    private double _baseEdgeChance = 0.45;

    public PetRuntime(string? configPath = null)
    {
        _configPathOverride = configPath ?? "";
    }

    public event Action<PreviewFrame>? PreviewReady;

    public event Action<string>? StatusChanged;

    public event Action<string>? LogAdded;

    public event Action? Reloaded;

    public event Action? SettingsApplied;

    public AppSettings Settings { get; private set; } = new();

    public string SettingsPath => AppSettings.FilePath;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.NameOverride))
            {
                return Settings.NameOverride.Trim();
            }

            string? remoteName = RemoteState?.Name;
            if (Settings.UseRemoteName && !string.IsNullOrWhiteSpace(remoteName))
            {
                return remoteName.Trim();
            }

            string? name = ActivePet?.Name;
            return string.IsNullOrWhiteSpace(name) ? "蓝毛小女仆" : name.Trim();
        }
    }

    public LoadedPetConfig? Loaded { get; private set; }

    public PetConfig? Config => Loaded?.Config;

    public PetProfile? ActivePet => Loaded?.Pet;

    public PetEngine? Engine { get; private set; }

    public AnimationLibrary? Library => _library;

    public MemeLibrary? Memes => _memes;

    public IReadOnlyList<string> ActionNames => _library == null
        ? Array.Empty<string>()
        : _library.Names.OrderBy(n => n, StringComparer.CurrentCulture).ToArray();

    public IReadOnlyList<string> MemeNames => _memes?.Names ?? Array.Empty<string>();

    public XiaowaiView? RemoteState => Engine?.RemoteState;

    public string ConfigPath => Loaded?.Path ?? PetConfigLoader.ResolveConfigPath(
        string.IsNullOrWhiteSpace(_configPathOverride) ? null : _configPathOverride);

    public string BundledConfigPath => PetConfigLoader.DefaultBundledPath;

    public bool IsConnected => _display?.IsConnected ?? false;

    public bool IsStreaming => _display?.IsStreaming ?? false;

    public int RenderFps => _display?.RenderFps ?? 0;

    public int SendFps => _display?.SendFps ?? 0;

    public string? DeviceSummary => _display?.DeviceSummary;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            var warnings = new System.Collections.Generic.List<string>();
            LoadedPetConfig loaded = PetConfigLoader.Load(
                string.IsNullOrWhiteSpace(_configPathOverride) ? null : _configPathOverride,
                warnings);
            Loaded = loaded;
            _baseEdgeChance = loaded.Config.Animations.Moves.Edge.Chance;

            MoveEdgeConfig edgeDefaults = loaded.Config.Animations.Moves.Edge;
            var settingDefaults = new AppSettings
            {
                StateUrl = loaded.Config.Xiaowai.Url,
                PollSeconds = (int)Math.Round(loaded.Config.Xiaowai.PollSec),
                TimeoutSeconds = (int)Math.Round(loaded.Config.Xiaowai.TimeoutSec),
                BackoffSeconds = (int)Math.Round(loaded.Config.Xiaowai.BackoffSec),
                ShowCover = loaded.Config.Xiaowai.ShowCover,
                ShowChatter = loaded.Config.Chatter.Enabled,
                PetSize = (int)Math.Round(loaded.Pet.Size),
                MotionRangeX = (int)Math.Round(loaded.Config.Display.MotionRangeX),
                WalkSpeedScale = 1.0,
                WalkingEnabled = loaded.Config.AnimationWeights.Move > 0,
                EdgeRestEnabled = edgeDefaults.Enabled,
                EdgeRestMinSeconds = (int)Math.Round(edgeDefaults.RestMinSec),
                EdgeRestMaxSeconds = (int)Math.Round(edgeDefaults.RestMaxSec),
                RespectRemoteSleeping = loaded.Config.Xiaowai.SleepFromRemote,
            };

            var settingsWarnings = new System.Collections.Generic.List<string>();
            Settings = AppSettings.Load(settingDefaults, out settingsWarnings);
            ApplySettingsToConfig(loaded.Config, Settings);

            string animationRoot = Path.Combine(AppContext.BaseDirectory, "assets", "animations");
            var libraryWarnings = new System.Collections.Generic.List<string>();
            _library = AnimationLibrary.Load(animationRoot, libraryWarnings);

            string memeRoot = Path.Combine(AppContext.BaseDirectory, "assets", "memes");
            _memes = MemeLibrary.Load(memeRoot);

            foreach (string warning in warnings)
            {
                AddLog(warning);
            }

            foreach (string warning in libraryWarnings)
            {
                AddLog(warning);
            }

            foreach (string warning in settingsWarnings)
            {
                AddLog(warning);
            }

            Engine = new PetEngine(loaded, _library, _memes, AddLog) { Settings = Settings };
            _frameCache = new FrameCache();
            _renderer = new PetSceneRenderer(_frameCache);
            _renderer.Configure(loaded.Config);

            _display = new DisplayService(Engine, _renderer);
            Subscribe(_display);
            _display.Start();

            if (Engine.Config.Xiaowai.Enabled)
            {
                StartXiaowai();
            }

            _initialized = true;

            AddLog($"OpenPet 启动：{loaded.Pet.Name}（{loaded.Pet.Id}），配置 {loaded.Path}");
            AddLog($"设置文件：{SettingsPath}");
            if (!string.IsNullOrWhiteSpace(Settings.DisplayTarget))
            {
                AddLog($"屏幕选择：{Settings.DisplayTarget}（当前实现仍需重启后生效）");
            }
            AddLog($"动画素材：{_library.Names.Count} 个；表情包：{_memes.Count} 张；" +
                   $"屏幕：{(_display.IsConnected ? "已连接" : "未连接")}");
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            if (!_initialized || _disposed)
            {
                return;
            }

            AddLog("正在重载配置…");

            // 先完整解析/校验，失败时保留旧配置和正在运行的显示线程。
            var warnings = new System.Collections.Generic.List<string>();
            LoadedPetConfig loaded = PetConfigLoader.Load(
                string.IsNullOrWhiteSpace(_configPathOverride) ? null : _configPathOverride,
                warnings);

            string animationRoot = Path.Combine(AppContext.BaseDirectory, "assets", "animations");
            var libraryWarnings = new System.Collections.Generic.List<string>();
            AnimationLibrary library = AnimationLibrary.Load(animationRoot, libraryWarnings);
            ResolvedAnimations.Build(loaded.Config, library, null);

            string memeRoot = Path.Combine(AppContext.BaseDirectory, "assets", "memes");
            MemeLibrary memes = MemeLibrary.Load(memeRoot);

            StopXiaowai(clearState: false);
            _display?.Stop();
            _display?.Dispose();
            _display = null;

            Loaded = loaded;
            _baseEdgeChance = loaded.Config.Animations.Moves.Edge.Chance;
            ApplySettingsToConfig(loaded.Config, Settings);
            _library = library;
            _memes = memes;
            _frameCache ??= new FrameCache();
            _frameCache.Clear();

            Engine?.Reconfigure(loaded, _library, _memes);
            Engine?.ApplySettings(Settings);
            _renderer?.Dispose();
            _renderer = new PetSceneRenderer(_frameCache);
            _renderer.Configure(loaded.Config);

            _display = new DisplayService(Engine!, _renderer);
            Subscribe(_display);
            _display.Start();

            RestartXiaowai(reapplyCover: true);

            foreach (string warning in warnings)
            {
                AddLog(warning);
            }

            foreach (string warning in libraryWarnings)
            {
                AddLog(warning);
            }

            AddLog($"配置已重载：{loaded.Pet.Name}，动画 {_library.Names.Count} 个，表情包 {_memes.Count} 张");
            Reloaded?.Invoke();
        }
    }

    private void StartXiaowai() => RestartXiaowai(reapplyCover: false);

    private void RestartXiaowai(bool reapplyCover)
    {
        StopXiaowai(clearState: false);

        PetConfig? config = Config;
        if (config is null)
        {
            return;
        }

        XiaowaiConfig xw = config.Xiaowai;
        if (!xw.Enabled)
        {
            Engine?.ApplyRemoteState(null, false);
            lock (_xiaowaiLock)
            {
                _lastRemoteState = "";
                _coverBvid = null;
                _coverJpeg = null;
            }

            return;
        }

        var client = new XiaowaiClient(xw.Url, xw.TimeoutSec);
        lock (_xiaowaiLock)
        {
            _xiaowai = client;
            _xiaowaiBackoffUntil = DateTime.MinValue;
        }

        if (reapplyCover)
        {
            string? bvid;
            byte[]? jpeg;
            lock (_xiaowaiLock)
            {
                bvid = _coverBvid;
                jpeg = _coverJpeg;
            }

            if (!string.IsNullOrWhiteSpace(bvid) && jpeg is not null)
            {
                _renderer?.SetCover(bvid, jpeg);
            }
        }

        double periodMs = xw.PollSec * 1000.0;
        _xiaowaiTimer = new System.Threading.Timer(
            _ => _ = PollRemoteAsync(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(periodMs));
        AddLog(DisplayName + "状态已接入：" + PathMasker.MaskUrl(xw.Url) + "（每 " + xw.PollSec + "s）");
    }

    private void StopXiaowai(bool clearState)
    {
        Timer? timer;
        XiaowaiClient? client;
        lock (_xiaowaiLock)
        {
            timer = _xiaowaiTimer;
            _xiaowaiTimer = null;
            client = _xiaowai;
            _xiaowai = null;
            _xiaowaiBackoffUntil = DateTime.MinValue;
        }

        timer?.Dispose();
        client?.Dispose();

        if (!clearState)
        {
            return;
        }

        Engine?.ApplyRemoteState(null, false);
        lock (_xiaowaiLock)
        {
            _lastRemoteState = "";
            _lastSleeping = null;
            _coverBvid = null;
            _coverJpeg = null;
        }
    }

    private async Task PollRemoteAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _xiaowaiPolling, 1) == 1)
        {
            return;
        }

        try
        {
            XiaowaiClient? client;
            DateTime backoffUntil;
            lock (_xiaowaiLock)
            {
                client = _xiaowai;
                backoffUntil = _xiaowaiBackoffUntil;
            }

            if (client is null || DateTime.UtcNow < backoffUntil)
            {
                return;
            }

            XiaowaiView? view = await client.FetchAsync().ConfigureAwait(false);
            lock (_xiaowaiLock)
            {
                if (!ReferenceEquals(_xiaowai, client))
                {
                    return;
                }
            }

            if (view is null)
            {
                XiaowaiConfig failedCfg = Config?.Xiaowai ?? new XiaowaiConfig();
                lock (_xiaowaiLock)
                {
                    _xiaowaiBackoffUntil = DateTime.UtcNow.AddSeconds(failedCfg.BackoffSec);
                }

                AddLog(DisplayName + "状态拉取失败，退避 " + failedCfg.BackoffSec + "s");
                return;
            }

            PetConfig? config = Config;
            if (config is null)
            {
                return;
            }

            XiaowaiConfig cfg = config.Xiaowai;
            bool changed;
            bool sleepChanged;
            lock (_xiaowaiLock)
            {
                changed = view.State != _lastRemoteState;
                _lastRemoteState = view.State;
                sleepChanged = _lastSleeping != view.Sleeping;
                _lastSleeping = view.Sleeping;
            }

            Engine?.ApplyRemoteState(view, cfg.BubbleOnChange);
            if (changed)
            {
                AddLog($"{DisplayName}状态：{view.State}（{view.Label}）{view.Detail}");
            }

            if (sleepChanged)
            {
                AddLog($"{DisplayName}睡眠状态：{(view.Sleeping ? "睡着了" : "醒着")}");
            }

            // 状态切换 → 播一个对应动作（动作名不存在就自然跳过，不报错）。
            if (changed && cfg.Actions.TryGetValue(view.State, out string[]? pool) && pool is { Length: > 0 })
            {
                string name = pool[Random.Shared.Next(pool.Length)];
                Engine?.PlayAction(name);
            }

            // 刷 B 站时拉封面（同一 BVID 只拉一次）。
            if (cfg.ShowCover && view.State == "browsing_bili" &&
                !string.IsNullOrWhiteSpace(view.VideoBvid))
            {
                string bvid = view.VideoBvid!;
                bool needFetch;
                lock (_xiaowaiLock)
                {
                    needFetch = bvid != _coverBvid;
                    if (needFetch)
                    {
                        _coverBvid = bvid;
                        _coverJpeg = null;
                    }
                }

                if (needFetch)
                {
                    byte[]? jpeg = await client.FetchCoverAsync(bvid).ConfigureAwait(false);
                    if (jpeg is not null)
                    {
                        lock (_xiaowaiLock)
                        {
                            if (!ReferenceEquals(_xiaowai, client))
                            {
                                return;
                            }

                            _coverJpeg = jpeg;
                        }

                        _renderer?.SetCover(bvid, jpeg);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog(DisplayName + "状态轮询异常：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _xiaowaiPolling, 0);
        }
    }

    public PetActionSummary PlayAction(string name) => Engine?.PlayAction(name)
        ?? new PetActionSummary(false, "尚未初始化");

    public bool PlayMeme(string name)
    {
        if (Engine == null || _memes == null || !_memes.TryGetPath(name, out _))
        {
            return false;
        }

        return Engine.ShowMeme(name);
    }

    public string? GetMemePath(string name)
        => _memes != null && _memes.TryGetPath(name, out string path) ? path : null;

    private void ApplySettingsToConfig(PetConfig config, AppSettings settings)
    {
        if (ActivePet is { } pet)
        {
            pet.Size = settings.PetSize;
        }

        config.Display.MotionRangeX = settings.MotionRangeX;
        config.Xiaowai.Url = settings.StateUrl;
        config.Xiaowai.PollSec = settings.PollSeconds;
        config.Xiaowai.TimeoutSec = settings.TimeoutSeconds;
        config.Xiaowai.BackoffSec = settings.BackoffSeconds;
        config.Xiaowai.ShowCover = settings.ShowCover;
        config.Xiaowai.SleepFromRemote = settings.RespectRemoteSleeping;
        config.Chatter.Enabled = settings.ShowChatter;

        MoveEdgeConfig edge = config.Animations.Moves.Edge;
        edge.Enabled = settings.EdgeRestEnabled;
        edge.Chance = Math.Clamp(_baseEdgeChance * settings.WalkChance, 0, 1);
        edge.RestMinSec = settings.EdgeRestMinSeconds;
        edge.RestMaxSec = settings.EdgeRestMaxSeconds;
    }

    /// <summary>写盘并热应用；失败时返回 false，不改动当前生效设置。</summary>
    public bool SaveSettings(AppSettings settings, out string error)
    {
        lock (_gate)
        {
            if (!_initialized || _disposed || Config is null)
            {
                error = "运行时尚未初始化。";
                return false;
            }

            var notes = new System.Collections.Generic.List<string>();
            settings.Clamp(notes);
            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                error = "写入设置失败：" + ex.Message;
                return false;
            }

            string oldTarget = Settings.DisplayTarget;
            Settings = settings.Clone();
            ApplySettingsToConfig(Config, Settings);
            Engine?.ApplySettings(Settings);
            _renderer?.Configure(Config);
            RestartXiaowai(reapplyCover: true);

            if (!AutostartService.SetEnabled(Settings.AutoStart))
            {
                AddLog("开机自启设置失败（注册表不可写）");
            }

            foreach (string note in notes)
            {
                AddLog(note);
            }

            AddLog($"配置已应用：轮询 {Settings.PollSeconds}s");
            AddLog(
                $"外观已应用：size={Settings.PetSize} range={Settings.MotionRangeX} " +
                $"speed={Settings.WalkSpeedScale:0.0}×");
            AddLog(
                $"显示开关：碎碎念={(Settings.ShowChatter ? "开" : "关")}，胶囊={(Settings.ShowStatusCapsule ? "开" : "关")}，" +
                $"封面={(Settings.ShowCover ? "开" : "关")}，B站={(Settings.ShowBiliProgress ? "开" : "关")}，" +
                $"搭话={(Settings.ShowNextProactive ? "开" : "关")}，第几天={(Settings.ShowDaysTogether ? "开" : "关")}，" +
                $"空闲详情={(Settings.ShowIdleDetail ? "开" : "关")}，置顶={(Settings.Topmost ? "开" : "关")}");
            AddLog(
                $"行为开关：走动={(Settings.WalkingEnabled ? "开" : "关")}，频率={Settings.WalkChance * 100:0}%，" +
                $"贴边休息={(Settings.EdgeRestEnabled ? "开" : "关")}，休息={Settings.EdgeRestMinSeconds}~{Settings.EdgeRestMaxSeconds}s，" +
                $"跟随睡眠={(Settings.RespectRemoteSleeping ? "开" : "关")}");
            if (!string.Equals(oldTarget, Settings.DisplayTarget, StringComparison.Ordinal))
            {
                AddLog("屏幕选择已保存：切换屏幕需要重启程序才生效");
            }

            SettingsApplied?.Invoke();
            error = "";
            return true;
        }
    }

    public bool PointerDown(double x, double y) => Engine?.PointerDown(x, y) ?? false;

    public void PointerMove(double x, double y) => Engine?.PointerMove(x, y);

    public void PointerUp(double x, double y, double vx, double vy) => Engine?.PointerUp(x, y, vx, vy);

    public void SetStreaming(bool enabled) => _display?.SetStreaming(enabled);

    public void OpenConfigFile()
    {
        try
        {
            string path = ConfigPath;
            if (string.Equals(path, BundledConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PetConfigLoader.UserConfigPath)!);
                File.Copy(BundledConfigPath, PetConfigLoader.UserConfigPath, overwrite: false);
                path = PetConfigLoader.UserConfigPath;
                AddLog($"已复制配置文件到用户目录：{path}");
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog($"打开配置失败：{ex.Message}");
        }
    }

    public void SaveScreenshot(string path)
    {
        if (Engine == null || _renderer == null)
        {
            throw new InvalidOperationException("运行时尚未初始化。");
        }

        _renderer.SavePng(Engine.CreateSnapshot(), path);
    }

    private void Subscribe(DisplayService display)
    {
        display.PreviewReady += frame => PreviewReady?.Invoke(frame);
        display.StatusChanged += message => StatusChanged?.Invoke(message);
        display.Log += AddLog;
    }

    public void AddLog(string message)
    {
        // 文件日志与界面日志共用同一份脱敏结果，避免两边不一致。
        string masked = PathMasker.Mask(message);
        Log.Write(masked);
        LogAdded?.Invoke(masked);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopXiaowai(clearState: false);

            _display?.Dispose();
            _display = null;
            _renderer?.Dispose();
            _renderer = null;
            _frameCache?.Dispose();
            _frameCache = null;
        }
    }
}
