using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenPet.Devices.Interop;

namespace OpenPet.Devices;

/// <summary>
/// Vendor HID transport on MI_00 (usage page 0xFF06). The panel has no WinUSB
/// device interface at all; every 512-byte protocol block travels as one HID
/// output report (report id byte + 512 payload bytes).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PipelineDepth"/> controls how many overlapped output reports may be
/// in flight at once. Depth 1 serialises (submit → wait → submit); depths above
/// 1 keep that many reports queued so the fixed per-report latency overlaps with
/// the next submission. Report order is preserved because HIDClass serialises
/// writes to one collection, which the frame format depends on.
/// </para>
/// </remarks>
public sealed unsafe class HidTransport : ILcdTransport
{
    private const int MaxPipelineDepth = 64;
    private const uint WriteTimeoutMs = 2000;

    private readonly nint _readEvent;
    private readonly byte[] _inputBuffer;

    private readonly byte[][] _buffers;
    private readonly GCHandle[] _bufferHandles;
    private readonly nint[] _bufferPointers;
    private readonly nint[] _writeEvents;
    private readonly bool[] _pending;
    private readonly HidNative.OverlappedData[] _overlaps;
    private readonly GCHandle _overlapsHandle;
    private readonly nint _overlapsPointer;
    private int _nextSlot;

    private nint _handle;
    private bool _disposed;

