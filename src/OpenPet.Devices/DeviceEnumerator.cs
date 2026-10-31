using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenPet.Devices.Interop;

namespace OpenPet.Devices;

/// <summary>A USB device interface path plus the instance id it maps to.</summary>
public sealed record LcdInterface(string Path, string InstanceId)
{
    /// <summary>Driver service bound to the device node (e.g. "WINUSB"), when readable.</summary>
    public string? DriverService { get; init; }

    public override string ToString() => Path;
}

/// <summary>Enumerates the Thermalright 0416:5302 interfaces and reports who holds them.</summary>
public static class DeviceEnumerator
{
    public const string TargetInterfaceMarker = "vid_0416&pid_5302&mi_01";
    public const string TargetHidMarker = "vid_0416&pid_5302&mi_00";

    /// <summary>GUID_DEVINTERFACE_HID.</summary>
    public static readonly Guid HidDeviceInterfaceClass = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>All present USB device interface paths (multi-sz flattened).</summary>
    public static IReadOnlyList<string> EnumerateUsbInterfacePaths()
        => EnumerateInterfacePaths(NativeMethods.UsbDeviceInterfaceClass);

    /// <summary>All present HID device interface paths (multi-sz flattened).</summary>
    public static IReadOnlyList<string> EnumerateHidInterfacePaths()
        => EnumerateInterfacePaths(HidDeviceInterfaceClass);

    /// <summary>All present interfaces of one device interface class.</summary>
    public static IReadOnlyList<string> EnumerateInterfacePaths(Guid classGuid)
    {
        int result = NativeMethods.CmGetDeviceInterfaceListSize(
            out uint length, ref classGuid, null, NativeMethods.CmGetDeviceInterfaceListPresent);
        if (result != 0)
        {
            throw new LcdDeviceException($"CM_Get_Device_Interface_List_Size failed (CONFIGRET 0x{result:X}).");
        }

        if (length == 0)
        {
            return Array.Empty<string>();
        }

        nint buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
        try
        {
            result = NativeMethods.CmGetDeviceInterfaceList(
                ref classGuid, null, buffer, length, NativeMethods.CmGetDeviceInterfaceListPresent);
            if (result != 0)
            {
                throw new LcdDeviceException($"CM_Get_Device_Interface_List failed (CONFIGRET 0x{result:X}).");
            }

            return ReadMultiSz(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Every 0416:5302 MI_01 WinUSB interface currently present (may be none).</summary>
    public static IReadOnlyList<LcdInterface> FindTargets()
    {
        var targets = new List<LcdInterface>();
        foreach (string path in EnumerateUsbInterfacePaths())
        {
            if (!path.Contains(TargetInterfaceMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string instanceId = InstanceIdFromInterfacePath(path);
            targets.Add(new LcdInterface(path, instanceId)
            {
                DriverService = TryReadDriverService(instanceId),
            });
        }

        return targets;
    }

    /// <summary>Every 0416:5302 MI_00 vendor HID interface currently present.</summary>
    public static IReadOnlyList<LcdInterface> FindHidTargets()
    {
        var targets = new List<LcdInterface>();
        foreach (string path in EnumerateHidInterfacePaths())
        {
            if (!path.Contains(TargetHidMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string instanceId = InstanceIdFromInterfacePath(path);
            targets.Add(new LcdInterface(path, instanceId)
            {
                DriverService = TryReadDriverService(instanceId),
            });
        }

        return targets;
    }

    /// <summary>TRCC (or a lookalike) processes currently running; the usual reason the device is busy.</summary>
    public static IReadOnlyList<int> FindTrccProcessIds()
    {
        var ids = new List<int>();
        foreach (string name in new[] { "TRCC", "OpenTRCC" })
        {
            try
            {
                ids.AddRange(Process.GetProcessesByName(name).Select(p => p.Id));
            }
            catch (InvalidOperationException)
            {
                // Process list can race with exit; ignore and keep the other name.
            }
        }

        return ids;
    }

    /// <summary>Extracts "HID\VID_0416&amp;PID_5302&amp;MI_00\&lt;instance&gt;" from a device interface path.</summary>
    public static string InstanceIdFromInterfacePath(string interfacePath)
    {
        string[] parts = interfacePath.Split('#');
        if (parts.Length < 3)
        {
            return interfacePath;
        }

        string enumerator = parts[0].Replace(@"\\?\", string.Empty, StringComparison.Ordinal);
        return $"{enumerator}\\{parts[1]}\\{parts[2]}";
    }

    private static string? TryReadDriverService(string instanceId)
    {
        try
        {
            object? value = Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\{instanceId}", "Service", null);
            return value as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> ReadMultiSz(nint buffer)
    {
        var result = new List<string>();
        int offset = 0;

        while (true)
        {
            string? value = Marshal.PtrToStringUni(buffer + offset);
            if (string.IsNullOrEmpty(value))
            {
                break;
            }

            result.Add(value);
            offset += (value.Length + 1) * sizeof(char);
        }

        return result;
    }
}
