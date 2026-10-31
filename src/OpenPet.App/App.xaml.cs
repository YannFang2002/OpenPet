using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using OpenPet.App.Services;

namespace OpenPet.App;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless diagnostics (--snapshot/--screen-test) must be able to run
        // while the normal GUI instance is up, so they bypass the single-instance mutex.
        if (Headless.IsHeadless(e.Args))
        {
            Headless.TryRun(e.Args);
            Shutdown(Headless.ExitCode);
            return;
        }

        _mutex = new Mutex(true, "OpenPet.SingleInstance.v1", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "OpenPet 已经在运行了。\n请从系统托盘双击图标把窗口叫出来。",
                "OpenPet",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("UnhandledException: " + args.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("UnobservedTaskException: " + args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, _) =>
        {
            if (MainWindow is MainWindow window)
            {
                window.ForceClose();
            }
        };

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Write("启动窗口失败：" + ex);
            MessageBox.Show(PathMasker.Mask(ex.Message), "OpenPet 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("DispatcherUnhandledException: " + e.Exception);
        MessageBox.Show(
            "OpenPet 遇到了一个问题：\n" + PathMasker.Mask(e.Exception.Message) + "\n\n详细信息已写入日志。",
            "OpenPet",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
