using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenPet.App.Pet;
using OpenPet.Core.Encoding;
using OpenPet.Core.Imaging;
using OpenPet.Core.Rendering;
using OpenPet.Devices;
using OpenPet.Devices.Protocol;

namespace OpenPet.App.Services;

public sealed record PreviewFrame(byte[] Bgra, int Width, int Height, DateTime Timestamp);

/// <summary>
/// 后台渲染/推流线程：每 50 ms tick 一次宠物引擎并以 20 fps 推 RGB565 帧到屏幕。
/// 屏幕没插、被 TRCC 占用或投屏关闭时自动退化为窗口预览，并定期重连。
/// </summary>
public sealed class DisplayService : IDisposable
{
    private readonly PetEngine _engine;
    private readonly PetSceneRenderer _renderer;
    private readonly object _gate = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _streaming = true;
    private volatile bool _disposed;

    private ThermalrightType2? _device;
    private DeviceProfile? _profile;
    private ThermalrightType2? _pendingDevice;
    private DeviceProfile? _pendingProfile;
    private readonly FramePump _pump = new(TimeSpan.FromSeconds(1.2));
    private volatile bool _connecting;
    private DateTime _nextConnectAttempt = DateTime.MinValue;
    private string _lastStatus = "";

    private int _renderCount;
    private int _sendCount;
    private DateTime _fpsWindow = DateTime.Now;
    private int _renderFps;
    private int _sendFps;

    public DisplayService(PetEngine engine, PetSceneRenderer renderer)
    {
        _engine = engine;
        _renderer = renderer;
    }

    public event Action<PreviewFrame>? PreviewReady;

    public event Action<string>? StatusChanged;

    public event Action<string>? Log;

