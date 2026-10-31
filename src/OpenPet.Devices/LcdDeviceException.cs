namespace OpenPet.Devices;

/// <summary>Base exception for device-layer failures that carry a user-actionable meaning.</summary>
public class LcdDeviceException : Exception
{
    public LcdDeviceException(string message) : base(message) { }

    public LcdDeviceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>No matching 0416:5302 interface could be enumerated.</summary>
public sealed class DeviceNotFoundException : LcdDeviceException
{
    public DeviceNotFoundException(string message) : base(message) { }
}

/// <summary>The device exists but another process (typically TRCC) holds an exclusive handle.</summary>
public sealed class DeviceBusyException : LcdDeviceException
{
    public DeviceBusyException(string message, IReadOnlyList<int> occupyingProcessIds) : base(message)
    {
        OccupyingProcessIds = occupyingProcessIds;
    }

    public IReadOnlyList<int> OccupyingProcessIds { get; }
}

/// <summary>WinUSB refused the handle; usually needs elevation.</summary>
public sealed class DeviceAccessDeniedException : LcdDeviceException
{
    public DeviceAccessDeniedException(string message) : base(message) { }
}

/// <summary>A pipe transfer exceeded PIPE_TRANSFER_TIMEOUT.</summary>
public sealed class LcdTimeoutException : LcdDeviceException
{
    public LcdTimeoutException(string message) : base(message) { }
}

/// <summary>A bulk write transferred fewer bytes than requested; the frame is corrupt.</summary>
public sealed class ShortWriteException : LcdDeviceException
{
    public ShortWriteException(int requested, int written)
        : base($"Short write: requested {requested} bytes, device accepted {written} bytes.")
    {
        Requested = requested;
        Written = written;
    }

    public int Requested { get; }

    public int Written { get; }
}
