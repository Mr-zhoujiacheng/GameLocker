using System.Management;
using System.Runtime.Intrinsics.X86;
using System.Text;
using static GameLocker.Services.CpuPowerService;

namespace GameLocker.Services;

internal sealed class CpuPowerBackend : IDisposable
{
    private readonly AmdCpuPower? _amd;
    private readonly string? _error;
    private readonly ulong _original;
    private readonly double _unit;
    private ulong _changedMask;
    // 固件真实上限与“恢复默认时应写回的值”：重启后登录任务可能已把限制写进寄存器，
    // 若只读当前寄存器，会把已生效的限制误当成固件上限，导致再也无法调高。
    private double _maxPl1;
    private double _maxPl2;
    private ulong _restoreRaw;
    private const ulong LockBit = 1UL << 63;
    private static int Shift(int index) => index * 32;
    private static ulong Field(int index) => 0x7FFFUL << Shift(index);
    private double Watts(ulong raw, int index) => ((raw >> Shift(index)) & 0x7FFF) * _unit;

    public CpuPowerBackend()
    {
        try
        {
            if (!X86Base.IsSupported) { _error = "仅支持 x64 Intel / AMD CPU"; return; }
            var id = X86Base.CpuId(0, 0);
            string vendor = Encoding.ASCII.GetString(BitConverter.GetBytes(id.Ebx)
                .Concat(BitConverter.GetBytes(id.Edx)).Concat(BitConverter.GetBytes(id.Ecx)).ToArray());
            if (vendor == "AuthenticAMD") { _amd = new AmdCpuPower(); return; }
            if (vendor != "GenuineIntel") { _error = "当前处理器厂商不支持 CPU 原生功耗控制"; return; }
            using var search = new ManagementObjectSearcher("SELECT NumberOfProcessors FROM Win32_ComputerSystem");
            foreach (ManagementObject system in search.Get())
            {
                using (system)
                    if (Convert.ToInt32(system["NumberOfProcessors"]) != 1)
                    { _error = "暂不支持多插槽处理器功耗设置"; return; }
            }
            if (!PawnIoMsr.EnsureReady()) { _error = PawnIoMsr.Status + "；请先安装随包提供的 PawnIO 驱动"; return; }
            if (!PawnIoMsr.ReadMsr(0x606, out var units) || !PawnIoMsr.ReadMsr(0x610, out _original))
            { _error = "无法读取 Intel RAPL 单位 / 封装功耗限制"; return; }
            _unit = Math.Pow(0.5, (int)(units & 0xF));
            _restoreRaw = _original;
            var savedPl1 = SettingsService.Current.CpuPowerOriginalPl1;
            var savedPl2 = SettingsService.Current.CpuPowerOriginalPl2;
            _maxPl1 = Math.Max(Watts(_original, 0), savedPl1);
            _maxPl2 = Math.Max(Watts(_original, 1), savedPl2);
            if (_unit > 0)
            {
                if (savedPl1 > Watts(_original, 0))
                    _restoreRaw = (_restoreRaw & ~Field(0)) | ((ulong)Math.Floor(savedPl1 / _unit) << Shift(0));
                if (savedPl2 > Watts(_original, 1))
                    _restoreRaw = (_restoreRaw & ~Field(1)) | ((ulong)Math.Floor(savedPl2 / _unit) << Shift(1));
            }
            if (_maxPl1 < 1 || _maxPl2 < 1)
                _error = "Intel 固件未提供有效 PL1 / PL2，禁止写入";
        }
        catch (Exception ex) { _error = "CPU 功耗初始化失败：" + ex.Message; }
    }

    public State Read()
    {
        if (_amd != null) return _amd.Read();
        if (_error != null) return new(false, true, _error, []);
        if (!PawnIoMsr.ReadMsr(0x610, out var raw)) return new(false, true, "读取 Intel RAPL 失败", []);
        var limits = Enumerable.Range(0, 2).Select(i => new Limit(i == 0 ? "Intel PL1" : "Intel PL2",
            Watts(raw, i), 1, Math.Max(1, Math.Floor(i == 0 ? _maxPl1 : _maxPl2)),
            (raw & LockBit) == 0 && (raw & (1UL << (Shift(i) + 15))) != 0 && (i == 0 ? _maxPl1 : _maxPl2) >= 1)).ToArray();
        return new(limits.Any(l => l.Writable), true,
            (raw & LockBit) != 0 ? "Intel RAPL 已被 BIOS 锁定，无法修改" :
            "Intel RAPL · 仅降低 / 恢复初始上限；保留时间窗与其他位；不保证覆盖 OEM / MMIO 的额外限制", limits);
    }

