using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace GameLocker.Services;

public enum GameCoreMode { Auto, PerformanceFirst, EfficiencyFirst }

public sealed record GameCpuTopology(bool Supported, bool Hybrid, int CoreCount, string Message);

/// <summary>Only Register authorizes roots; callers must register only a successful Process.Start result.</summary>
public static class GameCpuService
{
    private const uint Access = 0x1000 | 0x2000;
    private static readonly object Gate = new();
    private static readonly Dictionary<int, Target> Targets = new();
    private static readonly HashSet<int> DiscoveryFailures = new();
    private static readonly List<Core> Cores = new();
    private static readonly GameCpuTopology Hardware = ReadTopology();
    private static Timer? _timer;
    private static uint[] _selection = Array.Empty<uint>();
    private static int _generation;
    private static bool _stopped;
    private static string _message = Hardware.Message;

    public static GameCpuTopology Topology => Hardware;
    public static string Message { get { lock (Gate) return _message; } }

    private sealed record Core(ushort Group, byte Index, byte Efficiency, uint[] Sets);
    private sealed class Target(int pid, ulong created)
    {
        public int Pid { get; } = pid;
        public ulong Created { get; } = created;
        public uint[]? Original;
        public uint[]? Written;
        public bool ExternalChange;
        public int Attempt = -1;
    }

    public static (bool Ok, string Message) Configure(int count, GameCoreMode mode)
    {
        lock (Gate)
        {
            if (_stopped) return Result(false, "CPU Sets 服务已停止。");
            if (!Hardware.Supported) return Result(false, Hardware.Message);
            if (!Enum.IsDefined(mode) || count < 0 || count > Cores.Count)
                return Result(false, $"物理核心数必须为 0 至 {Cores.Count}，且模式有效。");
            if (mode != GameCoreMode.Auto && !Hardware.Hybrid)
                return Result(false, "未检测到可用的异构核心，拒绝性能/能效优先模式。");

            IEnumerable<Core> ordered = mode switch
            {
                GameCoreMode.PerformanceFirst => Cores.OrderByDescending(c => c.Efficiency).ThenBy(c => c.Group).ThenBy(c => c.Index),
                GameCoreMode.EfficiencyFirst => Cores.OrderBy(c => c.Efficiency).ThenBy(c => c.Group).ThenBy(c => c.Index),
                _ => Cores.OrderBy(c => c.Group).ThenBy(c => c.Index)
            };
            // count=0 in priority modes is a hard class filter, not a scheduling hint.
            _selection = count == 0 && mode == GameCoreMode.Auto ? Array.Empty<uint>()
                : (count > 0 ? ordered.Take(count) : ordered.Where(c => c.Efficiency == ordered.First().Efficiency))
                    .SelectMany(c => c.Sets).OrderBy(id => id).ToArray();
            _generation++;
            DiscoveryFailures.Clear();
            Discover();
            int success = 0, failed = 0;
            foreach (var target in Targets.Values.ToArray())
            {
                using var handle = OpenProcess(Access, false, target.Pid);
                if (handle.IsInvalid)
                {
                    Fail(target, "打开进程失败：" + Error());
                    failed++;
                    continue;
                }
                if (!Matches(handle, target)) { Targets.Remove(target.Pid); continue; }
                if (Apply(handle, target)) success++; else failed++;
            }
            EnsureTimer();
            if (success + failed == 0)
                return Result(true, "配置已保存在内存，当前无存活授权目标，将用于后续启动的游戏。" + SelectionText());
            return Result(failed == 0, $"当前存活授权进程：成功 {success}，失败 {failed}。" + SelectionText());
        }
    }

