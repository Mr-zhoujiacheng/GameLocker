using System.Runtime.InteropServices;

namespace GameLocker.Services;

internal sealed record GpuAdapter(int Index, uint VendorId, uint DeviceId, string Name);

internal static class GpuAdapterService
{
    public static List<GpuAdapter> Enumerate()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        try
        {
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdaptersDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            var result = new List<GpuAdapter>();
            for (uint i = 0; ; i++)
            {
                var hr = enumerate(factory, i, out var adapter);
                if (hr == unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var getDesc = Marshal.GetDelegateForFunctionPointer<GetDescDelegate>(
                        Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    Marshal.ThrowExceptionForHR(getDesc(adapter, out var desc));
                    if ((desc.Flags & 2) == 0)
                        result.Add(new GpuAdapter((int)i, desc.VendorId, desc.DeviceId, desc.Description));
                }
                finally { Marshal.Release(adapter); }
            }
            return result;
        }
        finally { Marshal.Release(factory); }
    }

    public static GpuAdapter Resolve(IReadOnlyList<GpuAdapter> adapters, int index, uint vendor, uint device)
    {
        if (index == -1 && vendor == 0 && device == 0)
            return adapters.FirstOrDefault(a => a.VendorId == 0x10de) ?? adapters.FirstOrDefault()
                ?? throw new InvalidOperationException("未找到可用的硬件 GPU");
        var exact = adapters.FirstOrDefault(a => a.Index == index && a.VendorId == vendor && a.DeviceId == device);
        if (exact is not null) return exact;
        var matches = adapters.Where(a => a.VendorId == vendor && a.DeviceId == device).ToList();
        if (matches.Count == 1) return matches[0];
        throw new InvalidOperationException("所选 GPU 已不存在或身份不唯一，请在编辑页重新选择 GPU");
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdaptersDelegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescDelegate(IntPtr adapter, out AdapterDesc desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow, LuidHigh, Flags;
    }
}
