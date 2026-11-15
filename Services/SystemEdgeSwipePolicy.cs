using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>
/// 通过独立 UAC 助手写入计算机 AllowEdgeSwipe 策略。原值也保存在受管理员权限保护的注册表中。
/// 策略跟随持久化开关，退出不还原；关闭开关时提权恢复，避免每次启动/退出都要求授权。
/// </summary>
internal static class SystemEdgeSwipePolicy
{
    internal const string HelperArgument = "--edge-swipe-policy";
    private const string KeyPath = @"SOFTWARE\Policies\Microsoft\Windows\EdgeUI";
    private const string ValueName = "AllowEdgeSwipe";
    private const string BackupName = "GameLocker.OriginalAllowEdgeSwipe";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal readonly record struct Result(bool Ok, string Message);

    public static async Task<Result> SetAsync(bool block)
    {
        await Gate.WaitAsync();
        try
        {
            // 已授权的设置可跨程序重启保持；无恢复记录时，关闭不触碰外部策略。
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(KeyPath))
            {
                if (block && IsBlocked(key)) return new(true, "系统边缘滑动策略已禁用");
                if (!block && key?.GetValue(BackupName) == null) return new(true, "无需恢复系统策略");
            }
            return await RunElevatedAsync(block);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"边缘策略操作失败：{ex}");
            return new(false, "无法修改系统边缘滑动策略，请重试");
        }
        finally { Gate.Release(); }
    }

    private static async Task<Result> RunElevatedAsync(bool block)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定程序路径"))
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = HelperArgument + (block ? " enable " : " disable ")
                    + Environment.ProcessId + " " + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                WorkingDirectory = AppContext.BaseDirectory
            };
            // ShellExecute 的 UAC 等待放在后台线程，界面保持响应。
            using var helper = await Task.Run(() => Process.Start(start))
                ?? throw new IOException("无法启动系统策略助手");
            await helper.WaitForExitAsync();
            var result = FromExitCode(helper.ExitCode);
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(KeyPath);
            var completed = block ? IsBlocked(key) : key?.GetValue(BackupName) == null;
            return completed ? new(true, "系统边缘滑动策略已更新；部分系统需重新登录生效")
                : result.Ok ? new(false, "系统策略回读未通过，请重新授权重试") : result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return FromExitCode(1223);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"边缘策略提权失败：{ex}");
            return new(false, "无法启动系统策略助手，请重试");
        }
    }

    internal static Result FromExitCode(int code) => code switch
    {
        0 => new(true, "系统边缘滑动策略已更新；部分系统需重新登录生效"),
        1223 => new(false, "已取消管理员授权，设置未更改"),
        _ => new(false, "系统策略操作未完成，请重新授权重试")
    };

    /// <summary>只接受两个固定操作，不接收任意注册表路径或外部恢复文件。</summary>
    public static int RunHelper(string operation, int parentId = 0, long parentStart = 0)
    {
        if (operation is not ("enable" or "disable")) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != parentStart
                || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return 3;
            // 跨会话互斥锁覆盖整个读改写事务，防多个助手覆盖恢复点。
            using var mutex = new Mutex(false, @"Global\GameLocker.EdgeSwipePolicy.Admin.v1");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return 1460;
                if (parent.HasExited) return 3;
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = machine.CreateSubKey(KeyPath, writable: true)
                    ?? throw new IOException("无法打开系统策略");
                Apply(key, operation == "enable");
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
            // 不重启 Explorer，避免中断用户文件操作。
            SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Policy", 0x0002, 200, out _);
            return 0;
        }
        catch { return 1; }
    }

    // 注册表 key 由调用方提供，便于在临时 HKCU 测试键验证恢复逻辑。
    internal static void Apply(RegistryKey key, bool block)
    {
        var oldValue = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var oldKind = oldValue == null ? RegistryValueKind.None : key.GetValueKind(ValueName);
        var oldBackup = key.GetValue(BackupName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var backupKind = oldBackup == null ? RegistryValueKind.None : key.GetValueKind(BackupName);
        try { ApplyCore(key, block); }
        catch
        {
            // 写入/Flush 部分失败时尽力恢复操作前状态；父进程还会回读最终结果。
            try
            {
                if (oldValue == null) key.DeleteValue(ValueName, false);
                else key.SetValue(ValueName, oldValue, oldKind);
                if (oldBackup == null) key.DeleteValue(BackupName, false);
                else key.SetValue(BackupName, oldBackup, backupKind);
                key.Flush();
            }
            catch { /* 恢复记录仍在时，下次可重新授权处理。 */ }
            throw;
        }
    }

    private static void ApplyCore(RegistryKey key, bool block)
    {
        var backup = key.GetValue(BackupName) as string;
        if (block)
        {
            if (backup != null) _ = ReadSnapshot(backup); // 损坏记录不能被覆盖。
            if (IsBlocked(key)) return;
            // 外部已修改策略时，以当前值作为新恢复点。
            var value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var snapshot = new Snapshot
            {
                HadValue = value != null,
                Kind = value == null ? RegistryValueKind.None : key.GetValueKind(ValueName),
                Value = JsonSerializer.SerializeToElement(value)
            };
            key.SetValue(BackupName, JsonSerializer.Serialize(snapshot), RegistryValueKind.String);
            key.Flush(); // 原值持久化成功后才能写禁用策略。
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
            key.Flush();
            return;
        }

        if (backup == null) return;
        var original = ReadSnapshot(backup);
        if (IsBlocked(key))
        {
            if (original.HadValue) key.SetValue(ValueName, ReadValue(original), original.Kind);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.Flush();
        }
        // 当前值已由管理员改变时，保留其新设置。
        key.DeleteValue(BackupName, throwOnMissingValue: false);
        key.Flush();
    }

    private static bool IsBlocked(RegistryKey? key) => key?.GetValue(ValueName) is int value
        && value == 0 && key.GetValueKind(ValueName) == RegistryValueKind.DWord;

    private static Snapshot ReadSnapshot(string json)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json)
            ?? throw new InvalidDataException("策略恢复记录无效");
        if (snapshot.HadValue) _ = ReadValue(snapshot);
        else if (snapshot.Kind != RegistryValueKind.None || snapshot.Value.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("策略恢复记录不完整");
        return snapshot;
    }

    private static object ReadValue(Snapshot snapshot) => snapshot.Kind switch
    {
        RegistryValueKind.DWord => snapshot.Value.GetInt32(),
        RegistryValueKind.QWord => snapshot.Value.GetInt64(),
        RegistryValueKind.String or RegistryValueKind.ExpandString => snapshot.Value.GetString()!,
        RegistryValueKind.MultiString => snapshot.Value.Deserialize<string[]>()!,
        RegistryValueKind.Binary or RegistryValueKind.None => snapshot.Value.GetBytesFromBase64(),
        _ => throw new InvalidDataException("未知策略类型")
    };

    private sealed class Snapshot
    {
        [JsonRequired] public bool HadValue { get; set; }
        [JsonRequired] public RegistryValueKind Kind { get; set; }
        [JsonRequired] public JsonElement Value { get; set; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam,
        string lParam, uint flags, uint timeout, out IntPtr result);
}
