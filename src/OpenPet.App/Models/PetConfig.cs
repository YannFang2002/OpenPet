using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPet.App.Models;

/// <summary>
/// OpenPet 的宠物配置。v2 只保留独立版副屏需要的显示、动画、物理、远程状态与碎碎念参数。
/// </summary>
public sealed class PetConfig
{
    public int SchemaVersion { get; set; } = 2;

    /// <summary>多只宠物时选哪一只；缺省用 pets[0]。</summary>
    public string ActivePetId { get; set; } = "";

    public List<PetProfile> Pets { get; set; } = new();

    public DisplayConfig Display { get; set; } = new();

    public XiaowaiConfig Xiaowai { get; set; } = new();

    public ChatterConfig Chatter { get; set; } = new();

    public FontConfig Font { get; set; } = new();

    public PhysicsConfig Physics { get; set; } = new();

    public AnimationsConfig Animations { get; set; } = new();

    public WeightsConfig AnimationWeights { get; set; } = new();

    public MouseConfig Mouse { get; set; } = new();

    public PetProfile ResolvePet()
    {
        if (Pets.Count == 0)
        {
            throw new PetConfigException("配置里没有任何宠物：pets 数组不能为空。");
        }

        PetProfile? pet = null;
        if (!string.IsNullOrWhiteSpace(ActivePetId))
        {
            pet = Pets.Find(p => string.Equals(p.Id, ActivePetId, StringComparison.Ordinal));
            if (pet == null)
            {
                throw new PetConfigException($"activePetId = \"{ActivePetId}\" 在 pets 里找不到。");
            }
        }

        return pet ?? Pets[0];
    }
}

public sealed class PetProfile
{
    public string Id { get; set; } = "main";
    public string Name { get; set; } = "小女仆";

    /// <summary>显示宽度（px，屏幕坐标）。高度 = size × 9/16，与 dsh-pet 一致。</summary>
    public double Size { get; set; } = 320;

    /// <summary>初始朝向：left / right。</summary>
    public string Facing { get; set; } = "left";

    /// <summary>true = 随机链不抽「转向 / 移动」，只播原地动作。</summary>
    public bool FixedEnabled { get; set; }

    public PositionConfig Position { get; set; } = new();
}

public sealed class PositionConfig
{
    public string Corner { get; set; } = "bottom-center";
    public double MarginX { get; set; }
    public double MarginY { get; set; }
}

public sealed class DisplayConfig
{
    /// <summary>auto = 按本地时间切换清晨/白天/黄昏/夜晚；也可固定 morning/day/dusk/night。</summary>
    public string Theme { get; set; } = "auto";

    public bool Hud { get; set; } = true;
    public bool Bubble { get; set; } = true;

    /// <summary>主强调色，用于气泡描边。</summary>
    public string Accent { get; set; } = "#8FBCFF";

    /// <summary>气泡底色。</summary>
    public string BubbleColor { get; set; } = "#FFF8EC";

    /// <summary>屏幕上的宠物左右活动半径（px，围绕画面中心）。</summary>
    public double MotionRangeX { get; set; } = 46;

    /// <summary>宠物脚底所在的 y（320×240 逻辑坐标）。</summary>
    public double FloorY { get; set; } = 204;

    /// <summary>是否允许在屏幕上弹出表情包贴纸。</summary>
    public bool MemesEnabled { get; set; } = true;

    /// <summary>碎碎念时随机附一张表情包的概率（0~1）。</summary>
    public double MemeChance { get; set; } = 0.35;

    /// <summary>表情包贴纸停留秒数。</summary>
    public double MemeDurationSec { get; set; } = 4.0;

    /// <summary>表情包贴纸在屏幕上的边长（px）。</summary>
    public double MemeSize { get; set; } = 104;
}

public sealed class XiaowaiConfig
{
    public const string DefaultUrl = "http://127.0.0.1:18900";

    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = DefaultUrl;
    public double PollSec { get; set; } = 5;
    public double TimeoutSec { get; set; } = 3;
    public double BackoffSec { get; set; } = 15;
    public bool ShowCover { get; set; } = true;
    public bool BubbleOnChange { get; set; } = true;
    public bool SleepFromRemote { get; set; } = true;

