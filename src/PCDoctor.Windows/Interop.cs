using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace PCDoctor.Windows;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    /// <summary>1 = BIOS (Legacy), 2 = UEFI.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFirmwareType(out int firmwareType);

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    internal static extern bool DnsFlushResolverCache();

    [DllImport("wininet.dll", SetLastError = true)]
    internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    internal const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    internal const int INTERNET_OPTION_REFRESH = 37;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    internal const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    internal const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
}

internal sealed class AccessDeniedException(string what) : Exception($"{what}: requiere permiso de administrador");

internal static class Wmi
{
    public static List<ManagementBaseObject> Query(string scope, string wql, int timeoutSeconds = 15)
    {
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(timeoutSeconds), ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            // No usar Cast().ToList(): en .NET 9+ LINQ consulta antes ICollection.Count, y en una colección WMI
            // de solo avance eso la consume entera y la enumeración real queda vacía.
            var list = new List<ManagementBaseObject>();
            foreach (ManagementBaseObject o in searcher.Get()) list.Add(o);
            return list;
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw new AccessDeniedException(wql);
        }
        catch (UnauthorizedAccessException)
        {
            throw new AccessDeniedException(wql);
        }
    }

    public static List<ManagementBaseObject> Cimv2(string wql) => Query(@"root\cimv2", wql);

    public static T? Get<T>(this ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            if (v is null) return default;
            if (v is T t) return t;
            return (T)Convert.ChangeType(v, typeof(T));
        }
        catch (ManagementException)
        {
            return default;
        }
    }

    public static string Str(this ManagementBaseObject o, string prop) => o.Get<object>(prop)?.ToString()?.Trim() ?? "";
}

internal static class Reg
{
    public static object? Value(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static string? Str(RegistryHive hive, string path, string name) => Value(hive, path, name)?.ToString();

    public static int? Int(RegistryHive hive, string path, string name) => Value(hive, path, name) switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var r) => r,
        _ => null,
    };
}

public static class WindowsEnvironment
{
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Identificador estable y anónimo de la PC (hash del MachineGuid), para separar el historial por equipo.</summary>
    public static string MachineId()
    {
        var guid = Reg.Str(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Cryptography", "MachineGuid") ?? Environment.MachineName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pcdoctor:" + guid)))[..16].ToLowerInvariant();
    }
}