    public bool IsStreaming => _streaming;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _device != null;
            }
        }
    }

    public int RenderFps => Volatile.Read(ref _renderFps);

    public int SendFps => Volatile.Read(ref _sendFps);

    public string? DeviceSummary
    {
        get
        {
            lock (_gate)
            {
                return _profile?.ToString();
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _thread != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _thread = new Thread(() => Run(_cts.Token))
            {
                IsBackground = true,
                Name = "OpenPet.Display",
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            thread = _thread;
            cts = _cts;
            _thread = null;
            _cts = null;
        }

        if (thread == null)
        {
            return;
        }

        cts?.Cancel();
        thread.Join(TimeSpan.FromSeconds(4));
        cts?.Dispose();
    }

    public void SetStreaming(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _streaming = enabled;
        if (!enabled)
        {
            Report("投屏已关闭，仅显示预览");
        }
        else
        {
            _nextConnectAttempt = DateTime.MinValue;
            Report("正在查找屏幕…");
        }
    }

    private void Run(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        DateTime lastPreview = DateTime.MinValue;

        try
        {
            while (!token.IsCancellationRequested)
            {
                TimeSpan frameStart = clock.Elapsed;
                DateTime now = DateTime.Now;

                _engine.Tick(now);
                PetSnapshot snapshot = _engine.CreateSnapshot();

                AdoptPendingDevice();
                if (!_streaming)
                {
                    if (_device != null)
                    {
                        Disconnect("投屏已关闭，仅显示预览");
                    }
                }
                else if (_device == null && !_connecting && now >= _nextConnectAttempt)
                {
                    StartConnect();
                }

                int width = 320;
                int height = 240;
                byte[] bgra = _renderer.Render(snapshot, width, height);
                Interlocked.Increment(ref _renderCount);

                if ((now - lastPreview).TotalMilliseconds >= 90)
                {
                    lastPreview = now;
                    PreviewReady?.Invoke(new PreviewFrame(bgra, width, height, now));
                }

                if (_device != null && _profile != null)
                {
                    SendFrame(snapshot, bgra);
                }

                UpdateFps(now);

                int elapsedMs = (int)(clock.Elapsed - frameStart).TotalMilliseconds;
                int wait = 50 - elapsedMs;
                if (wait > 0)
                {
                    token.WaitHandle.WaitOne(wait);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Report($"显示线程异常：{ex.Message}");
            Log?.Invoke(ex.ToString());
        }
        finally
        {
            Disconnect(null);
            lock (_gate)
            {
                _pendingDevice?.Dispose();
                _pendingDevice = null;
                _pendingProfile = null;
            }
        }
    }

    private void StartConnect()
    {
        _connecting = true;
        Report("正在连接屏幕…");

        Task.Run(() =>
        {
            ThermalrightType2? device = null;
            try
            {
                var targets = DeviceEnumerator.FindHidTargets();
                if (targets.Count == 0)
                {
                    throw new DeviceNotFoundException("未找到 0416:5302 屏幕（请检查 9Pin 线与驱动）");
                }

                var transport = HidTransport.Open(targets[0].Path, reportId: 0, pipelineDepth: 2);
                device = new ThermalrightType2(transport) { SettleMs = 0 };
                HandshakeResult handshake = device.Handshake(HandshakeMode.Auto);
                DeviceProfile profile = DeviceCatalog.ResolveOrDefault(
                    handshake.ProductId ?? -1,
                    handshake.SubVersion ?? -1);

                lock (_gate)
                {
                    if (!_streaming || _disposed)
                    {
                        device.Dispose();
                        device = null;
                        return;
                    }

                    _pendingDevice?.Dispose();
                    _pendingDevice = device;
                    _pendingProfile = profile;
                    device = null;
                }
            }
            catch (DeviceBusyException busy)
            {
                Report("屏幕被 TRCC / OpenTRCC 占用，请关闭后再试（已退化为预览）");
                Log?.Invoke(busy.Message);
            }
            catch (DeviceNotFoundException missing)
            {
                Report("未找到屏幕，已退化为预览");
                Log?.Invoke(missing.Message);
            }
            catch (Exception ex)
            {
                Report($"连接失败：{ex.Message}");
                Log?.Invoke(ex.ToString());
            }
            finally
            {
                device?.Dispose();
                _connecting = false;
                _nextConnectAttempt = DateTime.Now.AddSeconds(5);
            }
        });
    }

    private void AdoptPendingDevice()
    {
        ThermalrightType2? adopted = null;
        DeviceProfile? profile = null;
        lock (_gate)
        {
            if (_pendingDevice == null)
            {
                return;
            }

            adopted = _pendingDevice;
            profile = _pendingProfile;
            _pendingDevice = null;
            _pendingProfile = null;
        }

        _device?.Dispose();
        _device = adopted;
        _profile = profile;
        _pump.Reset();

        if (profile == null)
        {
            Report("已连接屏幕（缺少 profile）");
            return;
        }

        if (profile.Encoding != FrameEncoding.Rgb565)
        {
            Report($"屏幕 PM={profile.ProductId} 使用 JPEG 编码，当前版本仅支持 RGB565（已退化为预览）");
            _device.Dispose();
            _device = null;
            _profile = null;
            return;
        }

        string summary = $"已连接屏幕 · PM={profile.ProductId} SUB={profile.SubVersion} · " +
                         $"{profile.LineWidth}×{profile.LineHeight} · {profile.ByteOrder}";
        Report(summary);
        Log?.Invoke($"屏幕连接成功：{profile}");
    }

    private void SendFrame(PetSnapshot snapshot, byte[] previewBgra)
    {
        ThermalrightType2? device = _device;
        DeviceProfile? profile = _profile;
        if (device == null || profile == null)
        {
            return;
        }

        try
        {
            byte[] bgra = profile.CanvasWidth == 320 && profile.CanvasHeight == 240
                ? previewBgra
                : _renderer.Render(snapshot, profile.CanvasWidth, profile.CanvasHeight);

            var canvas = new BgraImage(profile.CanvasWidth, profile.CanvasHeight, bgra);
            BgraImage oriented = ImageTransforms.RotateAndFlip(canvas, profile.LineRotation, profile.Flip);
            byte[] payload = Rgb565Encoder.Encode(oriented, profile.ByteOrder);
            byte[] packet = FrameAssembler.Assemble(
                payload,
                oriented.Width,
                oriented.Height,
                profile.Encoding,
                512);

            if (_pump.ShouldSend(packet, DateTimeOffset.Now))
            {
                device.SendFrame(packet, 512, settleMs: 0);
                Interlocked.Increment(ref _sendCount);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"推帧失败：{ex.Message}");
            Report($"屏幕连接中断：{ex.Message}");
            Disconnect(null);
            _nextConnectAttempt = DateTime.Now.AddSeconds(3);
        }
    }

    private void UpdateFps(DateTime now)
    {
        double seconds = (now - _fpsWindow).TotalSeconds;
        if (seconds < 1)
        {
            return;
        }

        Volatile.Write(ref _renderFps, (int)Math.Round(Interlocked.Exchange(ref _renderCount, 0) / seconds));
        Volatile.Write(ref _sendFps, (int)Math.Round(Interlocked.Exchange(ref _sendCount, 0) / seconds));
        _fpsWindow = now;
    }

    private void Disconnect(string? status)
    {
        _device?.Dispose();
        _device = null;
        _profile = null;
        _pump.Reset();
        if (status != null)
        {
            Report(status);
        }
    }

    private void Report(string message)
    {
        bool changed;
        lock (_gate)
        {
            changed = _lastStatus != message;
            _lastStatus = message;
        }

        if (!changed)
        {
            return;
        }

        StatusChanged?.Invoke(message);
        Log?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        Disconnect(null);
        lock (_gate)
        {
            _pendingDevice?.Dispose();
            _pendingDevice = null;
            _pendingProfile = null;
        }
    }
}