    public Result Apply(int index, double watts)
    {
        if (_amd != null) return _amd.Apply(index, watts);
        var state = Read();
        if (index < 0 || index >= state.Limits.Length || !state.Limits[index].Writable)
            return new(false, state.Message);
        var limit = state.Limits[index];
        if (!double.IsFinite(watts) || watts < limit.Minimum || watts > limit.Maximum)
            return new(false, "功耗超出接管前限制，已拒绝写入");
        if (!PawnIoMsr.ReadMsr(0x610, out var before) || (before & LockBit) != 0)
            return new(false, "Intel RAPL 读取失败或已锁定");
        // 持续功耗发生变化时短时功耗可灵活跟随：抬高 PL1 时在同一寄存器里一起把 PL2 抬到不低于 PL1，
        // 只有 PL1 超过 PL2 自身上限才拒绝；反向下调 PL2 低于 PL1 才报错
        bool raiseBurst = false;
        if (index == 0 && watts > Watts(before, 1))
        {
            var burstMax = Math.Max(1, Math.Floor(_maxPl2));
            if (watts > burstMax)
                return new(false, $"持续功耗不能超过短时功耗上限（{burstMax:0} W）");
            raiseBurst = true;
        }
        if (index == 1 && watts < Watts(before, 0))
            return new(false, "短时功耗不能低于当前持续功耗，请先降低持续功耗");
        ulong encoded = (ulong)Math.Floor(watts / _unit);
        if (encoded == 0 || encoded > 0x7FFF) return new(false, "RAPL 编码超出范围");
        ulong mask = Field(index);
        ulong target = (before & ~mask) | (encoded << Shift(index));
        string followNote = "";
        if (raiseBurst)
        {
            // 同一次写入把短时功耗一起抬到持续功耗的水平（保持 PL1 ≤ PL2）
            mask |= Field(1);
            target = (target & ~Field(1)) | (encoded << Shift(1));
            followNote = $"；短时功耗同步为 {encoded * _unit:0.##} W";
        }
        _changedMask |= mask;
        bool wrote = PawnIoMsr.WritePackagePowerLimit(target);
        Thread.Sleep(100);
        if (wrote && PawnIoMsr.ReadMsr(0x610, out var actual) && (actual & mask) == (target & mask)
            && (actual & (1UL << (Shift(index) + 15))) != 0)
            return new(true, $"Intel {(index == 0 ? "PL1" : "PL2")} 已写入并读回：{encoded * _unit:0.##} W（不代表实际耗电）{followNote}");
        bool rollback = RestoreFields(mask, before);
        return new(false, "Intel 功耗写入或读回验证失败；" + (rollback ? "已恢复此前值" : "恢复未获确认，请检查固件 / 重启"));
    }

    private static bool RestoreFields(ulong mask, ulong original)
    {
        if (!PawnIoMsr.ReadMsr(0x610, out var now)) return false;
        if ((now & mask) == (original & mask)) return true;
        if ((now & LockBit) != 0) return false;
        if (!PawnIoMsr.WritePackagePowerLimit((now & ~mask) | (original & mask))) return false;
        Thread.Sleep(100);
        return PawnIoMsr.ReadMsr(0x610, out var read) && (read & mask) == (original & mask);
    }

    public Result Restore()
    {
        if (_amd != null) return _amd.Restore();
        // 登录任务可能已经把限制写进寄存器（本次会话没有修改记录）：关闭接管时同样要恢复到固件原值
        var mask = _changedMask;
        if (_maxPl1 > Watts(_original, 0)) mask |= Field(0);
        if (_maxPl2 > Watts(_original, 1)) mask |= Field(1);
        if (mask == 0) return new(true, "CPU 功耗未修改，已关闭接管");
        if (!RestoreFields(mask, _restoreRaw)) return new(false, "Intel 初始功耗恢复未获确认，请检查固件 / 重启");
        _changedMask = 0;
        return new(true, "Intel 初始功耗限制已恢复并验证");
    }

    public void Dispose()
    {
        _amd?.Dispose();
        PawnIoMsr.Close();
    }
}
