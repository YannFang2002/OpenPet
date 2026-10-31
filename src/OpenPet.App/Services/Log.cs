using System;
using System.IO;

namespace OpenPet.App.Services;

/// <summary>简单文件日志：%LocalAppData%\OpenPet\app.log，超过 2 MB 时滚动一次。</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPet");

    public static string FilePath => Path.Combine(Directory, "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                string path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    string old = path + ".1";
                    if (File.Exists(old))
                    {
                        File.Delete(old);
                    }

                    File.Move(path, old);
                }

                // 落盘前统一脱敏：本机路径、用户名目录、内网地址都不进日志。
                string masked = PathMasker.Mask(message);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {masked}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志永远不能影响宠物。
        }
    }
}
