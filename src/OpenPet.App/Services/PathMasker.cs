using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace OpenPet.App.Services;

/// <summary>
/// 把日志和界面上可能出现的本机路径、内网地址替换成占位符，
/// 让日志、截图天然干净。纯字符串处理，不读写磁盘。
/// </summary>
public static class PathMasker
{
    private const string UserProfileTag = "%USERPROFILE%";
    private const string ProjectTag = "<工程>";
    private const string LanIpTag = "<内网IP>";
    private const string NasTag = "<NAS>";
    private const int CacheLimit = 4096;

    // 绝对用户目录（盘符下的 Users 目录）。
    private static readonly Regex UserProfileRx = new(
        @"[A-Za-z]:\\Users\\[^\\]+\\",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 用户目录恰好落在字符串末尾（没有尾部反斜杠）时也要处理。
    private static readonly Regex UserProfileTailRx = new(
        @"[A-Za-z]:\\Users\\[^\\\s]+(?=\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 私有网段 IPv4（192.168/16、10/8、172.16-31/12）；127.0.0.1 不在此列，保持原样。
    private static readonly Regex LanIpRx = new(
        @"\b(?:192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, string> MaskCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> UrlCache = new(StringComparer.Ordinal);

    // 进程所在目录往上找，第一层名字含 Project 的目录即工程根。
    private static readonly string? ProjectRoot = FindProjectRoot();

    /// <summary>日志/界面文本脱敏：用户目录 → %USERPROFILE%，工程根 → &lt;工程&gt;，内网 IPv4 → &lt;内网IP&gt;。</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        if (MaskCache.TryGetValue(text, out string? cached))
        {
            return cached;
        }

        string masked = MaskCore(text);
        if (MaskCache.Count >= CacheLimit)
        {
            MaskCache.Clear();
        }

        MaskCache[text] = masked;
        return masked;
    }

    /// <summary>用于展示 NAS 地址：内网 IPv4 → &lt;NAS&gt;，端口与其余部分保持原样。</summary>
    public static string MaskUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url ?? string.Empty;
        }

        if (UrlCache.TryGetValue(url, out string? cached))
        {
            return cached;
        }

        string masked = LanIpRx.Replace(url, NasTag);
        if (UrlCache.Count >= CacheLimit)
        {
            UrlCache.Clear();
        }

        UrlCache[url] = masked;
        return masked;
    }

    private static string MaskCore(string text)
    {
        string result = UserProfileRx.Replace(text, UserProfileTag + "\\");
        result = UserProfileTailRx.Replace(result, UserProfileTag);

        if (!string.IsNullOrEmpty(ProjectRoot))
        {
            result = result.Replace(ProjectRoot, ProjectTag, StringComparison.OrdinalIgnoreCase);
            string slashRoot = ProjectRoot.Replace('\\', '/');
            result = result.Replace(slashRoot, ProjectTag, StringComparison.OrdinalIgnoreCase);
        }

        return LanIpRx.Replace(result, LanIpTag);
    }

    private static string? FindProjectRoot()
    {
        try
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (dir.Name.Contains("Project", StringComparison.OrdinalIgnoreCase))
                {
                    return dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // 取不到就退化为只处理用户目录和内网 IP。
        }

        return null;
    }
}
