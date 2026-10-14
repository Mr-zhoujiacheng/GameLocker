using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>无线网络 / 蓝牙连接状态检测（无额外依赖）</summary>
public static class NetworkService
{
    /// <summary>是否已连接 Wi-Fi（存在处于 Up 状态的无线网卡，即已关联热点）</summary>
    public static bool IsWifiConnected()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                    && ni.OperationalStatus == OperationalStatus.Up)
                    return true;
        }
        catch { /* 读取失败按未连接处理 */ }
        return false;
    }

    // ---- 蓝牙：枚举已连接设备（bthprops.cpl 经典蓝牙 API）----

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;          // BLUETOOTH_ADDRESS（union，8 字节对齐）
        public uint ClassOfDevice;
        public int Connected;          // BOOL：是否已连接
        public int Remembered;
        public int Authenticated;
        public SystemTime LastSeen;
        public SystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
    {
        public uint Size;
        public int ReturnAuthenticated;
        public int ReturnRemembered;
        public int ReturnUnknown;
        public int ReturnConnected;
        public int IssueInquiry;       // 不发起扫描，只查已知设备，速度快
        public byte TimeoutSeconds;
        public IntPtr Radio;           // 所有蓝牙适配器
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BluetoothDeviceSearchParams pSearchParams, ref BluetoothDeviceInfo pDeviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(
        IntPtr hFind, ref BluetoothDeviceInfo pDeviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    /// <summary>是否有已连接的蓝牙设备；无适配器/枚举失败按未连接处理</summary>
    public static bool IsBluetoothConnected()
    {
        var sp = new BluetoothDeviceSearchParams
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceSearchParams>(),
            ReturnConnected = 1
        };
        var info = new BluetoothDeviceInfo
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(),
            Name = string.Empty
        };

        IntPtr h = BluetoothFindFirstDevice(ref sp, ref info);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return false; // INVALID_HANDLE_VALUE
        try
        {
            if (info.Connected != 0) return true;
            while (BluetoothFindNextDevice(h, ref info))
                if (info.Connected != 0) return true;
            return false;
        }
        finally { BluetoothFindDeviceClose(h); }
    }
}
