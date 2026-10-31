using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenPet.App.Models;
using OpenPet.App.Services;
using SkiaSharp;

namespace OpenPet.App.Pet;

/// <summary>
/// 把 PetSnapshot 画成 320×240 的 BGRA 场景：昼夜背景、宠物帧、气泡、表情包、
/// 角色信息层与粒子。所有坐标按 320×240 设计，其他分辨率等比居中。
/// </summary>
public sealed class PetSceneRenderer : IDisposable
{
    public const int DesignWidth = 320;
    public const int DesignHeight = 240;

    private readonly object _coverLock = new();
    private readonly FrameCache _frames;
    private readonly Dictionary<(int Width, int Height), SKBitmap> _surfaces = new();

    private SKTypeface _candyTypeface = SKTypeface.Default;
    private SKTypeface _uiTypeface = SKTypeface.Default;
    private SKColor _accent = new(0x8F, 0xBC, 0xFF);
    private SKColor _bubbleColor = new(0xFF, 0xF8, 0xEC);
    private float _bubbleFontSize = 13.5f;
    private string? _coverBvid;
    private SKBitmap? _coverBitmap;
    private bool _showCover = true;
    private bool _disposed;

    private static readonly char[] LineSeparators = { '\n' };

    public PetSceneRenderer(FrameCache frames)
    {
        _frames = frames;
    }

    public void Configure(PetConfig config)
    {
        _accent = Parse(config.Display.Accent, new SKColor(0x8F, 0xBC, 0xFF));
        _bubbleColor = Parse(config.Display.BubbleColor, new SKColor(0xFF, 0xF8, 0xEC));
        _showCover = config.Xiaowai.ShowCover;

        string family = config.Font.Family ?? "candy";
        SKTypeface? candy = null;
        if (family.Equals("candy", StringComparison.OrdinalIgnoreCase))
        {
            string path = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "上首软糖体.ttf");
            if (File.Exists(path))
            {
                candy = SKTypeface.FromFile(path);
            }
        }

        candy ??= SKTypeface.FromFamilyName("Microsoft YaHei UI") ?? SKTypeface.Default;
        SKTypeface ui = SKTypeface.FromFamilyName("Microsoft YaHei UI")
            ?? SKTypeface.FromFamilyName("Microsoft YaHei")
            ?? SKTypeface.Default;

        ReleaseTypeface(_candyTypeface);
        if (!ReferenceEquals(_uiTypeface, _candyTypeface))
        {
            ReleaseTypeface(_uiTypeface);
        }