    public Dictionary<string, string[]> Actions { get; set; } = new()
    {
        ["chatting"] = new[] { "写代码", "轻快记录" },
        ["browsing_bili"] = new[] { "照镜子", "摇扇纳凉" },
        ["idle"] = Array.Empty<string>(),
    };
}

public sealed class ChatterConfig
{
    public bool Enabled { get; set; } = true;
    public double MinSec { get; set; } = 240;
    public double MaxSec { get; set; } = 600;
    public List<string> FallbackLines { get; set; } = new() { "……", "哼。", "哥，你在忙吗" };
}

public sealed class FontConfig
{
    /// <summary>candy（内置上首软糖体）/ yahei / system。</summary>
    public string Family { get; set; } = "yahei";

    /// <summary>气泡基准字号（px）。</summary>
    public double Size { get; set; } = 12;

    public bool FollowPet { get; set; }
}

/// <summary>与 dsh-pet 同名的拖拽抛掷参数；屏幕坐标下同样以 px/s² 计。</summary>
public sealed class PhysicsConfig
{
    public double Gravity { get; set; } = 1400;
    public double Restitution { get; set; } = 0.78;
    public double GroundFriction { get; set; } = 2.5;
    public bool CeilingBounce { get; set; } = true;
    public double ThrowPower { get; set; } = 1.0;
    public bool PetCollision { get; set; }
}

public sealed class AnimationsConfig
{
    public List<string> Idle { get; set; } = new();
    public List<string> Turn { get; set; } = new();
    public List<string> Drag { get; set; } = new();
    public List<string> Clicks { get; set; } = new();
    public MovesConfig Moves { get; set; } = new();
    public List<CategoryConfig> Categories { get; set; } = new();
    public Dictionary<string, List<string>> Events { get; set; } = new();
}

public sealed class MovesConfig
{
    public MoveParams Default { get; set; } = new();

    public List<MoveActionConfig> Actions { get; set; } = new();

    public MoveEdgeConfig Edge { get; set; } = new();
}

public sealed class MoveParams
{
    public double MinDist { get; set; } = 60;
    public double MaxDist { get; set; } = 240;
    public double Margin { get; set; } = 20;
    public double LeadSec { get; set; } = 2;
    public double TailSec { get; set; } = 2;
}

/// <summary>走到左右边缘后趴/坐休息的参数。</summary>
public sealed class MoveEdgeConfig
{
    public bool Enabled { get; set; }
    public double Chance { get; set; } = 0.45;
    public double RestMinSec { get; set; } = 60;
    public double RestMaxSec { get; set; } = 180;
    public double TolerancePx { get; set; } = 4;
    public double Margin { get; set; } = 6;
    public List<string> Actions { get; set; } = new();
}

public sealed class MoveActionConfig
{
    public string Name { get; set; } = "";
    public MoveParams? Params { get; set; }
}

public sealed class CategoryConfig
{
    public string Id { get; set; } = "";
    public double Weight { get; set; } = 10;
    public bool NoMirror { get; set; }
    public List<string> Actions { get; set; } = new();
}

public sealed class WeightsConfig
{
    public double Idle { get; set; } = 10;
    public double Turn { get; set; } = 5;
    public double Move { get; set; } = 5;
}

public sealed class MouseConfig
{
    public bool DragEnabled { get; set; } = true;
    public bool ClickReaction { get; set; } = true;
}

public sealed class PetConfigException : Exception
{
    public PetConfigException(string message) : base(message) { }
    public PetConfigException(string message, Exception inner) : base(message, inner) { }
}

public sealed record LoadedPetConfig(
    PetConfig Config,
    PetProfile Pet,
    string Path,
    IReadOnlyList<string> Warnings);

