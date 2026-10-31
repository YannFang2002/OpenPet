using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenPet.App.Pet;

/// <summary>assets/memes 下的 384×384 表情包贴纸。</summary>
public sealed class MemeLibrary
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

    public static MemeLibrary Load(string directory, ICollection<string>? warnings = null)
    {
        var library = new MemeLibrary();
        if (!Directory.Exists(directory))
        {
            warnings?.Add($"表情包目录不存在：{directory}");
            return library;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(name))
            {
                library._paths[name] = path;
            }
        }

        return library;
    }

    public int Count => _paths.Count;

    public IReadOnlyList<string> Names => _paths.Keys.OrderBy(n => n, StringComparer.CurrentCulture).ToArray();

    public bool TryGetPath(string name, out string path) => _paths.TryGetValue(name, out path!);

    public string? PickRandom(Random random)
    {
        if (_paths.Count == 0)
        {
            return null;
        }

        int index = random.Next(_paths.Count);
        return _paths.Keys.ElementAt(index);
    }
}
