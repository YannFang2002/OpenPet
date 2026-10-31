using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenPet.App.Models;
using SkiaSharp;

namespace OpenPet.App.Pet;

/// <summary>一段已经转好的动画：一组带 alpha 的 WebP 帧 + meta.json。</summary>
public sealed class AnimationClip
{
    public required string Name { get; init; }
    public required int Fps { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string[] FramePaths { get; init; }

    public int FrameCount => FramePaths.Length;

    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)Fps);

    public int FrameIndexAt(TimeSpan elapsed, bool loop)
    {
        if (FrameCount == 0)
        {
            return 0;
        }

        double seconds = Math.Max(0, elapsed.TotalSeconds);
        int index = (int)Math.Floor(seconds * Fps);
        if (loop)
        {
            return ((index % FrameCount) + FrameCount) % FrameCount;
        }

        return Math.Clamp(index, 0, FrameCount - 1);
    }
}

/// <summary>扫描 assets/animations，按动画名加载帧序列。</summary>
public sealed class AnimationLibrary
{
    private readonly Dictionary<string, AnimationClip> _clips = new(StringComparer.Ordinal);

    public static AnimationLibrary Load(string directory, ICollection<string>? warnings = null)
    {
        var library = new AnimationLibrary();
        if (!Directory.Exists(directory))
        {
            throw new PetConfigException($"动画目录不存在：{directory}");
        }

        foreach (string clipDir in Directory.EnumerateDirectories(directory))
        {
            string metaPath = Path.Combine(clipDir, "meta.json");
            if (!File.Exists(metaPath))
            {
                warnings?.Add($"动画目录缺少 meta.json，已跳过：{Path.GetFileName(clipDir)}");
                continue;
            }

            AnimationClip? clip = ReadClip(clipDir, metaPath, warnings);
            if (clip != null)
            {
                library._clips[clip.Name] = clip;
            }
        }

        if (library._clips.Count == 0)
        {
            throw new PetConfigException(
                $"没有找到任何动画。请先运行 tools/prepare_animations.py 生成帧序列（{directory}）。");
        }

        return library;
    }

    private static AnimationClip? ReadClip(string dir, string metaPath, ICollection<string>? warnings)
    {
        try
        {
            string json = File.ReadAllText(metaPath);
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            JsonElement root = doc.RootElement;

            string name = root.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? Path.GetFileName(dir)
                : Path.GetFileName(dir);
            int fps = ReadInt(root, "fps", 12);
            int width = ReadInt(root, "frameWidth", 320);
            int height = ReadInt(root, "frameHeight", 180);

            string[] frames = Directory.GetFiles(dir, "*.webp", SearchOption.TopDirectoryOnly);
            Array.Sort(frames, StringComparer.OrdinalIgnoreCase);
            if (frames.Length == 0)
            {
                warnings?.Add($"动画没有帧文件，已跳过：{name}");
                return null;
            }

            if (fps <= 0)
            {
                warnings?.Add($"动画 {name} 的 fps 非法，已按 12 处理。");
                fps = 12;
            }

            return new AnimationClip
            {
                Name = name,
                Fps = fps,
                Width = width,
                Height = height,
                FramePaths = frames,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warnings?.Add($"读取动画 {Path.GetFileName(dir)} 失败：{ex.Message}");
            return null;
        }
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out JsonElement el) && el.TryGetInt32(out int value)
            ? value
            : fallback;

    public bool TryGet(string name, out AnimationClip clip) => _clips.TryGetValue(name, out clip!);

    public bool Contains(string name) => _clips.ContainsKey(name);

    public AnimationClip? Find(string name) => _clips.TryGetValue(name, out AnimationClip? clip) ? clip : null;

    public IReadOnlyCollection<string> Names => _clips.Keys;

    public IEnumerable<AnimationClip> Clips => _clips.Values;
}

/// <summary>
/// 已解码帧的小型 LRU 缓存。动画只有 120 帧左右，按需解码即可；
/// 缓存上限用帧数控制（180 帧 ≈ 41 MB）。
/// </summary>
public sealed class FrameCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _lru = new();
    private readonly int _capacity;
    private bool _disposed;

    private sealed class Entry
    {
        public required string Path { get; init; }
        public required SKBitmap Bitmap { get; init; }
    }

    public FrameCache(int capacity = 180)
    {
        _capacity = Math.Max(8, capacity);
    }

    public SKBitmap? Get(string path)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_map.TryGetValue(path, out LinkedListNode<Entry>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }

            SKBitmap? bitmap = Decode(path);
            if (bitmap == null)
            {
                return null;
            }

            var entry = new Entry { Path = path, Bitmap = bitmap };
            LinkedListNode<Entry> added = _lru.AddFirst(entry);
            _map[path] = added;

            while (_lru.Count > _capacity)
            {
                LinkedListNode<Entry>? last = _lru.Last;
                if (last == null)
                {
                    break;
                }

                _lru.RemoveLast();
                _map.Remove(last.Value.Path);
                last.Value.Bitmap.Dispose();
            }

            return bitmap;
        }
    }

    private static SKBitmap? Decode(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            SKBitmap? bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null)
            {
                return null;
            }

            return bitmap;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (Entry entry in _lru)
            {
                entry.Bitmap.Dispose();
            }

            _lru.Clear();
            _map.Clear();
        }
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
            Clear();
        }
    }
}