    private HidTransport(
        string interfacePath, nint handle, nint readEvent,
        int outputReportLength, int inputReportLength, int featureReportLength,
        ushort usagePage, ushort usage, byte reportId, int pipelineDepth)
    {
        Description = interfacePath;
        _handle = handle;
        _readEvent = readEvent;
        OutputReportLength = outputReportLength;
        InputReportLength = inputReportLength;
        FeatureReportLength = featureReportLength;
        UsagePage = usagePage;
        Usage = usage;
        ReportId = reportId;
        PipelineDepth = Math.Clamp(pipelineDepth, 1, MaxPipelineDepth);
        PayloadOffset = outputReportLength >= 513 ? 1 : 0;
        PayloadSize = outputReportLength - PayloadOffset;

        _inputBuffer = new byte[inputReportLength];

        _buffers = new byte[PipelineDepth][];
        _bufferHandles = new GCHandle[PipelineDepth];
        _bufferPointers = new nint[PipelineDepth];
        _writeEvents = new nint[PipelineDepth];
        _pending = new bool[PipelineDepth];

        for (int i = 0; i < PipelineDepth; i++)
        {
            _buffers[i] = new byte[outputReportLength];
            _bufferHandles[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);
            _bufferPointers[i] = _bufferHandles[i].AddrOfPinnedObject();
            _writeEvents[i] = HidNative.CreateEvent(nint.Zero, manualReset: 1, initialState: 0, name: null);
            if (_writeEvents[i] == nint.Zero)
            {
                throw new LcdDeviceException(
                    $"CreateEvent failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }
        }

        _overlaps = new HidNative.OverlappedData[PipelineDepth];
        _overlapsHandle = GCHandle.Alloc(_overlaps, GCHandleType.Pinned);
        _overlapsPointer = _overlapsHandle.AddrOfPinnedObject();
        for (int i = 0; i < PipelineDepth; i++)
        {
            _overlaps[i].Event = _writeEvents[i];
        }
    }

    public string Description { get; }

    /// <summary>Bytes per HID output report as declared by the descriptor (513 on this panel).</summary>
    public int OutputReportLength { get; }

    public int InputReportLength { get; }

    public int FeatureReportLength { get; }

    /// <summary>Payload bytes per report: report length minus the leading report id byte.</summary>
    public int PayloadSize { get; }

    /// <summary>1 when the descriptor uses report ids (this panel does), otherwise 0.</summary>
    public int PayloadOffset { get; }

    public ushort UsagePage { get; }

    public ushort Usage { get; }

    /// <summary>Report id byte written in front of every payload (0 for this panel).</summary>
    public byte ReportId { get; }

    /// <summary>How many output reports may be in flight at once.</summary>
    public int PipelineDepth { get; }

    public static HidTransport Open(string interfacePath, byte reportId = 0, int pipelineDepth = 1)
    {
        nint handle = HidNative.CreateFile(
            interfacePath,
            HidNative.GenericRead | HidNative.GenericWrite,
            shareMode: 0,
            nint.Zero,
            HidNative.OpenExisting,
            HidNative.FileFlagOverlapped,
            nint.Zero);

        if (handle == new nint(-1))
        {
            int error = Marshal.GetLastWin32Error();
            switch (error)
            {
                case HidNative.ErrorSharingViolation:
                    IReadOnlyList<int> pids = DeviceEnumerator.FindTrccProcessIds();
                    string who = pids.Count > 0 ? $"TRCC/OpenTRCC (PID {string.Join(", ", pids)})" : "another process";
                    throw new DeviceBusyException(
                        $"{who} is holding the HID display interface exclusively. Close it and retry.", pids);

                case HidNative.ErrorAccessDenied:
                    throw new DeviceAccessDeniedException(
                        "Windows denied access to the HID display interface (ERROR_ACCESS_DENIED).");

                default:
                    throw new LcdDeviceException(
                        $"CreateFile failed for '{interfacePath}': {new Win32Exception(error).Message}");
            }
        }

        nint readEvent = nint.Zero;

        try
        {
            if (HidNative.HidD_GetPreparsedData(handle, out nint preparsed) == 0)
            {
                throw new LcdDeviceException(
                    $"HidD_GetPreparsedData failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            HidNative.HidpCaps caps;
            try
            {
                int status = HidNative.HidP_GetCaps(preparsed, out caps);
                if (status != HidNative.HidpStatusSuccess)
                {
                    throw new LcdDeviceException($"HidP_GetCaps returned 0x{status:X}.");
                }
            }
            finally
            {
                HidNative.HidD_FreePreparsedData(preparsed);
            }

            readEvent = HidNative.CreateEvent(nint.Zero, manualReset: 1, initialState: 0, name: null);
            if (readEvent == nint.Zero)
            {
                throw new LcdDeviceException(
                    $"CreateEvent failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            return new HidTransport(
                interfacePath, handle, readEvent,
                caps.OutputReportByteLength, caps.InputReportByteLength, caps.FeatureReportByteLength,
                caps.UsagePage, caps.Usage, reportId, pipelineDepth);
        }
        catch
        {
            if (readEvent != nint.Zero)
            {
                HidNative.CloseHandle(readEvent);
            }

            HidNative.CloseHandle(handle);
            throw;
        }
    }

    /// <summary>Queues one 512-byte protocol block per report; larger spans are split into consecutive reports.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty)
        {
            return;
        }

        for (int offset = 0; offset < data.Length; offset += PayloadSize)
        {
            int count = Math.Min(PayloadSize, data.Length - offset);
            int slot = _nextSlot;
            WaitSlot(slot);

            byte[] buffer = _buffers[slot];
            Array.Clear(buffer);
            buffer[0] = ReportId;
            data.Slice(offset, count).CopyTo(buffer.AsSpan(PayloadOffset));
            Submit(slot);

            _nextSlot = (_nextSlot + 1) % PipelineDepth;
        }
    }

    /// <summary>Waits until every queued report has been accepted by the device.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int slot = 0; slot < PipelineDepth; slot++)
        {
            WaitSlot(slot);
        }
    }

    /// <summary>Reads one input report and returns its payload bytes (report id stripped).</summary>
    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The device answers only after it has consumed the request.
        Flush();

        Array.Clear(_inputBuffer);
        ReadReport(_inputBuffer, timeout);

        int payloadLength = Math.Max(0, _inputBuffer.Length - PayloadOffset);
        int count = Math.Min(buffer.Length, payloadLength);
        _inputBuffer.AsSpan(PayloadOffset, count).CopyTo(buffer);
        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != nint.Zero)
        {
            HidNative.CancelIoEx(_handle, null);
            HidNative.CloseHandle(_handle);
            _handle = nint.Zero;
        }

        foreach (GCHandle handle in _bufferHandles)
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }

        if (_overlapsHandle.IsAllocated)
        {
            _overlapsHandle.Free();
        }

        foreach (nint writeEvent in _writeEvents)
        {
            if (writeEvent != nint.Zero)
            {
                HidNative.CloseHandle(writeEvent);
            }
        }

        if (_readEvent != nint.Zero)
        {
            HidNative.CloseHandle(_readEvent);
        }
    }

