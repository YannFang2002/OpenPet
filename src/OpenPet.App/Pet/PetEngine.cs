using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenPet.App.Models;
using OpenPet.App.Services;
using OpenPet.App.Settings;

namespace OpenPet.App.Pet;

/// <summary>
/// OpenPet 副屏的渲染引擎：驱动动画链、拖拽抛掷、点击回应、气泡、表情包与远端睡眠状态。
/// 引擎不碰 UI、不碰设备，只产出快照。
/// </summary>
public sealed class PetEngine
{
    private const double PauseAfterActionSec = 2.4;
    private const double PhysicsCeiling = -78;
    private const double ClickMoveThreshold = 5;

    private readonly object _gate = new();
    private readonly Random _random;
    private readonly Action<string>? _log;

    private PetConfig _config;
    private PetProfile _pet;
    private AnimationLibrary _library;
    private MemeLibrary? _memes;
    private ResolvedAnimations _pools;

    private DateTime _now = DateTime.Now;

    private bool _sleeping;
    private bool _dragging;
    private bool _airborne;
    private double _ox;
    private double _oy;
    private double _vx;
    private double _vy;
    private double _grabDx;
    private double _grabDy;
    private double _pointerLastX;
    private double _pointerLastY;
    private DateTime _pointerLastAt;
    private double _pointerDownX;
    private double _pointerDownY;
    private bool _facingRight;

    private string _currentAnim = "";
    private AnimationClip? _clip;
    private DateTime _animStarted;
    private bool _animLoop;
    private bool _animIsEvent;
    private DateTime _animEnd;
    private bool _walking;
    private double _walkFrom;
    private double _walkTo;
    private DateTime _walkStarted;
    private double _walkDuration;
    private double _walkLead;
    private double _walkTail;
    private int _edgeSide;
    private DateTime _edgeRestUntil;
    private DateTime _autoNextAt;
    private DateTime _nextChatterAt;

    private readonly List<Particle> _particles = new();
    private Bubble? _bubble;
    private string _lastChatterLine = "";
    private string? _memeName;
    private DateTime _memeUntil;
    private XiaowaiView? _remote;

    private DateTime _lastParticleUpdate = DateTime.MinValue;
    private DateTime _lastPhysicsAt = DateTime.MinValue;