public static class PetConfigLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string AppBase => AppContext.BaseDirectory;

    public static string UserConfigPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPet", "pet.jsonc");

    public static string DefaultBundledPath => System.IO.Path.Combine(AppBase, "assets", "pet.jsonc");

    public static string ResolveConfigPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return System.IO.Path.GetFullPath(explicitPath);
        }

        return File.Exists(UserConfigPath) ? UserConfigPath : DefaultBundledPath;
    }

    public static LoadedPetConfig Load(string? explicitPath = null, ICollection<string>? warnings = null)
    {
        string path = ResolveConfigPath(explicitPath);
        if (!File.Exists(path))
        {
            throw new PetConfigException($"找不到配置文件：{path}");
        }

        string json = File.ReadAllText(path);
        PetConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<PetConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PetConfigException($"配置 JSON 解析失败（{path}）：{ex.Message}", ex);
        }

        if (config == null)
        {
            throw new PetConfigException($"配置为空：{path}");
        }

        var notes = new List<string>();
        Normalize(config, notes);
        if (warnings != null)
        {
            foreach (string note in notes)
            {
                warnings.Add(note);
            }
        }

        PetProfile pet = config.ResolvePet();
        return new LoadedPetConfig(config, pet, path, notes);
    }

    private static void Normalize(PetConfig c, List<string> notes)
    {
        c.Pets ??= new List<PetProfile>();
        c.Display ??= new DisplayConfig();
        c.Xiaowai ??= new XiaowaiConfig();
        c.Chatter ??= new ChatterConfig();
        c.Font ??= new FontConfig();
        c.Physics ??= new PhysicsConfig();
        c.Animations ??= new AnimationsConfig();
        c.AnimationWeights ??= new WeightsConfig();
        c.Mouse ??= new MouseConfig();

        foreach (PetProfile p in c.Pets)
        {
            p.Id = string.IsNullOrWhiteSpace(p.Id) ? "main" : p.Id.Trim();
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name.Trim();
            if (double.IsNaN(p.Size) || double.IsInfinity(p.Size) || p.Size <= 0)
            {
                p.Size = 320;
            }

            p.Size = Math.Clamp(p.Size, 100, 320);

            if (p.Facing is not ("left" or "right"))
            {
                p.Facing = "left";
            }

            p.Position ??= new PositionConfig();
        }

        if (c.Pets.Count > 1)
        {
            notes.Add("配置里有多只宠物；独立应用当前只养 activePetId 指定的那一只。");
        }

        if (c.Animations.Idle == null || c.Animations.Idle.Count == 0)
        {
            throw new PetConfigException("animations.idle 至少需要一个待机动画。");
        }

        c.Animations.Turn ??= new List<string>();
        c.Animations.Drag ??= new List<string>();
        c.Animations.Clicks ??= new List<string>();
        c.Animations.Moves ??= new MovesConfig();
        c.Animations.Moves.Default ??= new MoveParams();
        c.Animations.Moves.Actions ??= new List<MoveActionConfig>();
        c.Animations.Moves.Edge ??= new MoveEdgeConfig();
        c.Animations.Categories ??= new List<CategoryConfig>();
        c.Animations.Events ??= new Dictionary<string, List<string>>();
        foreach (string key in c.Animations.Events.Keys.ToList())
        {
            c.Animations.Events[key] ??= new List<string>();
        }

        foreach (CategoryConfig category in c.Animations.Categories)
        {
            category.Actions ??= new List<string>();
            if (double.IsNaN(category.Weight) || category.Weight < 0)
            {
                notes.Add($"动作分类 {category.Id} 的 weight 非法，已改为 0。");
                category.Weight = 0;
            }
        }

        c.AnimationWeights.Idle = Math.Max(0, c.AnimationWeights.Idle);
        c.AnimationWeights.Turn = Math.Max(0, c.AnimationWeights.Turn);
        c.AnimationWeights.Move = Math.Max(0, c.AnimationWeights.Move);

        c.Display.MotionRangeX = Math.Clamp(c.Display.MotionRangeX, 0, 120);
        PetProfile? primaryPet = c.Pets.Find(p => string.Equals(p.Id, c.ActivePetId, StringComparison.Ordinal))
            ?? c.Pets.FirstOrDefault();
        double maxMotionRange = primaryPet is null ? 120 : Math.Max(0, (320 - primaryPet.Size) / 2);
        if (c.Display.MotionRangeX > maxMotionRange)
        {
            notes.Add(
                $"display.motionRangeX={c.Display.MotionRangeX:0.##} 超过 (320-size)/2={maxMotionRange:0.##}，已下调。");
            c.Display.MotionRangeX = maxMotionRange;
        }

        MoveEdgeConfig edge = c.Animations.Moves.Edge;
        edge.Actions ??= new List<string>();
        edge.Chance = double.IsFinite(edge.Chance) ? Math.Clamp(edge.Chance, 0, 1) : 0.45;
        edge.RestMinSec = double.IsFinite(edge.RestMinSec) ? Math.Clamp(edge.RestMinSec, 10, 600) : 60;
        edge.RestMaxSec = double.IsFinite(edge.RestMaxSec) ? Math.Clamp(edge.RestMaxSec, 10, 600) : 180;
        if (edge.RestMaxSec < edge.RestMinSec)
        {
            (edge.RestMinSec, edge.RestMaxSec) = (edge.RestMaxSec, edge.RestMinSec);
        }

        edge.TolerancePx = double.IsFinite(edge.TolerancePx) ? Math.Clamp(edge.TolerancePx, 0, 20) : 4;
        edge.Margin = double.IsFinite(edge.Margin) ? Math.Clamp(edge.Margin, 0, 30) : 6;
        c.Display.FloorY = Math.Clamp(c.Display.FloorY, 150, 235);
        c.Display.MemeChance = Math.Clamp(c.Display.MemeChance, 0, 1);
        c.Display.MemeDurationSec = Math.Clamp(c.Display.MemeDurationSec, 1, 20);
        c.Display.MemeSize = Math.Clamp(c.Display.MemeSize, 56, 160);
        c.Display.Accent = NormalizeColor(c.Display.Accent, "#8FBCFF");
        c.Display.BubbleColor = NormalizeColor(c.Display.BubbleColor, "#FFF8EC");

        c.Xiaowai.PollSec = double.IsFinite(c.Xiaowai.PollSec)
            ? Math.Clamp(c.Xiaowai.PollSec, 3, 60)
            : 5;
        c.Xiaowai.TimeoutSec = double.IsFinite(c.Xiaowai.TimeoutSec)
            ? Math.Clamp(c.Xiaowai.TimeoutSec, 1, 15)
            : 3;
        c.Xiaowai.BackoffSec = double.IsFinite(c.Xiaowai.BackoffSec)
            ? Math.Clamp(c.Xiaowai.BackoffSec, 5, 120)
            : 15;
        c.Xiaowai.Url = string.IsNullOrWhiteSpace(c.Xiaowai.Url)
            ? XiaowaiConfig.DefaultUrl
            : c.Xiaowai.Url.Trim();
        c.Xiaowai.Actions ??= new XiaowaiConfig().Actions;

        c.Chatter.MinSec = double.IsFinite(c.Chatter.MinSec)
            ? Math.Clamp(c.Chatter.MinSec, 30, 3600)
            : 240;
        c.Chatter.MaxSec = double.IsFinite(c.Chatter.MaxSec)
            ? Math.Clamp(c.Chatter.MaxSec, 30, 3600)
            : 600;
        if (c.Chatter.MaxSec < c.Chatter.MinSec)
        {
            (c.Chatter.MinSec, c.Chatter.MaxSec) = (c.Chatter.MaxSec, c.Chatter.MinSec);
        }

        c.Chatter.FallbackLines ??= new List<string>();

        c.Physics.Gravity = Math.Clamp(c.Physics.Gravity, -500, 8000);
        c.Physics.Restitution = Math.Clamp(c.Physics.Restitution, 0, 0.995);
        c.Physics.GroundFriction = Math.Clamp(c.Physics.GroundFriction, 0, 30);
        c.Physics.ThrowPower = Math.Clamp(c.Physics.ThrowPower, 0.1, 4);
    }

    private static string NormalizeColor(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string value = text.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (value.Length is 7 or 9 && value[1..].All(Uri.IsHexDigit))
        {
            return value.ToUpperInvariant();
        }

        return fallback;
    }
}
