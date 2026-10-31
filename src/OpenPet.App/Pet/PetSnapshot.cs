using System;
using System.Collections.Generic;
using System.Drawing;
using OpenPet.App.Services;

namespace OpenPet.App.Pet;

public enum ParticleShape
{
    Circle = 0,
    Heart = 1,
    Star = 2,
}

public sealed class ParticleSnapshot
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Life { get; init; }
    public double MaxLife { get; init; } = 1;
    public Color Color { get; init; }
    public double Size { get; init; } = 2;
    public ParticleShape Shape { get; init; }

    public double Alpha => Math.Clamp(1 - Life / Math.Max(0.001, MaxLife), 0, 1);
}

public sealed class BubbleSnapshot
{
    public required string Text { get; init; }
    public double AgeSeconds { get; init; }
    public double DurationSeconds { get; init; } = 5;
    public bool Important { get; init; }

    public double Alpha
    {
        get
        {
            double remaining = DurationSeconds - AgeSeconds;
            if (remaining <= 0)
            {
                return 0;
            }

            return Math.Clamp(remaining / 0.35, 0, 1);
        }
    }
}

/// <summary>引擎交给渲染层的一帧只读快照。所有坐标都是 320×240 逻辑坐标。</summary>
public sealed class PetSnapshot
{
    public required string PetId { get; init; }
    public required string PetName { get; init; }

    /// <summary>远端同步/用户覆盖的显示名；null/空 = 回落到配置名。</summary>
    public string? DisplayName { get; init; }

    public bool ShowStatusCapsule { get; init; } = true;
    public bool ShowCover { get; init; } = true;
    public bool ShowBiliProgress { get; init; } = true;
    public bool ShowNextProactive { get; init; } = true;
    public bool ShowDaysTogether { get; init; } = true;
    public bool ShowIdleDetail { get; init; } = true;
    public required bool Sleeping { get; init; }
    public required bool Dragging { get; init; }
    public required bool Airborne { get; init; }
    public required string Activity { get; init; }
    public required string StatusText { get; init; }

    public required double Size { get; init; }
    public required double OffsetX { get; init; }
    public required double OffsetY { get; init; }
    public required double FloorY { get; init; }
    public required double MotionRangeX { get; init; }
    public required bool FacingRight { get; init; }

    public AnimationClip? Clip { get; init; }
    public int FrameIndex { get; init; }

    public BubbleSnapshot? Bubble { get; init; }
    public IReadOnlyList<ParticleSnapshot> Particles { get; init; } = Array.Empty<ParticleSnapshot>();

    public string? MemeName { get; init; }
    public double MemeSecondsRemaining { get; init; }
    public double MemeSize { get; init; } = 104;

    public required bool ShowHud { get; init; }
    public required string Theme { get; init; }
    public required DateTime Now { get; init; }

    /// <summary>远程状态；null = 没有接入或本次没拿到。</summary>
    public XiaowaiView? Xiaowai { get; init; }
}

public sealed record PetActionSummary(bool Ok, string Message);