    public static void Register(int pid)
    {
        lock (Gate)
        {
            if (_stopped || !Hardware.Supported || pid <= 0 || pid == Environment.ProcessId) return;
            using var handle = OpenProcess(Access, false, pid);
            if (handle.IsInvalid || !Identity(handle, pid, out var created))
            {
                Report($"注册 PID {pid} 失败：无法验证存活身份（{Error()}）。");
                return;
            }
            if (!Targets.TryGetValue(pid, out var target) || target.Created != created)
                Targets[pid] = target = new Target(pid, created);
            if (target.Attempt != _generation) Apply(handle, target);
            EnsureTimer();
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_stopped) return;
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
            int restored = 0, failed = 0;
            foreach (var target in Targets.Values)
            {
                if (target.Written is null || target.ExternalChange) continue;
                using var handle = OpenProcess(Access, false, target.Pid);
                if (handle.IsInvalid) { Fail(target, "停止时无法打开进程：" + Error()); failed++; continue; }
                if (!Matches(handle, target)) continue;
                if (Restore(handle, target)) restored++; else failed++;
            }
            Targets.Clear();
            DiscoveryFailures.Clear();
            Report($"CPU Sets 服务已停止：恢复 {restored}，失败 {failed}；外部修改不会被覆盖。");
        }
    }

    private static string SelectionText() => _selection.Length == 0
        ? "恢复系统默认 CPU Sets。" : $"选中 {_selection.Length} 个逻辑处理器，保留所选物理核的全部可用 SMT 线程；CPU Sets 不覆盖线程显式设置。";

    private static (bool Ok, string Message) Result(bool ok, string message)
    {
        _message = message;
        if (!ok) LaunchService.Diag("[GameCpu] " + message);
        return (ok, message);
    }

    private static void Report(string message)
    {
        _message = message;
        LaunchService.Diag("[GameCpu] " + message);
    }

    private static bool Fail(Target target, string message)
    {
        target.Attempt = _generation;
        Report($"PID {target.Pid}：{message}");
        return false;
    }

    private static void EnsureTimer() => _timer ??= new Timer(Tick, null, 1000, 1000);

    private static void Tick(object? state)
    {
        lock (Gate)
        {
            if (_stopped) return;
            try
            {
                Discover();
                foreach (var target in Targets.Values.ToArray())
                {
                    using var handle = OpenProcess(Access, false, target.Pid);
                    if (handle.IsInvalid)
                    {
                        if (Marshal.GetLastWin32Error() == 87) Targets.Remove(target.Pid);
                        else if (target.Attempt != _generation) Fail(target, "打开进程失败：" + Error());
                        continue;
                    }
                    if (!Matches(handle, target)) { Targets.Remove(target.Pid); continue; }
                    if (target.Attempt != _generation) Apply(handle, target);
                }
            }
            catch (Exception ex)
            {
                // A diagnostic is emitted only once until a new configuration requests a retry.
                if (DiscoveryFailures.Add(-1)) Report("进程跟踪失败：" + ex.Message);
            }
        }
    }

    private static bool Apply(SafeProcessHandle handle, Target target)
    {
        target.Attempt = _generation;
        if (target.ExternalChange) return Fail(target, "已发现外部 CPU Sets 修改，拒绝再次接管。");
        try
        {
            if (target.Written is not null && _selection.Length == 0) return Restore(handle, target);
            var current = ReadSets(handle);
            if (target.Written is not null)
            {
                if (!Equal(current, target.Written))
                {
                    target.ExternalChange = true;
                    return Fail(target, "CPU Sets 被外部修改，不覆盖。");
                }
            }
            else
            {
                if (current.Length != 0) return Fail(target, "已有非空默认 CPU Sets，拒绝接管。");
                target.Original = current;
            }
            if (_selection.Length == 0 || Equal(current, _selection)) return true;
            if (!Matches(handle, target)) return Fail(target, "写入前身份验证失败。");
            if (!SetProcessDefaultCpuSets(handle, _selection, (uint)_selection.Length))
                return Fail(target, "写入 CPU Sets 失败：" + Error());
            target.Written = _selection.ToArray();
            if (!Equal(ReadSets(handle), target.Written))
            {
                target.ExternalChange = true;
                return Fail(target, "CPU Sets 写入后校验不一致，停止接管。");
            }
            return true;
        }
        catch (Exception ex) { return Fail(target, ex.Message); }
    }

    private static bool Restore(SafeProcessHandle handle, Target target)
    {
        try
        {
            if (target.Written is null) return true;
            if (!Equal(ReadSets(handle), target.Written))
            {
                target.ExternalChange = true;
                return Fail(target, "恢复前发现外部修改，保留外部值。");
            }
            if (!Matches(handle, target)) return Fail(target, "恢复前身份验证失败。");
            var original = target.Original!;
            if (!SetProcessDefaultCpuSets(handle, original.Length == 0 ? null : original, (uint)original.Length))
                return Fail(target, "恢复 CPU Sets 失败：" + Error());
            if (!Equal(ReadSets(handle), original))
            {
                target.ExternalChange = true;
                return Fail(target, "恢复后校验不一致，停止接管。");
            }
            target.Written = null;
            return true;
        }
        catch (Exception ex) { return Fail(target, "恢复失败：" + ex.Message); }
    }

    private static void Discover()
    {
        if (Targets.Count == 0) return;
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
        {
            if (DiscoveryFailures.Add(-2)) Report("进程快照失败：" + Error());
            return;
        }
        var entries = new List<(int Pid, int Parent)>();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(snapshot, ref entry))
        {
            if (Marshal.GetLastWin32Error() != 18 && DiscoveryFailures.Add(-2)) Report("读取进程快照失败：" + Error());
            return;
        }
        do { entries.Add((unchecked((int)entry.Pid), unchecked((int)entry.ParentPid))); }
        while (Process32Next(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18)
        {
            if (DiscoveryFailures.Add(-2)) Report("进程快照枚举不完整：" + Error());
            return;
        }
        var liveIds = entries.Select(e => e.Pid).ToHashSet();
        foreach (int pid in Targets.Keys.Where(pid => !liveIds.Contains(pid)).ToArray()) Targets.Remove(pid);
        DiscoveryFailures.RemoveWhere(pid => pid > 0 && !liveIds.Contains(pid));
        bool added;
        do
        {
            added = false;
            foreach (var item in entries)
            {
                if (item.Pid <= 0 || item.Pid == Environment.ProcessId || Targets.ContainsKey(item.Pid)
                    || DiscoveryFailures.Contains(item.Pid) || !Targets.TryGetValue(item.Parent, out var parent)) continue;
                using var parentHandle = OpenProcess(Access, false, parent.Pid);
                if (parentHandle.IsInvalid || !Matches(parentHandle, parent)) continue;
                using var childHandle = OpenProcess(Access, false, item.Pid);
                if (childHandle.IsInvalid)
                {
                    if (DiscoveryFailures.Add(item.Pid)) Report($"后代 PID {item.Pid} 无法授权：{Error()}");
                    continue;
                }
                if (!Identity(childHandle, item.Pid, out var created) || created < parent.Created
                    || !Matches(parentHandle, parent)) continue;
                Targets[item.Pid] = new Target(item.Pid, created);
                added = true;
            }
        } while (added);
    }

    private static bool Identity(SafeProcessHandle handle, int pid, out ulong created)
    {
        created = 0;
        if (GetProcessId(handle) != (uint)pid || !GetProcessTimes(handle, out var start, out var exit, out _, out _) || exit != 0)
            return false;
        created = start;
        return true;
    }

    private static bool Matches(SafeProcessHandle handle, Target target) =>
        Identity(handle, target.Pid, out var created) && created == target.Created;

    private static bool Equal(uint[] left, uint[] right) => left.OrderBy(x => x).SequenceEqual(right.OrderBy(x => x));
    private static string Error() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    private static uint[] ReadSets(SafeProcessHandle handle)
    {
        uint size = 0;
        if (!GetProcessDefaultCpuSets(handle, null, 0, out size) && Marshal.GetLastWin32Error() != 122)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取默认 CPU Sets 失败");
        if (size == 0) return Array.Empty<uint>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (size > 1048576) throw new InvalidOperationException("CPU Sets 数量异常。");
            var sets = new uint[checked((int)size)];
            if (GetProcessDefaultCpuSets(handle, sets, size, out var actual))
            {
                if (actual > sets.Length) throw new InvalidOperationException("CPU Sets 长度异常。");
                return sets.Take((int)actual).ToArray();
            }
            if (Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error(), "读取默认 CPU Sets 失败");
            size = actual;
        }
        throw new InvalidOperationException("默认 CPU Sets 持续变化，拒绝写入。");
    }

    private static GameCpuTopology ReadTopology()
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, 0, "CPU Sets 仅支持 Windows。");
        try
        {
            uint size = 0;
            if (!GetSystemCpuSetInformation(IntPtr.Zero, 0, out size, new IntPtr(-1), 0)
                && Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error());
            var logical = new List<(uint Id, ushort Group, byte Logical, byte Core, byte Efficiency)>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (size < 20 || size > 16777216) throw new InvalidOperationException("CPU Sets 拓扑长度异常。");
                var buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    if (!GetSystemCpuSetInformation(buffer, size, out var actual, new IntPtr(-1), 0))
                    {
                        if (Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error());
                        size = actual;
                        continue;
                    }
                    if (actual > size) throw new InvalidOperationException("CPU Sets 返回长度异常。");
                    int offset = 0;
                    while (offset < actual)
                    {
                        if (actual - offset < 8) throw new InvalidOperationException("CPU Sets 记录头截断。");
                        var record = IntPtr.Add(buffer, offset);
                        uint length = unchecked((uint)Marshal.ReadInt32(record, 0));
                        int type = Marshal.ReadInt32(record, 4);
                        if (length < 8 || length > actual - offset) throw new InvalidOperationException("CPU Sets 记录长度异常。");
                        if (type == 0)
                        {
                            if (length < 20) throw new InvalidOperationException("CPU Sets 记录截断。");
                            byte flags = Marshal.ReadByte(record, 19);
                            // Parked (bit 0) remains eligible: Windows can unpark it.
                            if ((flags & 2) == 0 || (flags & 4) != 0)
                                logical.Add((unchecked((uint)Marshal.ReadInt32(record, 8)),
                                    unchecked((ushort)Marshal.ReadInt16(record, 12)), Marshal.ReadByte(record, 14), Marshal.ReadByte(record, 15),
                                    Marshal.ReadByte(record, 18)));
                        }
                        offset += checked((int)length);
                    }
                    var cores = logical.GroupBy(c => (c.Group, c.Core)).Select(group =>
                        new Core(group.Key.Group, group.Key.Core, group.Max(c => c.Efficiency),
                            group.Select(c => c.Id).Distinct().OrderBy(id => id).ToArray())).ToList();
                    if (cores.Count == 0) throw new InvalidOperationException("没有可用物理核心。");
                    Cores.AddRange(cores);
                    bool hybrid = cores.Select(c => c.Efficiency).Distinct().Count() > 1;
                    return new(true, hybrid, cores.Count, $"可用物理核心 {cores.Count}，逻辑处理器 {logical.Count}；" +
                        (hybrid ? "检测到不同 EfficiencyClass。" : "未检测到异构核心。"));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("CPU Sets 拓扑持续变化。");
        }
        catch (Exception ex)
        {
            LaunchService.Diag("[GameCpu] 拓扑不可用：" + ex.Message);
            return new(false, false, 0, "CPU Sets 不可用：" + ex.Message);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong exited, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessDefaultCpuSets(SafeProcessHandle process, [Out] uint[]? sets, uint count, out uint required);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDefaultCpuSets(SafeProcessHandle process, [In] uint[]? sets, uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint length, out uint returned, IntPtr process, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