        _candyTypeface = candy;
        _uiTypeface = ui;
        _bubbleFontSize = (float)Math.Clamp(config.Font.Size + 1.5, 12.5, 16);
    }

    public void SetCover(string? bvid, byte[]? jpeg)
    {
        if (_disposed || string.IsNullOrWhiteSpace(bvid) || jpeg is null || jpeg.Length == 0) return;
        lock (_coverLock)
        {
            if (_coverBvid == bvid && _coverBitmap is not null) return;
            SKBitmap? bmp = null;
            try { bmp = SKBitmap.Decode(jpeg); } catch { bmp = null; }
            if (bmp is null) return;
            _coverBitmap?.Dispose();
            _coverBitmap = bmp;
            _coverBvid = bvid;
        }
    }

    public byte[] Render(PetSnapshot snapshot, int width, int height)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PetSceneRenderer));
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        SKBitmap bitmap = GetSurface(width, height);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Black);

        float scale = Math.Min(width / (float)DesignWidth, height / (float)DesignHeight);
        float offsetX = (width - DesignWidth * scale) / 2f;
        float offsetY = (height - DesignHeight * scale) / 2f;
        canvas.Save();
        canvas.Translate(offsetX, offsetY);
        canvas.Scale(scale);

        DrawScene(canvas, snapshot);

        canvas.Restore();
        canvas.Flush();

        var bytes = new byte[width * height * 4];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }

    public void SavePng(PetSnapshot snapshot, string path)
    {
        SKBitmap bitmap = GetSurface(DesignWidth, DesignHeight);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            DrawScene(canvas, snapshot);
            canvas.Flush();
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private void DrawScene(SKCanvas canvas, PetSnapshot s)
    {
        DateTime now = s.Now;
        double hour = now.Hour + now.Minute / 60.0;
        Palette palette = PaletteFor(s.Theme, hour);
        DrawBackground(canvas, now, s, palette);
        DrawPetShadow(canvas, s);
        DrawPet(canvas, s);
        DrawParticles(canvas, s);
        if (s.Sleeping)
        {
            DrawSleepVeil(canvas, s, now);
        }

        DrawMeme(canvas, s);

        if (s.Xiaowai is not null)
        {
            DrawXiaowai(canvas, s);
        }

        // 气泡最后画，和封面/信息层重叠时在上。
        DrawBubble(canvas, s);
    }

    // ---------------------------------------------------------------- background

    private readonly record struct Palette(
        SKColor Top,
        SKColor Bottom,
        SKColor Glow,
        bool Night);

    private static Palette PaletteFor(string theme, double hour)
    {
        if (!string.Equals(theme, "auto", StringComparison.OrdinalIgnoreCase))
        {
            switch (theme.ToLowerInvariant())
            {
                case "morning":
                    hour = 6;
                    break;
                case "day":
                    hour = 11;
                    break;
                case "dusk":
                    hour = 17.5;
                    break;
                case "night":
                    hour = 22;
                    break;
            }
        }

        if (hour is >= 5 and < 8)
        {
            return new Palette(new SKColor(0x4B, 0x5A, 0x86), new SKColor(0xE6, 0xA1, 0x77), new SKColor(0xFF, 0xD9, 0xA0), false);
        }

        if (hour is >= 8 and < 16)
        {
            return new Palette(new SKColor(0x6F, 0xA8, 0xD4), new SKColor(0xCB, 0xE6, 0xF2), new SKColor(0xFF, 0xFF, 0xFF), false);
        }

        if (hour is >= 16 and < 19)
        {
            return new Palette(new SKColor(0x45, 0x3A, 0x6B), new SKColor(0xD9, 0x83, 0x6B), new SKColor(0xFF, 0xBE, 0x8C), false);
        }

        return new Palette(new SKColor(0x10, 0x1A, 0x33), new SKColor(0x05, 0x08, 0x10), new SKColor(0x5A, 0x78, 0xB8), true);
    }

    private void DrawBackground(SKCanvas canvas, DateTime now, PetSnapshot s, Palette p)
    {
        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(0, DesignHeight),
            new[] { p.Top, p.Bottom },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(new SKRect(0, 0, DesignWidth, DesignHeight), paint);
        paint.Shader = null;

        // 宠物身后的柔光。
        paint.Shader = SKShader.CreateRadialGradient(
            new SKPoint(160, 142),
            128,
            new[] { p.Glow.WithAlpha(58), p.Glow.WithAlpha(0) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawCircle(160, 142, 128, paint);
        paint.Shader = null;

        if (p.Night)
        {
            DrawStars(canvas, now);
        }
        else
        {
            DrawMotes(canvas, now, p);
        }

        // 地面：一条渐深的暖色台面。
        using var floor = new SKPaint { IsAntialias = true };
        floor.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 191),
            new SKPoint(0, DesignHeight),
            new[] { p.Bottom.WithAlpha(0), p.Bottom.WithAlpha(110) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(new SKRect(0, 191, DesignWidth, DesignHeight), floor);
        floor.Shader = null;

        floor.Color = p.Glow.WithAlpha(30);
        floor.Style = SKPaintStyle.Fill;
        floor.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10);
        canvas.DrawOval(new SKRect(18, (float)s.FloorY - 16, DesignWidth - 18, (float)s.FloorY + 20), floor);
        floor.MaskFilter = null;

        // 四角压暗，让 320×240 的画更集中。
        using var vignette = new SKPaint { IsAntialias = true };
        vignette.Shader = SKShader.CreateRadialGradient(
            new SKPoint(160, 120),
            150,
            new[] { SKColors.Transparent, new SKColor(0, 0, 0, 105) },
            new[] { 0.55f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(new SKRect(0, 0, DesignWidth, DesignHeight), vignette);
    }

    private static void DrawStars(SKCanvas canvas, DateTime now)
    {
        using var paint = new SKPaint { IsAntialias = true };
        double t = now.TimeOfDay.TotalSeconds;
        for (int i = 0; i < 30; i++)
        {
            double x = (i * 97 + 23) % 320;
            double y = (i * 53 + 11) % 170;
            double twinkle = 0.35 + 0.65 * Math.Abs(Math.Sin(t * 0.35 + i * 1.7));
            byte alpha = (byte)Math.Clamp(35 + twinkle * 95, 0, 255);
            paint.Color = new SKColor(220, 232, 255, alpha);
            canvas.DrawCircle((float)x, (float)y, i % 7 == 0 ? 1.2f : 0.7f, paint);
        }
    }

    private static void DrawMotes(SKCanvas canvas, DateTime now, Palette p)
    {
        using var paint = new SKPaint { IsAntialias = true };
        double t = now.TimeOfDay.TotalSeconds;
        for (int i = 0; i < 14; i++)
        {
            double x = 25 + ((i * 61 + t * (3 + i % 3)) % 290);
            double y = 70 + ((i * 41 + Math.Sin(t * 0.2 + i) * 12) % 110);
            paint.Color = p.Glow.WithAlpha((byte)(24 + i % 3 * 9));
            canvas.DrawCircle((float)x, (float)y, 0.8f + i % 3 * 0.35f, paint);
        }
    }

    // ---------------------------------------------------------------- pet

    private static double SpriteWidth(PetSnapshot s) => s.Size;

    private static double SpriteHeight(PetSnapshot s) => s.Size * 9.0 / 16.0;

    private static double PetLeft(PetSnapshot s) => (DesignWidth - SpriteWidth(s)) / 2 + s.OffsetX;

    // 素材帧底部有约 8.3% 的透明留白；角色脚底对齐 floorY，而不是帧底。
    private const double FrameFeetRatio = 0.9167;

    private static double PetTop(PetSnapshot s) => s.FloorY - SpriteHeight(s) * FrameFeetRatio + s.OffsetY;

    private void DrawPetShadow(SKCanvas canvas, PetSnapshot s)
    {
        double left = PetLeft(s);
        double width = SpriteWidth(s);
        double heightGap = Math.Max(0, -s.OffsetY);
        double shrink = Math.Clamp(1 - heightGap / 220.0, 0.45, 1);
        float cx = (float)(left + width / 2);
        float w = (float)(width * 0.34 * shrink);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, (byte)(70 * shrink)),
            Style = SKPaintStyle.Fill,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4.5f),
        };
        canvas.DrawOval(
            new SKRect(cx - w, (float)s.FloorY - 5, cx + w, (float)s.FloorY + 8),
            paint);
        paint.MaskFilter = null;
    }

    private void DrawPet(SKCanvas canvas, PetSnapshot s)
    {
        AnimationClip? clip = s.Clip;
        if (clip == null || clip.FrameCount == 0)
        {
            using var placeholder = new SKPaint { IsAntialias = true, Color = _accent.WithAlpha(180) };
            canvas.DrawCircle(160, (float)(s.FloorY - 50), 30, placeholder);
            return;
        }

        int index = Math.Clamp(s.FrameIndex, 0, clip.FrameCount - 1);
        SKBitmap? bitmap = _frames.Get(clip.FramePaths[index]);
        if (bitmap == null)
        {
            return;
        }

        double width = SpriteWidth(s);
        double height = SpriteHeight(s);
        double left = PetLeft(s);
        double top = PetTop(s);

        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        if (s.FacingRight)
        {
            canvas.Save();
            canvas.Translate((float)(left + width / 2), 0);
            canvas.Scale(-1, 1);
            canvas.DrawBitmap(
                bitmap,
                new SKRect(0, 0, bitmap.Width, bitmap.Height),
                new SKRect((float)(-width / 2), (float)top, (float)(width / 2), (float)(top + height)),
                paint);
            canvas.Restore();
        }
        else
        {
            canvas.DrawBitmap(
                bitmap,
                new SKRect(0, 0, bitmap.Width, bitmap.Height),
                new SKRect((float)left, (float)top, (float)(left + width), (float)(top + height)),
                paint);
        }
    }

    private void DrawParticles(SKCanvas canvas, PetSnapshot s)
    {
        if (s.Particles.Count == 0)
        {
            return;
        }

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach (ParticleSnapshot p in s.Particles)
        {
            byte alpha = (byte)Math.Clamp(p.Alpha * 255, 0, 255);
            paint.Color = new SKColor(p.Color.R, p.Color.G, p.Color.B, alpha);
            switch (p.Shape)
            {
                case ParticleShape.Star:
                    DrawStar(canvas, (float)p.X, (float)p.Y, (float)p.Size * 1.6f, paint);
                    break;
                case ParticleShape.Heart:
                    DrawHeart(canvas, (float)p.X, (float)p.Y, (float)p.Size * 1.7f, paint);
                    break;
                default:
                    canvas.DrawCircle((float)p.X, (float)p.Y, (float)p.Size, paint);
                    break;
            }
        }
    }

    private static void DrawStar(SKCanvas canvas, float cx, float cy, float r, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(cx, cy - r);
        path.LineTo(cx + r * 0.28f, cy - r * 0.28f);
        path.LineTo(cx + r, cy);
        path.LineTo(cx + r * 0.28f, cy + r * 0.28f);
        path.LineTo(cx, cy + r);
        path.LineTo(cx - r * 0.28f, cy + r * 0.28f);
        path.LineTo(cx - r, cy);
        path.LineTo(cx - r * 0.28f, cy - r * 0.28f);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private static void DrawHeart(SKCanvas canvas, float cx, float cy, float r, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(cx, cy + r * 0.75f);
        path.CubicTo(cx - r * 1.5f, cy - r * 0.2f, cx - r * 0.7f, cy - r * 1.15f, cx, cy - r * 0.42f);
        path.CubicTo(cx + r * 0.7f, cy - r * 1.15f, cx + r * 1.5f, cy - r * 0.2f, cx, cy + r * 0.75f);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    // ---------------------------------------------------------------- bubble

    private void DrawBubble(SKCanvas canvas, PetSnapshot s)
    {
        BubbleSnapshot? bubble = s.Bubble;
        if (bubble == null || bubble.Alpha <= 0.01 || string.IsNullOrWhiteSpace(bubble.Text))
        {
            return;
        }

        float fontSize = _bubbleFontSize;
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = _candyTypeface,
            TextSize = fontSize,
            Color = new SKColor(0x2B, 0x24, 0x36),
            TextAlign = SKTextAlign.Center,
        };

        float maxTextWidth = 252;
        List<string> lines = WrapText(bubble.Text, textPaint, maxTextWidth);
        if (lines.Count > 3)
        {
            lines = lines.Take(3).ToList();
            lines[^1] = TrimToWidth(lines[^1] + "…", textPaint, maxTextWidth);
        }

        float lineHeight = fontSize + 3.6f;
        float textWidth = lines.Count == 0 ? 40 : lines.Max(l => textPaint.MeasureText(l));
        float boxWidth = Math.Min(280, textWidth + 20);
        float boxHeight = lines.Count * lineHeight + 12;
        float cx = (float)Math.Clamp(PetLeft(s) + SpriteWidth(s) / 2, boxWidth / 2 + 8, DesignWidth - boxWidth / 2 - 8);
        float petHead = (float)(PetTop(s) + SpriteHeight(s) * 0.16);
        float boxLeft = cx - boxWidth / 2;
        float boxTop = Math.Clamp(petHead - boxHeight - 8, 30, (float)(s.FloorY - boxHeight - 18));

        using var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 60),
            Style = SKPaintStyle.Fill,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4),
        };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(boxLeft + 1, boxTop + 2, boxLeft + boxWidth + 1, boxTop + boxHeight + 2), 8, 8), shadow);

        using var fill = new SKPaint
        {
            IsAntialias = true,
            Color = _bubbleColor.WithAlpha((byte)Math.Clamp(bubble.Alpha * 245, 0, 245)),
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(boxLeft, boxTop, boxLeft + boxWidth, boxTop + boxHeight), 8, 8), fill);

        using var border = new SKPaint
        {
            IsAntialias = true,
            Color = _accent.WithAlpha((byte)Math.Clamp(bubble.Alpha * 150, 0, 150)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(boxLeft, boxTop, boxLeft + boxWidth, boxTop + boxHeight), 8, 8), border);

        // 小尾巴指向宠物头顶。
        using var tail = new SKPath();
        float tailX = Math.Clamp(cx, boxLeft + 18, boxLeft + boxWidth - 18);
        tail.MoveTo(tailX - 5, boxTop + boxHeight - 1);
        tail.LineTo(tailX + 5, boxTop + boxHeight - 1);
        tail.LineTo(tailX, boxTop + boxHeight + 7);
        tail.Close();
        canvas.DrawPath(tail, fill);

        float textY = boxTop + 6 + fontSize;
        for (int i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], cx, textY + i * lineHeight, textPaint);
        }
    }

    private void DrawMeme(SKCanvas canvas, PetSnapshot s)
    {
        if (string.IsNullOrWhiteSpace(s.MemeName) || s.MemeSecondsRemaining <= 0)
        {
            return;
        }

        string safeName = Path.GetFileName(s.MemeName);
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "memes", safeName + ".png");
        SKBitmap? bitmap = _frames.Get(path);
        if (bitmap == null)
        {
            return;
        }

        float size = (float)Math.Clamp(s.MemeSize, 56, 160);
        float left = DesignWidth - size - 10;
        float top = 66;
        float alpha = (float)Math.Clamp(s.MemeSecondsRemaining / 0.3, 0, 1);
        var card = new SKRect(left, top, left + size, top + size);

        using var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, (byte)(90 * alpha)),
            Style = SKPaintStyle.Fill,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f),
        };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(card.Left + 2, card.Top + 3, card.Right + 2, card.Bottom + 3), 9, 9), shadow);

        using var paper = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 252, 244, (byte)(248 * alpha)),
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawRoundRect(new SKRoundRect(card, 9, 9), paper);

        using var border = new SKPaint
        {
            IsAntialias = true,
            Color = _accent.WithAlpha((byte)(120 * alpha)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
        };
        canvas.DrawRoundRect(new SKRoundRect(card, 9, 9), border);

        float pad = 4;
        var inner = new SKRect(card.Left + pad, card.Top + pad, card.Right - pad, card.Bottom - pad);
        float scale = Math.Min(inner.Width / bitmap.Width, inner.Height / bitmap.Height);
        float drawW = bitmap.Width * scale;
        float drawH = bitmap.Height * scale;
        var dest = new SKRect(
            inner.MidX - drawW / 2,
            inner.MidY - drawH / 2,
            inner.MidX + drawW / 2,
            inner.MidY + drawH / 2);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
            Color = SKColors.White.WithAlpha((byte)(255 * alpha)),
        };
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(inner, 6, 6), antialias: true);
        canvas.DrawBitmap(bitmap, dest, paint);
        canvas.Restore();
    }

    private static List<string> WrapText(string text, SKPaint paint, float maxWidth)
    {
        var result = new List<string>();
        foreach (string paragraph in text.Split(LineSeparators))
        {
            if (paragraph.Length == 0)
            {
                result.Add("");
                continue;
            }

            string current = "";
            foreach (char ch in paragraph)
            {
                string candidate = current + ch;
                if (current.Length > 0 && paint.MeasureText(candidate) > maxWidth)
                {
                    result.Add(current);
                    current = ch.ToString();
                }
                else
                {
                    current = candidate;
                }
            }

            if (current.Length > 0)
            {
                result.Add(current);
            }
        }

        return result;
    }

    private static string TrimToWidth(string text, SKPaint paint, float maxWidth)
    {
        if (paint.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        while (text.Length > 1 && paint.MeasureText(text) > maxWidth)
        {
            text = text[..^2] + "…";
        }

        return text;
    }

    // ---------------------------------------------------------------- sleep/hud

    private void DrawSleepVeil(SKCanvas canvas, PetSnapshot s, DateTime now)
    {
        using var veil = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(6, 10, 26, 72),
        };
        canvas.DrawRect(new SKRect(0, 0, DesignWidth, DesignHeight), veil);

        using var z = new SKPaint
        {
            IsAntialias = true,
            Typeface = _uiTypeface,
            TextSize = 15,
            Color = _accent.WithAlpha(210),
            TextAlign = SKTextAlign.Center,
        };
        double t = now.TimeOfDay.TotalSeconds;
        float baseX = (float)(PetLeft(s) + SpriteWidth(s) * 0.72);
        float baseY = (float)(PetTop(s) + SpriteHeight(s) * 0.28);
        for (int i = 0; i < 3; i++)
        {
            float y = baseY - i * 13 - (float)((Math.Sin(t * 1.1 + i) + 1) * 2);
            z.Color = _accent.WithAlpha((byte)(70 + i * 55));
            z.TextSize = 9 + i * 3.5f;
            canvas.DrawText("z", baseX + i * 9, y, z);
        }
    }

    // ---------------------------------------------------------------- xiaowai

    private void DrawXiaowai(SKCanvas canvas, PetSnapshot s)
    {
        XiaowaiView? x = s.Xiaowai;
        if (x is null)
        {
            return;
        }

        bool browsingBili = string.Equals(x.State, "browsing_bili", StringComparison.Ordinal);
        using SKBitmap? cover = browsingBili && _showCover && s.ShowCover ? CloneCover() : null;

        if (s.ShowStatusCapsule)
        {
            string label = string.IsNullOrWhiteSpace(x.Label) ? x.State : x.Label;
            string? displayName = string.IsNullOrWhiteSpace(s.DisplayName) ? null : s.DisplayName!.Trim();
            string pillLabel = displayName is null ? label : displayName + " · " + label;
            SKColor pillColor = x.State switch
            {
                "chatting" => new SKColor(0xFF, 0x8F, 0xB8),
                "browsing_bili" => new SKColor(0x5A, 0xB6, 0xF0),
                _ => new SKColor(0xB6, 0xC0, 0xD0),
            };

            using var pillText = new SKPaint
            {
                IsAntialias = true,
                Typeface = _uiTypeface,
                TextSize = 12,
                Color = SKColors.White,
                TextAlign = SKTextAlign.Left,
            };
            pillLabel = TrimToWidth(pillLabel, pillText, 280);
            float pillWidth = pillText.MeasureText(pillLabel) + 16;
            using var pillPaint = new SKPaint
            {
                IsAntialias = true,
                Color = pillColor,
                Style = SKPaintStyle.Fill,
            };
            var pill = new SKRect(8, 8, 8 + pillWidth, 28);
            canvas.DrawRoundRect(new SKRoundRect(pill, 10, 10), pillPaint);
            canvas.DrawText(pillLabel, 16, 22.5f, pillText);
        }

        string detail = x.Detail;
        using var detailPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = _uiTypeface,
            TextSize = 10.5f,
            Color = new SKColor(0x6B, 0x72, 0x80),
            TextAlign = SKTextAlign.Left,
        };
        string detailText = s.ShowIdleDetail ? TrimToWidth(detail, detailPaint, 200) : "";
        if (s.ShowIdleDetail)
        {
            canvas.DrawText(detailText, 8, 41, detailPaint);
        }

        if (s.ShowDaysTogether && x.DaysTogether > 0)
        {
            using var daysPaint = new SKPaint
            {
                IsAntialias = true,
                Typeface = _uiTypeface,
                TextSize = 10.5f,
                Color = new SKColor(0x9A, 0xA3, 0xB2),
                TextAlign = SKTextAlign.Left,
            };
            canvas.DrawText($"第 {x.DaysTogether} 天 · 今天聊了 {x.TodayMsgs} 句", 8, 55, daysPaint);
        }

        if (cover is null)
        {
            if (browsingBili)
            {
                string inlineText = BudgetText(x, s.ShowBiliProgress, s.ShowNextProactive);
                if (inlineText.Length == 0)
                {
                    return;
                }

                using var inlineBudget = new SKPaint
                {
                    IsAntialias = true,
                    Typeface = _uiTypeface,
                    TextSize = 9,
                    Color = new SKColor(0x37, 0x41, 0x51),
                    TextAlign = SKTextAlign.Left,
                };
                float start = 8 + detailPaint.MeasureText(detailText) + 8;
                float width = 312 - start;
                if (width > 24)
                {
                    canvas.DrawText(TrimToWidth(inlineText, inlineBudget, width), start, 41, inlineBudget);
                }
            }

            return;
        }

        var coverRect = new SKRect(216, 8, 312, 62);
        using var coverPaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
        };
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(coverRect, 8, 8), antialias: true);
        canvas.DrawBitmap(cover, new SKRect(0, 0, cover.Width, cover.Height), coverRect, coverPaint);
        canvas.Restore();

        using var border = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
        };
        canvas.DrawRoundRect(new SKRoundRect(coverRect, 8, 8), border);

        if (!string.IsNullOrWhiteSpace(x.VideoTitle))
        {
            using var titlePaint = new SKPaint
            {
                IsAntialias = true,
                Typeface = _uiTypeface,
                TextSize = 10,
                Color = new SKColor(0x37, 0x41, 0x51),
                TextAlign = SKTextAlign.Left,
            };

            List<string> lines = WrapText(x.VideoTitle, titlePaint, 96);
            if (lines.Count > 2)
            {
                lines = lines.Take(2).ToList();
                lines[1] = TrimToWidth(lines[1] + "…", titlePaint, 96);
            }

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Length > 12 ? lines[i][..12] : lines[i];
                canvas.DrawText(TrimToWidth(line, titlePaint, 96), 216, 75 + i * 12, titlePaint);
            }
        }

        string budgetText = BudgetText(x, s.ShowBiliProgress, s.ShowNextProactive);
        if (budgetText.Length > 0)
        {
            using var budgetPaint = new SKPaint
            {
                IsAntialias = true,
                Typeface = _uiTypeface,
                TextSize = 8,
                Color = new SKColor(0x37, 0x41, 0x51),
                TextAlign = SKTextAlign.Left,
            };
            canvas.DrawText(TrimToWidth(budgetText, budgetPaint, 96), 216, 99, budgetPaint);
        }
    }

    private SKBitmap? CloneCover()
    {
        lock (_coverLock)
        {
            if (_coverBitmap is null)
            {
                return null;
            }

            try
            {
                return _coverBitmap.Copy();
            }
            catch
            {
                return null;
            }
        }
    }

    private static string BudgetText(XiaowaiView x, bool showBili, bool showNextProactive)
    {
        var parts = new List<string>();
        if (showBili)
        {
            string bili = $"刷B站 {x.BiliTodayMin:0}/{x.BiliTodayLimit:0} 分";
            if (x.NextBiliInMin.HasValue)
            {
                bili += $" · 下次{x.NextBiliInMin.Value:0}分";
            }

            parts.Add(bili);
        }

        if (showNextProactive && x.NextProactiveInMin.HasValue)
        {
            parts.Add($"下次搭话{x.NextProactiveInMin.Value:0}分");
        }

        return string.Join(" · ", parts);
    }

    // ---------------------------------------------------------------- helpers

    private SKBitmap GetSurface(int width, int height)
    {
        if (_surfaces.TryGetValue((width, height), out SKBitmap? bitmap))
        {
            return bitmap;
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap = new SKBitmap(info);
        _surfaces[(width, height)] = bitmap;
        return bitmap;
    }

    private static SKColor Parse(string text, SKColor fallback)
        => SKColor.TryParse(text, out SKColor color) ? color : fallback;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_coverLock)
        {
            _coverBitmap?.Dispose();
            _coverBitmap = null;
            _coverBvid = null;
        }

        foreach (SKBitmap bitmap in _surfaces.Values)
        {
            bitmap.Dispose();
        }

        _surfaces.Clear();
        ReleaseTypeface(_candyTypeface);
        if (!ReferenceEquals(_uiTypeface, _candyTypeface))
        {
            ReleaseTypeface(_uiTypeface);
        }

        _candyTypeface = SKTypeface.Default;
        _uiTypeface = SKTypeface.Default;
    }

    private static void ReleaseTypeface(SKTypeface? typeface)
    {
        if (typeface == null || ReferenceEquals(typeface, SKTypeface.Default))
        {
            return;
        }

        typeface.Dispose();
    }
}
