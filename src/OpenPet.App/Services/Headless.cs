using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OpenPet.App.Models;
using OpenPet.App.Pet;

namespace OpenPet.App.Services;

/// <summary>
/// 无窗口模式，便于自动化验证：
///   OpenPet.exe --snapshot [out.png] [--config path]
///   OpenPet.exe --screen-test [seconds] [--config path]
/// 结果写入 %LocalAppData%\OpenPet\headless.log。
/// </summary>
public static class Headless
{
    public static int ExitCode { get; private set; }

    public static bool IsHeadless(string[] args)
        => args.Length > 0
           && args[0].ToLowerInvariant() is "--snapshot" or "--screen-test";

    public static bool TryRun(string[] args)
    {
        if (!IsHeadless(args))
        {
            return false;
        }

        string mode = args[0].ToLowerInvariant();

        try
        {
            string? config = ReadOption(args, "--config");
            if (mode == "--snapshot")
            {
                RunSnapshot(args, config);
            }
            else
            {
                RunScreenTest(args, config);
            }

            ExitCode = 0;
        }
        catch (Exception ex)
        {
            Log.Write("Headless 失败：" + ex);
            ExitCode = 1;
        }

        return true;
    }

    private static void RunSnapshot(string[] args, string? config)
    {
        string output = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[1])
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenPet",
                "snapshot.png");

        string? directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        double at = 0.15;
        if (ReadOption(args, "--at") is string atText && double.TryParse(atText, out double parsedAt))
        {
            at = Math.Clamp(parsedAt, 0, 600);
        }

        HeadlessContext runtime = Load(config);
        using var frameCache = new FrameCache();
        using var renderer = new PetSceneRenderer(frameCache);
        renderer.Configure(runtime.Loaded.Config);
        var engine = new PetEngine(runtime.Loaded, runtime.Library, runtime.Memes, Log.Write);

        // 快照沿用实时远端状态，这样名字/状态胶囊与真机一致；指定 --edge 时只做边缘姿势截图。
        string? edgeArg = ReadOption(args, "--edge");
        XiaowaiConfig xw = runtime.Loaded.Config.Xiaowai;
        if (edgeArg is null && xw.Enabled && !string.IsNullOrWhiteSpace(xw.Url))
        {
            try
            {
                using var client = new XiaowaiClient(xw.Url, xw.TimeoutSec);
                XiaowaiView? remote = client.FetchAsync().GetAwaiter().GetResult();
                if (remote is not null)
                {
                    engine.ApplyRemoteState(remote, bubbleOnChange: false);
                }
            }
            catch
            {
                // 拉不到远端时退回纯本地快照。
            }
        }

        if (edgeArg is not null)
        {
            int side = edgeArg.ToLowerInvariant() switch
            {
                "left" or "l" or "-1" => -1,
                "right" or "r" or "1" => 1,
                _ => 0,
            };
            if (side != 0)
            {
                engine.ForceEdgeRestForSnapshot(side);
            }
        }

        if (ReadOption(args, "--meme") is string meme && !string.IsNullOrWhiteSpace(meme))
        {
            engine.ShowMeme(meme);
        }

        engine.Tick(DateTime.Now.AddSeconds(at));
        renderer.SavePng(engine.CreateSnapshot(), output);

        string note = $"snapshot: {output} (at={at:0.00}s)";
        Log.Write(note);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(output) ?? ".", "headless.log"),
            note + Environment.NewLine);
    }

    private static void RunScreenTest(string[] args, string? config)
    {
        int seconds = 8;
        if (args.Length > 1
            && !args[1].StartsWith("--", StringComparison.Ordinal)
            && int.TryParse(args[1], out int parsed))
        {
            seconds = Math.Clamp(parsed, 2, 60);
        }

        HeadlessContext runtime = Load(config);
        using var frameCache = new FrameCache();
        using var renderer = new PetSceneRenderer(frameCache);
        renderer.Configure(runtime.Loaded.Config);
        var engine = new PetEngine(runtime.Loaded, runtime.Library, runtime.Memes, Log.Write);
        using var display = new DisplayService(engine, renderer);

        var lines = new List<string>();
        int connectedFlag = 0;
        display.Log += message =>
        {
            lock (lines)
            {
                lines.Add($"{DateTime.Now:HH:mm:ss} {message}");
            }

            if (message.Contains("已连接", StringComparison.Ordinal))
            {
                Volatile.Write(ref connectedFlag, 1);
            }
        };

        string imagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenPet",
            "screen-test-frame.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        renderer.SavePng(engine.CreateSnapshot(), imagePath);

        display.Start();
        DateTime deadline = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < deadline)
        {
            engine.Tick(DateTime.Now);
            Thread.Sleep(100);
        }

        display.Stop();
        bool connected = Volatile.Read(ref connectedFlag) == 1;
        string report = string.Join(Environment.NewLine, lines);
        string reportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenPet",
            "headless.log");
        File.WriteAllText(
            reportPath,
            $"screen-test seconds={seconds} connected={connected} sendFps={display.SendFps} " +
            $"rendered={imagePath}{Environment.NewLine}{report}{Environment.NewLine}");
        Log.Write($"screen-test done connected={connected}");
        if (!connected)
        {
            throw new InvalidOperationException("屏幕未连接，详见 " + reportPath);
        }
    }

    private sealed record HeadlessContext(
        LoadedPetConfig Loaded,
        AnimationLibrary Library,
        MemeLibrary Memes);

    private static HeadlessContext Load(string? config)
    {
        var warnings = new List<string>();
        LoadedPetConfig loaded = PetConfigLoader.Load(config, warnings);
        string animationRoot = Path.Combine(AppContext.BaseDirectory, "assets", "animations");
        AnimationLibrary library = AnimationLibrary.Load(animationRoot);
        string memeRoot = Path.Combine(AppContext.BaseDirectory, "assets", "memes");
        MemeLibrary memes = MemeLibrary.Load(memeRoot);

        foreach (string warning in warnings)
        {
            Log.Write("配置警告：" + warning);
        }

        return new HeadlessContext(loaded, library, memes);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