    private sealed class Particle
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Life;
        public double MaxLife;
        public Color Color;
        public double Size;
        public ParticleShape Shape;
        public double Gravity;
    }

    private sealed class Bubble
    {
        public required string Text { get; init; }
        public DateTime CreatedAt { get; init; }
        public double DurationSec { get; init; }
        public bool Important { get; init; }
    }

    public PetEngine(
        LoadedPetConfig loaded,
        AnimationLibrary library,
        MemeLibrary? memes,
        Action<string>? log = null)
    {
        _config = loaded.Config;
        _pet = loaded.Pet;
        _library = library;
        _memes = memes;
        _log = log;
        _random = new Random();
        _pools = ResolvedAnimations.Build(_config, _library, msg => _log?.Invoke(msg));
        _facingRight = string.Equals(_pet.Facing, "right", StringComparison.OrdinalIgnoreCase);
        _now = DateTime.Now;
        ResetAfterStart(_now);
    }

    public string PetId => _pet.Id;

    public string PetName => _pet.Name;

    public PetConfig Config => _config;

    public AppSettings Settings { get; set; } = new();

    public PetProfile Profile => _pet;

    public XiaowaiView? RemoteState
    {
        get
        {
            lock (_gate)
            {
                return _remote;
            }
        }
    }

    /// <summary>配置热重载：保留位置、气泡和当前远端状态，换掉动画池/名字等。</summary>
    public void Reconfigure(LoadedPetConfig loaded, AnimationLibrary library, MemeLibrary memes)
    {
        lock (_gate)
        {
            _config = loaded.Config;
            _pet = loaded.Pet;
            _library = library;
            _memes = memes;
            _pools = ResolvedAnimations.Build(_config, _library, msg => _log?.Invoke(msg));
            _ox = Math.Clamp(_ox, -_config.Display.MotionRangeX, _config.Display.MotionRangeX);
            _oy = Math.Clamp(_oy, PhysicsCeiling, 0);

            ApplyRemoteSleeping(_remote is { Sleeping: true });
            if (!_sleeping)
            {
                EnterIdle(_now, 1.2);
            }
        }
    }

    /// <summary>热应用用户设置：立即影响行走、贴边休息、碎碎念与快照显示开关。</summary>
    public void ApplySettings(AppSettings settings)
    {
        lock (_gate)
        {
            Settings = settings;
            if (!settings.ShowChatter)
            {
                _bubble = null;
            }

            if (!settings.EdgeRestEnabled && _edgeSide != 0)
            {
                ClearEdgeRest();
                if (!_sleeping)
                {
                    EnterIdle(_now, 1.2);
                }
            }

            if (!settings.WalkingEnabled && _walking)
            {
                _walking = false;
                if (!_sleeping)
                {
                    EnterIdle(_now, 1.2);
                }
            }

            double range = Math.Max(0, _config.Display.MotionRangeX);
            if (!_dragging && !_airborne && Math.Abs(_ox) > range)
            {
                _walking = false;
                if (!_sleeping)
                {
                    EnterIdle(_now, 1.2);
                }
            }
        }
    }

    private string EffectiveDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(Settings.NameOverride))
        {
            return Settings.NameOverride.Trim();
        }

        if (Settings.UseRemoteName && !string.IsNullOrWhiteSpace(_remote?.Name))
        {
            return _remote!.Name!.Trim();
        }

        return string.IsNullOrWhiteSpace(_pet.Name) ? _pet.Id : _pet.Name;
    }

    /// <summary>远程状态（NAS 下发）。设进来后随快照一起交给渲染器；状态变化时可选弹气泡。</summary>
    public void ApplyRemoteState(XiaowaiView? view, bool bubbleOnChange = true)
    {
        lock (_gate)
        {
            XiaowaiView? prev = _remote;
            _remote = view;

            string? prevName = prev?.Name;
            string? nextName = view?.Name;
            string fallbackName = string.IsNullOrWhiteSpace(_pet.Name) ? _pet.Id : _pet.Name;
            string effectivePrev = string.IsNullOrWhiteSpace(prevName) ? fallbackName : prevName!;
            if (!string.IsNullOrWhiteSpace(nextName) &&
                !string.Equals(effectivePrev, nextName, StringComparison.Ordinal))
            {
                _log?.Invoke($"她改名了：{effectivePrev} → {nextName}");
            }

            if (prev is not null && view is not null &&
                prev.State != view.State &&
                view.State is "chatting" or "browsing_bili")
            {
                ClearEdgeRest();
                if (!_sleeping)
                {
                    EnterIdle(_now, 1.2);
                }
            }

            ApplyRemoteSleeping(view is { Sleeping: true });

            if (view is null || prev is null) return;
            if (!bubbleOnChange || !Config.Display.Bubble) return;
            if (prev.State == view.State) return;
            string text = string.IsNullOrWhiteSpace(view.Detail) ? view.Label : view.Detail;
            if (!string.IsNullOrWhiteSpace(text)) ShowBubble(text, 7, false);
        }
    }

    /// <summary>外部强制设置一句话气泡（供远程事件用）。</summary>
    public void ShowExternalBubble(string text, double seconds = 6)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(text)) ShowBubble(text, seconds, false);
        }
    }

    // ---------------------------------------------------------------- ticking

    public void Tick(DateTime now)
    {
        lock (_gate)
        {
            if (now <= _now)
            {
                return;
            }

            _now = now;

            if (_edgeSide != 0 && now >= _edgeRestUntil)
            {
                EndEdgeRest(now);
            }

            UpdateWalking(now);
            ClampOffsetToRange();
            UpdateParticlesDelta(now);
            UpdatePhysics(now);

            if (_dragging)
            {
                EnsureLoop(_pools.Drag.FirstOrDefault());
            }
            else if (_sleeping)
            {
                EnsureLoop(_pools.Sleep.FirstOrDefault() ?? _pools.Idle[0]);
            }
            else if (!_animLoop && _clip != null && now >= _animEnd)
            {
                OnAnimationEnded(now);
            }
            else if (_animLoop && _currentAnim == _pools.Idle.FirstOrDefault())
            {
                if (now >= _autoNextAt)
                {
                    AutoPick(now);
                }
            }

            UpdateChatter(now);

            if (_bubble != null && (now - _bubble.CreatedAt).TotalSeconds > _bubble.DurationSec + 0.4)
            {
                _bubble = null;
            }

            if (_memeName != null && now >= _memeUntil)
            {
                _memeName = null;
            }
        }
    }

    private void UpdateParticlesDelta(DateTime now)
    {
        if (_lastParticleUpdate == DateTime.MinValue)
        {
            _lastParticleUpdate = now;
            return;
        }

        double dt = Math.Clamp((now - _lastParticleUpdate).TotalSeconds, 0, 0.2);
        _lastParticleUpdate = now;
        UpdateParticles(dt);
    }

    private void UpdateParticles(double dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            Particle p = _particles[i];
            p.Life += dt;
            if (p.Life >= p.MaxLife)
            {
                _particles.RemoveAt(i);
                continue;
            }

            p.Vy += p.Gravity * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Vx *= 0.995;
        }
    }

    // ---------------------------------------------------------------- actions

    /// <summary>素材库里手动点播一个动作；重复点会重播。</summary>
    public PetActionSummary PlayAction(string name)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(name) || !_library.TryGet(name, out _))
            {
                return new PetActionSummary(false, $"没有这个动作：{name}");
            }

            _dragging = false;
            _airborne = false;
            _walking = false;
            _oy = 0;
            _vx = 0;
            _vy = 0;
            _autoNextAt = _now.AddSeconds(30);

            if (!PlayAnimation(name, loop: false, isEvent: true, force: true))
            {
                return new PetActionSummary(false, $"播放失败：{name}");
            }

            _log?.Invoke($"手动播放动作：{name}");
            return new PetActionSummary(true, $"正在播放「{name}」");
        }
    }

    /// <summary>在屏幕右下角弹一张表情包贴纸。</summary>
    public bool ShowMeme(string name, double? seconds = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            double duration = seconds ?? _config.Display.MemeDurationSec;
            _memeName = name;
            _memeUntil = _now.AddSeconds(Math.Clamp(duration, 1, 20));
            _log?.Invoke($"显示表情包：{name}");
            return true;
        }
    }

    // ---------------------------------------------------------------- pointer

    public bool PointerDown(double x, double y)
    {
        lock (_gate)
        {
            if (!_config.Mouse.DragEnabled && !_config.Mouse.ClickReaction)
            {
                return false;
            }

            if (!HitTest(x, y))
            {
                return false;
            }

            ClearEdgeRest();
            _pointerDownX = x;
            _pointerDownY = y;
            _pointerLastX = x;
            _pointerLastY = y;
            _pointerLastAt = _now;
            _grabDx = x - (BaseX + _ox);
            _grabDy = y - (RestY + _oy);
            _dragging = true;
            _airborne = false;
            _walking = false;
            _vx = 0;
            _vy = 0;

            EnsureLoop(_pools.Drag.FirstOrDefault());
            _autoNextAt = _now.AddSeconds(3);
            return true;
        }
    }

    public void PointerMove(double x, double y)
    {
        lock (_gate)
        {
            if (!_dragging || !_config.Mouse.DragEnabled)
            {
                return;
            }

            double nowSeconds = (_now - _pointerLastAt).TotalSeconds;
            if (nowSeconds > 0.001)
            {
                _vx = (x - _pointerLastX) / nowSeconds;
                _vy = (y - _pointerLastY) / nowSeconds;
            }

            _pointerLastX = x;
            _pointerLastY = y;
            _pointerLastAt = _now;

            _ox = Math.Clamp(x - _grabDx - BaseX, -_config.Display.MotionRangeX, _config.Display.MotionRangeX);
            _oy = Math.Clamp(y - _grabDy - RestY, PhysicsCeiling, 0);
        }
    }

    public void PointerUp(double x, double y, double vx, double vy)
    {
        lock (_gate)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            if (!_config.Mouse.DragEnabled)
            {
                _oy = 0;
                _airborne = false;
                EnterIdle(_now, 1.2);
                ClickReaction();
                return;
            }

            double totalMove = Math.Sqrt(Math.Pow(x - _pointerDownX, 2) + Math.Pow(y - _pointerDownY, 2));

            if (totalMove < ClickMoveThreshold)
            {
                _ox = Math.Clamp(_ox, -_config.Display.MotionRangeX, _config.Display.MotionRangeX);
                _oy = 0;
                _airborne = false;
                EnterIdle(_now, 1.2);
                ClickReaction();
                return;
            }

            double power = _config.Physics.ThrowPower;
            _vx = Math.Clamp(vx * power, -2600, 2600);
            _vy = Math.Clamp(vy * power, -2600, 2600);
            _airborne = true;
            _log?.Invoke($"松手：v=({_vx:0}, {_vy:0})");
        }
    }

    private void ClickReaction()
    {
        if (!_config.Mouse.ClickReaction)
        {
            return;
        }

        if (_pools.Clicks.Count > 0)
        {
            string name = Pick(_pools.Clicks, _currentAnim);
            if (!PlayAnimation(name, loop: false, isEvent: true, force: true))
            {
                EnterIdle(_now, PauseAfterActionSec);
            }
        }
        else
        {
            EnterIdle(_now, PauseAfterActionSec);
        }

        SpawnBurst("#FFD6E6", 4, ParticleShape.Heart);
        string? text = PickChatterLine();
        if (text != null)
        {
            ShowBubble(text, 3.5, important: false);
        }

        _log?.Invoke("点击回应");
    }

    private bool HitTest(double x, double y)
    {
        double spriteW = _pet.Size;
        double spriteH = spriteW * 9.0 / 16.0;
        double left = BaseX + _ox;
        double top = RestY + _oy;
        double hitLeft = left + spriteW * 0.20;
        double hitRight = left + spriteW * 0.80;
        double hitTop = top + spriteH * 0.02;
        double hitBottom = top + spriteH;
        return x >= hitLeft && x <= hitRight && y >= hitTop && y <= hitBottom;
    }

    // ---------------------------------------------------------------- animation

    private bool PlayAnimation(string name, bool loop, bool isEvent, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(name) || !_library.TryGet(name, out AnimationClip clip))
        {
            _log?.Invoke($"动画缺失，跳过：{name}");
            return false;
        }

        if (!force && _clip != null && _currentAnim == name && _animLoop == loop)
        {
            return true;
        }

        _currentAnim = name;
        _clip = clip;
        _animStarted = _now;
        _animLoop = loop;
        _animIsEvent = isEvent;
        _animEnd = _now + clip.Duration;
        return true;
    }

    private void EnsureLoop(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_currentAnim != name || !_animLoop)
        {
            PlayAnimation(name, loop: true, isEvent: false, force: true);
        }
    }

    private void OnAnimationEnded(DateTime now)
    {
        string ended = _currentAnim;

        if (_pools.Turn.Contains(ended))
        {
            _facingRight = !_facingRight;
            EnterIdle(now, 1.6);
            return;
        }

        EnterIdle(now, PauseAfterActionSec);
    }

    private void EnterIdle(DateTime now, double pauseSec)
    {
        _walking = false;
        PlayAnimation(_pools.Idle[0], loop: true, isEvent: false, force: true);
        _autoNextAt = now.AddSeconds(Math.Max(2, pauseSec) + _random.NextDouble() * 2.0);
    }

    private void AutoPick(DateTime now)
    {
        if (_sleeping || _dragging || _airborne || _edgeSide != 0)
        {
            return;
        }

        WeightsConfig weights = _config.AnimationWeights;
        double idleW = Math.Max(0, weights.Idle);
        double turnW = _pet.FixedEnabled ? 0 : Math.Max(0, weights.Turn);
        double moveW = _pet.FixedEnabled || !Settings.WalkingEnabled
            ? 0
            : Math.Max(0, weights.Move) * Math.Clamp(Settings.WalkChance, 0, 1);
        // 与 dsh-pet 一样：idle/turn/move 是绝对百分比，剩余概率归入动作分类。
        double r = _random.NextDouble() * 100;

        if (r < idleW)
        {
            _autoNextAt = now.AddSeconds(8 + _random.NextDouble() * 6);
            return;
        }

        if (r < idleW + turnW && _pools.Turn.Count > 0)
        {
            PlayAnimation(Pick(_pools.Turn, _currentAnim), loop: false, isEvent: false);
            return;
        }

        if (r < idleW + turnW + moveW && TryStartWalk(now))
        {
            return;
        }

        CategoryConfig? category = PickCategory();
        if (category != null)
        {
            string name = Pick(category.Actions, _currentAnim);
            if (PlayAnimation(name, loop: false, isEvent: false))
            {
                _log?.Invoke($"随机动作 [{category.Id}] {name}");
                return;
            }
        }

        _autoNextAt = now.AddSeconds(6);
    }

    private CategoryConfig? PickCategory()
    {
        List<CategoryConfig> eligible = _pools.Categories
            .Where(c => c.Actions.Count > 0 && c.Weight > 0 && !(c.NoMirror && _facingRight))
            .ToList();
        if (eligible.Count == 0)
        {
            eligible = _pools.Categories.Where(c => c.Actions.Count > 0 && c.Weight > 0).ToList();
        }

        if (eligible.Count == 0)
        {
            return null;
        }

        double total = eligible.Sum(c => c.Weight);
        double t = _random.NextDouble() * total;
        foreach (CategoryConfig c in eligible)
        {
            t -= c.Weight;
            if (t <= 0)
            {
                return c;
            }
        }

        return eligible[^1];
    }

    private bool TryStartWalk(DateTime now)
    {
        if (_pools.Moves.Count == 0 || _edgeSide != 0)
        {
            return false;
        }

        MoveActionConfig action = _pools.Moves[_random.Next(_pools.Moves.Count)];
        if (!_library.TryGet(action.Name, out AnimationClip clip))
        {
            return false;
        }

        MoveParams defaults = _config.Animations.Moves.Default;
        MoveParams p = action.Params ?? defaults;
        MoveEdgeConfig edge = _config.Animations.Moves.Edge;

        bool edgeWalk = edge.Enabled && _edgeSide == 0 && _random.NextDouble() < edge.Chance;
        double target;
        double distance = 0;

        if (edgeWalk)
        {
            double maxTarget = Math.Max(0, _config.Display.MotionRangeX - edge.Margin);
            int side = _random.Next(2) == 0 ? -1 : 1;
            target = side * maxTarget;
            if (Math.Abs(target - _ox) < 8)
            {
                side = -side;
                target = side * maxTarget;
            }

            if (Math.Abs(target - _ox) < 8)
            {
                return false;
            }

            _log?.Invoke($"走去边缘：{(side < 0 ? "左" : "右")}");
        }
        else
        {
            double scale = _pet.Size / 462.0;
            distance = (p.MinDist + _random.NextDouble() * Math.Max(0, p.MaxDist - p.MinDist)) * scale;
            distance = Math.Clamp(distance, 10, _config.Display.MotionRangeX * 1.6);

            double margin = Math.Max(0, p.Margin);
            double range = Math.Max(0, _config.Display.MotionRangeX - margin);
            double direction = _random.Next(2) == 0 ? -1 : 1;
            target = Math.Clamp(_ox + distance * direction, -range, range);
            if (Math.Abs(target - _ox) < 8)
            {
                target = Math.Clamp(_ox - distance * direction, -range, range);
            }

            if (Math.Abs(target - _ox) < 8)
            {
                return false;
            }
        }

        BeginWalk(target, action, clip, p, now);
        if (!edgeWalk)
        {
            _log?.Invoke($"走动 [{action.Name}] {_ox:0} -> {target:0} (距离 {distance:0})");
        }

        return true;
    }

    private void BeginWalk(
        double target,
        MoveActionConfig action,
        AnimationClip clip,
        MoveParams p,
        DateTime now)
    {
        _walkFrom = _ox;
        _walkTo = target;
        _walkStarted = now;
        double speed = Math.Clamp(Settings.WalkSpeedScale, 0.5, 2.0);
        _walkDuration = Math.Max(0.5, clip.Duration.TotalSeconds / speed);
        _walkLead = Math.Clamp(p.LeadSec, 0, _walkDuration * 0.4);
        _walkTail = Math.Clamp(p.TailSec, 0, _walkDuration * 0.4);
        _walking = true;
        _facingRight = target > _walkFrom;
        PlayAnimation(action.Name, loop: false, isEvent: false, force: true);
    }

    private void ClampOffsetToRange()
    {
        if (_dragging || _airborne)
        {
            return;
        }

        double range = Math.Max(0, _config.Display.MotionRangeX);
        if (Math.Abs(_ox) <= range)
        {
            return;
        }

        double target = _ox > 0 ? range : -range;
        _ox += (target - _ox) * 0.2;
        if (Math.Abs(_ox - target) < 0.5)
        {
            _ox = target;
        }
    }

    private void UpdateWalking(DateTime now)
    {
        if (!_walking)
        {
            return;
        }

        double elapsed = (now - _walkStarted).TotalSeconds;
        double motionStart = _walkLead;
        double motionEnd = Math.Max(motionStart + 0.1, _walkDuration - _walkTail);
        double progress = elapsed <= motionStart
            ? 0
            : elapsed >= motionEnd
                ? 1
                : (elapsed - motionStart) / (motionEnd - motionStart);
        double eased = progress * progress * (3 - 2 * progress);
        _ox = _walkFrom + (_walkTo - _walkFrom) * eased;

        if (elapsed >= _walkDuration)
        {
            _walking = false;
            MoveEdgeConfig edge = _config.Animations.Moves.Edge;
            double maxOffset = _config.Display.MotionRangeX;
            double threshold = maxOffset - Math.Max(0, edge.Margin) - Math.Max(0, edge.TolerancePx);
            if (edge.Enabled && maxOffset > 0 && Math.Abs(_ox) >= threshold)
            {
                EnterEdgeRest(_ox > 0 ? 1 : -1, now);
            }
        }
    }

    private void EnterEdgeRest(int side, DateTime now)
    {
        MoveEdgeConfig edge = _config.Animations.Moves.Edge;
        if (!edge.Enabled || side == 0)
        {
            return;
        }

        _edgeSide = side;
        double duration = edge.RestMinSec
            + _random.NextDouble() * Math.Max(0, edge.RestMaxSec - edge.RestMinSec);
        _edgeRestUntil = now.AddSeconds(Math.Max(1, duration));
        _facingRight = side < 0;
        _walking = false;

        string[] pool = edge.Actions.Where(n => _library.Contains(n)).ToArray();
        if (pool.Length > 0)
        {
            PlayAnimation(pool[_random.Next(pool.Length)], loop: true, isEvent: true, force: true);
        }
        else
        {
            EnterIdle(now, 1.2);
        }

        _log?.Invoke($"贴边休息：{(side < 0 ? "左" : "右")} offset={_ox:0} {duration:0}s");
    }

    private void EndEdgeRest(DateTime now)
    {
        int side = _edgeSide;
        ClearEdgeRest();
        if (side == 0)
        {
            return;
        }

        MoveEdgeConfig edge = _config.Animations.Moves.Edge;
        bool toOtherSide = _random.Next(2) == 0;
        double maxTarget = Math.Max(0, _config.Display.MotionRangeX - edge.Margin);
        double target = toOtherSide ? -side * maxTarget : 0;
        _log?.Invoke(toOtherSide ? "休息结束，去另一边" : "休息结束，往回走");

        if (_pools.Moves.Count == 0 || Math.Abs(target - _ox) < 8)
        {
            EnterIdle(now, 1.2);
            return;
        }

        MoveActionConfig action = _pools.Moves[_random.Next(_pools.Moves.Count)];
        if (!_library.TryGet(action.Name, out AnimationClip clip))
        {
            EnterIdle(now, 1.2);
            return;
        }

        MoveParams p = action.Params ?? _config.Animations.Moves.Default;
        BeginWalk(target, action, clip, p, now);
    }

    private void ClearEdgeRest()
    {
        _edgeSide = 0;
        _edgeRestUntil = DateTime.MinValue;
    }

    /// <summary>无窗口截图用：直接把宠物放到指定边缘并进入休息姿势。</summary>
    internal void ForceEdgeRestForSnapshot(int side)
    {
        lock (_gate)
        {
            MoveEdgeConfig edge = _config.Animations.Moves.Edge;
            double maxTarget = Math.Max(0, _config.Display.MotionRangeX - edge.Margin);
            _ox = side * maxTarget;
            _walking = false;
            _airborne = false;
            _dragging = false;
            _oy = 0;
            EnterEdgeRest(side, _now);
        }
    }

    // ---------------------------------------------------------------- physics

    private void UpdatePhysics(DateTime now)
    {
        double dt = _lastPhysicsAt == DateTime.MinValue
            ? 1.0 / 20.0
            : Math.Clamp((now - _lastPhysicsAt).TotalSeconds, 0.001, 0.12);
        _lastPhysicsAt = now;

        if (!_airborne || _dragging)
        {
            return;
        }

        _vy += _config.Physics.Gravity * dt;
        _ox += _vx * dt;
        _oy += _vy * dt;

        double range = _config.Display.MotionRangeX;
        if (_ox < -range)
        {
            _ox = -range;
            _vx = -_vx * _config.Physics.Restitution;
        }
        else if (_ox > range)
        {
            _ox = range;
            _vx = -_vx * _config.Physics.Restitution;
        }

        if (_oy < PhysicsCeiling)
        {
            _oy = PhysicsCeiling;
            if (_config.Physics.CeilingBounce)
            {
                _vy = -_vy * _config.Physics.Restitution;
            }
            else
            {
                _vy = Math.Max(0, _vy);
            }
        }

        if (_oy >= 0)
        {
            _oy = 0;
            if (_vy > 0)
            {
                _vy = -_vy * _config.Physics.Restitution;
            }

            double friction = Math.Exp(-_config.Physics.GroundFriction * dt);
            _vx *= friction;

            if (Math.Abs(_vy) < 45 && Math.Abs(_vx) < 18)
            {
                _airborne = false;
                _vx = 0;
                _vy = 0;
                EnterIdle(now, 1.2);
            }
        }
    }

    // ---------------------------------------------------------------- remote sleep

    private void ApplyRemoteSleeping(bool sleeping)
    {
        if (!_config.Xiaowai.SleepFromRemote)
        {
            sleeping = false;
        }

        if (sleeping)
        {
            ClearEdgeRest();
        }

        if (sleeping == _sleeping)
        {
            return;
        }

        _sleeping = sleeping;
        if (sleeping)
        {
            _walking = false;
            _airborne = false;
            _vx = 0;
            _vy = 0;
            _oy = 0;
            EnsureLoop(_pools.Sleep.FirstOrDefault() ?? _pools.Idle[0]);
            return;
        }

        if (_pools.Wake.Count > 0)
        {
            string name = Pick(_pools.Wake, _currentAnim);
            if (!PlayAnimation(name, loop: false, isEvent: true, force: true))
            {
                EnterIdle(_now, 1.2);
            }
        }
        else
        {
            EnterIdle(_now, 1.2);
        }
    }

    // ---------------------------------------------------------------- chatter

    private void UpdateChatter(DateTime now)
    {
        if (!_config.Chatter.Enabled || _sleeping || _dragging || _animIsEvent || now < _nextChatterAt)
        {
            return;
        }

        _nextChatterAt = now.AddSeconds(NextChatterDelay());
        string? line = PickChatterLine();
        if (line == null)
        {
            return;
        }

        ShowBubble(line, 5.5, important: false);

        if (_config.Display.MemesEnabled
            && _config.Display.MemeChance > 0
            && _memes is { Count: > 0 }
            && _random.NextDouble() < _config.Display.MemeChance)
        {
            string? meme = _memes.PickRandom(_random);
            if (meme != null)
            {
                _memeName = meme;
                _memeUntil = now.AddSeconds(_config.Display.MemeDurationSec);
                _log?.Invoke($"碎碎念配图：{meme}");
            }
        }

        _log?.Invoke($"碎碎念：{line}");
    }

    private string? PickChatterLine()
    {
        IReadOnlyList<string>? pool = _remote?.RecentLines;
        if (pool == null || pool.Count == 0)
        {
            pool = _config.Chatter.FallbackLines;
        }

        List<string> candidates = pool
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        string chosen = Pick(candidates, _lastChatterLine);
        _lastChatterLine = chosen;
        return chosen;
    }

    private double NextChatterDelay()
    {
        double min = _config.Chatter.MinSec;
        double max = Math.Max(min, _config.Chatter.MaxSec);
        return min + _random.NextDouble() * Math.Max(0, max - min);
    }

    // ---------------------------------------------------------------- particles/bubble

    private void SpawnBurst(string color, int count, ParticleShape shape)
    {
        double cx = BaseX + _pet.Size / 2 + _ox;
        double cy = RestY + _pet.Size * 9.0 / 32.0 + _oy;
        Color c = ParseColor(color, Color.White);
        for (int i = 0; i < count; i++)
        {
            double angle = _random.NextDouble() * Math.PI * 2;
            double speed = 20 + _random.NextDouble() * 55;
            _particles.Add(new Particle
            {
                X = cx + (_random.NextDouble() - 0.5) * 30,
                Y = cy + (_random.NextDouble() - 0.5) * 20,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed - 25,
                Life = 0,
                MaxLife = 0.8 + _random.NextDouble() * 0.9,
                Color = c,
                Size = 1.5 + _random.NextDouble() * 2.5,
                Shape = shape,
                Gravity = 55,
            });
        }
    }

    private static Color ParseColor(string text, Color fallback)
    {
        try
        {
            return ColorTranslator.FromHtml(text);
        }
        catch
        {
            return fallback;
        }
    }

    private void ShowBubble(string text, double durationSec, bool important)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _bubble = new Bubble
        {
            Text = text.Trim(),
            CreatedAt = _now,
            DurationSec = Math.Clamp(durationSec, 2, 12),
            Important = important,
        };
    }

    private string Pick(IReadOnlyList<string> pool, string exclude = "")
    {
        if (pool.Count == 0)
        {
            return "";
        }

        if (pool.Count == 1)
        {
            return pool[0];
        }

        List<string> candidates = pool.Where(n => !string.Equals(n, exclude, StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0)
        {
            candidates = pool.ToList();
        }

        return candidates[_random.Next(candidates.Count)];
    }

    private void ResetAfterStart(DateTime now)
    {
        _currentAnim = _pools.Idle[0];
        _clip = _library.Find(_currentAnim);
        _animStarted = now;
        _animLoop = true;
        _animEnd = now.Add(_clip?.Duration ?? TimeSpan.FromSeconds(10));
        _autoNextAt = now.AddSeconds(6);
        _nextChatterAt = now.AddSeconds(NextChatterDelay());
        _facingRight = string.Equals(_pet.Facing, "right", StringComparison.OrdinalIgnoreCase);
        _lastParticleUpdate = now;
        _lastPhysicsAt = now;
    }

    public PetSnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            DateTime now = _now;
            int frameIndex = 0;
            if (_clip != null)
            {
                frameIndex = _clip.FrameIndexAt(now - _animStarted, _animLoop);
            }

            BubbleSnapshot? bubble = null;
            if (_bubble != null && _config.Display.Bubble)
            {
                bubble = new BubbleSnapshot
                {
                    Text = _bubble.Text,
                    AgeSeconds = Math.Max(0, (now - _bubble.CreatedAt).TotalSeconds),
                    DurationSeconds = _bubble.DurationSec,
                    Important = _bubble.Important,
                };
            }

            var particles = _particles.Select(p => new ParticleSnapshot
            {
                X = p.X,
                Y = p.Y,
                Life = p.Life,
                MaxLife = p.MaxLife,
                Color = p.Color,
                Size = p.Size,
                Shape = p.Shape,
            }).ToArray();

            return new PetSnapshot
            {
                PetId = _pet.Id,
                PetName = _pet.Name,
                DisplayName = EffectiveDisplayName(),
                ShowStatusCapsule = Settings.ShowStatusCapsule,
                ShowCover = Settings.ShowCover,
                ShowBiliProgress = Settings.ShowBiliProgress,
                ShowNextProactive = Settings.ShowNextProactive,
                ShowDaysTogether = Settings.ShowDaysTogether,
                ShowIdleDetail = Settings.ShowIdleDetail,
                Sleeping = _sleeping,
                Dragging = _dragging,
                Airborne = _airborne,
                Activity = _remote?.State ?? "idle",
                StatusText = _remote?.Label ?? "",
                Size = _pet.Size,
                OffsetX = _ox,
                OffsetY = _oy,
                FloorY = _config.Display.FloorY,
                MotionRangeX = _config.Display.MotionRangeX,
                FacingRight = _facingRight,
                Clip = _clip,
                FrameIndex = frameIndex,
                Bubble = bubble,
                Particles = particles,
                MemeName = _memeName,
                MemeSecondsRemaining = _memeName == null ? 0 : Math.Max(0, (_memeUntil - now).TotalSeconds),
                MemeSize = _config.Display.MemeSize,
                ShowHud = _config.Display.Hud,
                Theme = _config.Display.Theme,
                Now = now,
                Xiaowai = _remote,
            };
        }
    }

    private double BaseX => (320 - _pet.Size) / 2.0;

    private double RestY => _config.Display.FloorY - _pet.Size * 9.0 / 16.0 * 0.9167;
}

