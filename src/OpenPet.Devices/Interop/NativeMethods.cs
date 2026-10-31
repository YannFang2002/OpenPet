using System.Runtime.InteropServices;

namespace OpenPet.Devices.Interop;

/// <summary>
/// Thin P/Invoke surface over cfgmgr32 (device interface enumeration), kernel32
/// (device file handle) and winusb (bulk pipe I/O).
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorSemTimeout = 121;
    public const int ErrorOperationAborted = 995;

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint OpenExisting = 3;
    public const uint FileShareNone = 0;

    public const uint CmGetDeviceInterfaceListPresent = 0;

    public const uint PipeTransferTimeout = 0x03;
    public const uint MaxTransferSizePolicy = 0x08;

    /// <summary>Default read timeout for the handshake reply (ms).</summary>
    public const uint ReadTimeoutMs = 5000;

    /// <summary>Default write timeout for one bulk transfer (ms).</summary>
    public const uint WriteTimeoutMs = 1000;

    public const byte UsbdPipeTypeBulk = 2;

    /// <summary>GUID_DEVINTERFACE_USB_DEVICE.</summary>
    public static readonly Guid UsbDeviceInterfaceClass = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbInterfaceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public byte InterfaceNumber;
        public byte AlternateSetting;
        public byte NumEndpoints;
        public byte InterfaceClass;
        public byte InterfaceSubClass;
        public byte InterfaceProtocol;
        public byte Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinUsbPipeInformation
    {
        public uint PipeType;
        public byte EndpointId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CmGetDeviceInterfaceListSize(
        out uint length,
        ref Guid interfaceClassGuid,
        string? deviceInstanceId,
        uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CmGetDeviceInterfaceList(
        ref Guid interfaceClassGuid,
        string? deviceInstanceId,
        nint buffer,
        uint bufferLength,
        uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CloseHandle(IntPtr handle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_Initialize(IntPtr deviceHandle, out IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_Free(IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_QueryInterfaceSettings(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        out UsbInterfaceDescriptor descriptor);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_QueryPipe(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        byte pipeIndex,
        out WinUsbPipeInformation pipeInformation);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_SetPipePolicy(
        IntPtr interfaceHandle,
        byte pipeId,
        uint policyType,
        uint valueLength,
        void* value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_WritePipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte* buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_ReadPipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte* buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_FlushPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", SetLastError = true)]
    internal static partial int WinUsb_AbortPipe(IntPtr interfaceHandle, byte pipeId);

    internal static void SetTimeout(IntPtr interfaceHandle, byte pipeId, uint milliseconds)
    {
        uint value = milliseconds;
        if (WinUsb_SetPipePolicy(interfaceHandle, pipeId, PipeTransferTimeout, sizeof(uint), &value) == 0)
        {
            throw new LcdDeviceException(
                $"WinUsb_SetPipePolicy(timeout) failed on pipe 0x{pipeId:X2}: {Marshal.GetLastWin32Error()}");
        }
    }
}