    private void Submit(int slot)
    {
        HidNative.ResetEvent(_writeEvents[slot]);

        var overlapped = (HidNative.OverlappedData*)_overlapsPointer + slot;
        overlapped->Event = _writeEvents[slot];

        int ok = HidNative.WriteFile(
            _handle, (byte*)_bufferPointers[slot], (uint)_buffers[slot].Length, out uint written, overlapped);

        if (ok != 0)
        {
            _pending[slot] = false;
            if (written != (uint)_buffers[slot].Length)
            {
                throw new ShortWriteException(_buffers[slot].Length, (int)written);
            }

            return;
        }

        int error = Marshal.GetLastWin32Error();
        if (error != HidNative.ErrorIoPending)
        {
            _pending[slot] = false;
            throw error == HidNative.ErrorOperationAborted
                ? new LcdDeviceException("HID write aborted; device disappeared.")
                : new LcdDeviceException($"WriteFile failed: {new Win32Exception(error).Message}");
        }

        _pending[slot] = true;
    }

    private void WaitSlot(int slot)
    {
        if (!_pending[slot])
        {
            return;
        }

        var overlapped = (HidNative.OverlappedData*)_overlapsPointer + slot;
        uint wait = HidNative.WaitForSingleObject(_writeEvents[slot], WriteTimeoutMs);
        if (wait != HidNative.WaitObject0)
        {
            HidNative.CancelIoEx(_handle, overlapped);
            _pending[slot] = false;
            throw new LcdTimeoutException($"HID output report timed out after {WriteTimeoutMs} ms.");
        }

        if (HidNative.GetOverlappedResult(_handle, overlapped, out uint transferred, wait: 0) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            _pending[slot] = false;
            throw error == HidNative.ErrorOperationAborted
                ? new LcdDeviceException("HID write aborted; device disappeared.")
                : new LcdDeviceException($"WriteFile (overlapped) failed: {new Win32Exception(error).Message}");
        }

        _pending[slot] = false;
        if (transferred != (uint)_buffers[slot].Length)
        {
            throw new ShortWriteException(_buffers[slot].Length, (int)transferred);
        }
    }

    private void ReadReport(byte[] report, TimeSpan timeout)
    {
        HidNative.ResetEvent(_readEvent);
        uint timeoutMs = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, 60_000);

        fixed (byte* pointer = report)
        {
            var overlapped = new HidNative.OverlappedData { Event = _readEvent };
            int ok = HidNative.ReadFile(_handle, pointer, (uint)report.Length, out _, &overlapped);

            if (ok == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != HidNative.ErrorIoPending)
                {
                    throw error == HidNative.ErrorOperationAborted
                        ? new LcdDeviceException("HID read aborted; device disappeared.")
                        : new LcdDeviceException($"ReadFile failed: {new Win32Exception(error).Message}");
                }

                uint wait = HidNative.WaitForSingleObject(_readEvent, timeoutMs);
                if (wait != HidNative.WaitObject0)
                {
                    HidNative.CancelIoEx(_handle, &overlapped);
                    throw new LcdTimeoutException($"HID input report timed out after {timeoutMs} ms.");
                }

                if (HidNative.GetOverlappedResult(_handle, &overlapped, out _, wait: 0) == 0)
                {
                    int resultError = Marshal.GetLastWin32Error();
                    if (resultError == HidNative.ErrorOperationAborted)
                    {
                        throw new LcdTimeoutException("HID read cancelled.");
                    }

                    throw new LcdDeviceException($"ReadFile (overlapped) failed: {new Win32Exception(resultError).Message}");
                }
            }
        }
    }
}