/// <summary>把配置里的动画池和实际素材求交集，避免每次抽到缺失素材。</summary>
public sealed class ResolvedAnimations
{
    public required List<string> Idle { get; init; }
    public required List<string> Turn { get; init; }
    public required List<string> Drag { get; init; }
    public required List<string> Clicks { get; init; }
    public required List<string> Sleep { get; init; }
    public required List<string> Wake { get; init; }
    public required List<CategoryConfig> Categories { get; init; }
    public required List<MoveActionConfig> Moves { get; init; }

    public static ResolvedAnimations Build(PetConfig config, AnimationLibrary library, Action<string>? warn)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();

        List<string> Resolve(IEnumerable<string>? names, string label, bool required = false)
        {
            var result = new List<string>();
            if (names == null)
            {
                return result;
            }

            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (library.Contains(name))
                {
                    result.Add(name);
                }
                else if (missing.Add(name))
                {
                    notes.Add($"动画缺失：{name}（{label}）");
                }
            }

            if (required && result.Count == 0)
            {
                throw new PetConfigException($"{label} 里没有任何可用动画，请检查 assets/animations 与配置。");
            }

            return result;
        }

        List<string> idle = Resolve(config.Animations.Idle, "animations.idle", required: true);
        List<string> turn = Resolve(config.Animations.Turn, "animations.turn");
        List<string> drag = Resolve(config.Animations.Drag, "animations.drag");
        List<string> clicks = Resolve(config.Animations.Clicks, "animations.clicks");

        var categories = new List<CategoryConfig>();
        foreach (CategoryConfig category in config.Animations.Categories)
        {
            List<string> actions = Resolve(category.Actions, $"categories.{category.Id}");
            if (actions.Count > 0 && category.Weight > 0)
            {
                categories.Add(new CategoryConfig
                {
                    Id = category.Id,
                    Weight = category.Weight,
                    NoMirror = category.NoMirror,
                    Actions = actions,
                });
            }
        }

        var moves = new List<MoveActionConfig>();
        foreach (MoveActionConfig move in config.Animations.Moves.Actions)
        {
            if (library.Contains(move.Name))
            {
                moves.Add(move);
            }
            else if (missing.Add(move.Name))
            {
                notes.Add($"动画缺失：{move.Name}（moves）");
            }
        }

        config.Animations.Events.TryGetValue("sleep", out List<string>? sleepNames);
        config.Animations.Events.TryGetValue("wake", out List<string>? wakeNames);
        List<string> sleep = Resolve(sleepNames, "events.sleep");
        List<string> wake = Resolve(wakeNames, "events.wake");
        List<string> fallbackClicks = clicks.Count > 0 ? clicks : idle;

        if (warn != null)
        {
            foreach (string note in notes)
            {
                warn(note);
            }
        }

        return new ResolvedAnimations
        {
            Idle = idle,
            Turn = turn,
            Drag = drag,
            Clicks = fallbackClicks,
            Sleep = sleep.Count > 0 ? sleep : idle,
            Wake = wake,
            Categories = categories,
            Moves = moves,
        };
    }
}
