using System.Runtime.InteropServices;

namespace OpenPet.Devices.Interop;

/// <summary>Win32 HID surface: device file, overlapped read/write and report caps.</summary>
internal static unsafe partial class HidNative
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 258;

    public const int ErrorIoPending = 997;
    public const int ErrorOperationAborted = 995;
    public const int ErrorSharingViolation = 32;
    public const int ErrorAccessDenied = 5;

    public const int HidpStatusSuccess = 0x00110000;

    /// <summary>Usage page Thermalright uses for the vendor HID collection.</summary>
    public const ushort VendorUsagePage = 0xFF06;

    [StructLayout(LayoutKind.Sequential)]
    public struct OverlappedData
    {
        public nint Internal;
        public nint InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public nint Event;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int ReadFile(
        nint handle, byte* buffer, uint numberOfBytesToRead, out uint bytesRead, OverlappedData* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int WriteFile(
        nint handle, byte* buffer, uint numberOfBytesToWrite, out uint bytesWritten, OverlappedData* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetOverlappedResult(
        nint handle, OverlappedData* overlapped, out uint bytesTransferred, int wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CancelIoEx(nint handle, OverlappedData* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateEvent(nint attributes, int manualReset, int initialState, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int ResetEvent(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("hid.dll", SetLastError = true)]
    internal static partial int HidD_GetPreparsedData(nint device, out nint preparsedData);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetCaps(nint preparsedData, out HidpCaps caps);

    [LibraryImport("hid.dll", SetLastError = true)]
    internal static partial int HidD_FreePreparsedData(nint preparsedData);
}
