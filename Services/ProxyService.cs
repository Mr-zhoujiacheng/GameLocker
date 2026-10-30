using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameLocker.Models;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>
/// 小猫咪小组件（订阅式简易代理）：
/// 用户只填一条 Clash 订阅链接，组件负责下载并启动内置 mihomo 内核（缺失时自动从 GitHub 下载），
/// A 键 = 开/关系统代理连接；Y 键 = 强制更新订阅并测速，自动切换到最快的节点。
/// 内核配置用 proxy-provider 直接引用订阅，因此兼容 Clash / Mihomo 格式的订阅。
/// </summary>
public static class ProxyService
{
    // ---- 固定的本地 API（仅监听到 127.0.0.1）----
    private const string ApiSecret = "gameLockerClash";
    private const int ApiPort = 9098;
    private const string GroupName = "节点选择";
    /// <summary>全局模式下 mihomo 内置的全局分组名，选节点要落在这个分组上才生效</summary>
    private const string GlobalGroupName = "GLOBAL";
    private const string DefaultTestUrl = "http://www.gstatic.com/generate_204";

    /// <summary>当前生效的分组名：规则模式=自建"节点选择"，全局模式=内核内置 GLOBAL</summary>
    private static string GroupFor(LaunchItem item) =>
        string.Equals((item.ProxyMode ?? "").Trim(), "global", StringComparison.OrdinalIgnoreCase)
            ? GlobalGroupName : GroupName;

    /// <summary>代理模式（"rule"/"global"），非法值回退规则模式</summary>
    private static string ModeOf(LaunchItem item) =>
        string.Equals((item.ProxyMode ?? "").Trim(), "global", StringComparison.OrdinalIgnoreCase)
            ? "global" : "rule";

    private static string WorkDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "mihomo");
    private static string CoreExe => Path.Combine(WorkDir, "mihomo.exe");
    private static string ConfigPath => Path.Combine(WorkDir, "config.yaml");

    // ---- 运行状态快照（磁贴渲染用）----

    public sealed class Snapshot
    {
        /// <summary>内核是否在运行</summary>
        public bool Running;
        /// <summary>系统代理是否已开启且指向本组件端口</summary>
        public bool Connected;
        /// <summary>当前选中的节点名</summary>
        public string Node = "";
        /// <summary>订阅里的节点数量</summary>
        public int Count;
        /// <summary>失败原因（无错误为空）</summary>
        public string Error = "";
    }

    public static Snapshot Current { get; private set; } = new();

    public sealed record ToggleResult(bool Ok, bool On, string Message);
    public sealed record FastestResult(bool Ok, string Message, string Selected = "");

    /// <summary>首次下载内核的提示文案（内核已在磁盘上时返回 null）</summary>
    public static string? FirstRunHint() => File.Exists(CoreExe) ? null : "首次使用：正在下载代理内核（约 22 MB）…";

    // ==================== HTTP ====================

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static HttpRequestMessage ApiRequest(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{ApiPort}/{path}");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiSecret);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<(bool Ok, string Body)> ApiAsync(HttpMethod method, string path, string? json, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var request = ApiRequest(method, path, json);
            using var response = await Http.SendAsync(request, cts.Token);
            return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cts.Token));
        }
        catch
        {
            return (false, "");
        }
    }

    // ==================== 状态刷新 ====================

    private static bool _refreshing;

    /// <summary>探测内核与连接状态（异步；并发保护，同一时刻只跑一次）</summary>
    public static async Task RefreshAsync(LaunchItem item)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var snapshot = new Snapshot();
            var port = NormalizePort(item.ProxyPort);
            snapshot.Connected = IsSystemProxyOn()
                && SystemProxyServer().Contains($":{port}", StringComparison.Ordinal);

            if (CoreProcessRunning())
            {
                var (ok, body) = await ApiAsync(HttpMethod.Get, "version", null, 1500);
                snapshot.Running = ok;
                if (ok)
                {
                    var node = await ReadGroupAsync(GroupFor(item));
                    snapshot.Node = node.Now;
                    snapshot.Count = node.Count;
                }
            }

            Current = snapshot;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>同步版本（只读注册表/进程，不做网络请求），供磁贴预览等场景使用</summary>
    public static Snapshot Poll(LaunchItem item)
    {
        var port = NormalizePort(item.ProxyPort);
        return new Snapshot
        {
            Running = CoreProcessRunning(),
            Connected = IsSystemProxyOn()
                && SystemProxyServer().Contains($":{port}", StringComparison.Ordinal),
            Node = Current.Node,
            Count = Current.Count
        };
    }

    private static async Task<(string Now, int Count)> ReadGroupAsync(string group)
    {
        var (ok, body) = await ApiAsync(HttpMethod.Get, "proxies/" + Uri.EscapeDataString(group), null, 2000);
        if (!ok) return ("", 0);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var now = root.TryGetProperty("now", out var n) ? n.GetString() ?? "" : "";
            var count = root.TryGetProperty("all", out var all) ? all.GetArrayLength() : 0;
            return (now, count);
        }
        catch
        {
            return ("", 0);
        }
    }

    // ==================== 系统代理（注册表 + WinINet 通知） ====================

    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int OptionSettingsChanged = 39;
    private const int OptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static bool IsSystemProxyOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            return key?.GetValue("ProxyEnable") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    public static string SystemProxyServer()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            return key?.GetValue("ProxyServer") as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool SetSystemProxy(bool on, int port)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null) return false;
            if (on)
            {
                key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            }
            else
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            }
            InternetSetOption(IntPtr.Zero, OptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, OptionRefresh, IntPtr.Zero, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int NormalizePort(int port) => port is >= 1 and <= 65535 ? port : 7897;

    // ==================== A 键：开关连接 ====================

    public static async Task<ToggleResult> ToggleAsync(LaunchItem item)
    {
        var port = NormalizePort(item.ProxyPort);
        var running = CoreProcessRunning();
        var proxyOn = IsSystemProxyOn() && SystemProxyServer().Contains($":{port}", StringComparison.Ordinal);

        if (running && proxyOn)
        {
            KillCore();
            SetSystemProxy(false, port);
            await RefreshAsync(item);
            return new ToggleResult(true, false, "已断开连接");
        }

        var url = (item.ProxySubscription ?? "").Trim();
        if (url.Length == 0)
        {
            await RefreshAsync(item);
            return new ToggleResult(false, false, "还没有填写订阅链接：长按磁贴进入编辑页填写");
        }

        if (!await EnsureCoreAsync())
        {
            await RefreshAsync(item);
            return new ToggleResult(false, false, "代理内核下载失败：请检查网络后重试，或手动把 mihomo.exe 放到 " + WorkDir);
        }

        var portChanged = false;
        if (!running)
        {
            // 首选端口可能被 Clash Verge 等程序占用，自动换到相邻的空闲端口
            var free = await PickFreePortAsync(port);
            portChanged = free != port;
            if (portChanged) item.ProxyPort = port = free;

            var startError = await StartCoreAsync(item);
            if (startError.Length > 0)
            {
                KillCore();
                SetSystemProxy(false, port);
                await RefreshAsync(item);
                return new ToggleResult(false, false, startError);
            }
        }

        if (!SetSystemProxy(true, port))
        {
            await RefreshAsync(item);
            return new ToggleResult(false, false, "设置系统代理失败（注册表不可写）");
        }

        await ApplyAsync(item); // 内核可能还在用旧设置：把当前模式与选中节点生效

        var node = await ReadGroupAsync(GroupFor(item));
        await RefreshAsync(item);
        var tail = portChanged ? $"（端口 {port}）" : "";
        return new ToggleResult(true, true,
            node.Now.Length > 0 ? $"已连接（节点：{node.Now}）{tail}" : $"已连接（订阅加载中…）{tail}");
    }

    // ==================== Y 键：更新订阅 + 测速选最快 ====================

    public static async Task<FastestResult> RefreshFastestAsync(LaunchItem item)
    {
        var url = (item.ProxySubscription ?? "").Trim();
        if (url.Length == 0)
        {
            await RefreshAsync(item);
            return new FastestResult(false, "还没有填写订阅链接：长按磁贴进入编辑页填写");
        }

        if (!await EnsureCoreAsync())
        {
            await RefreshAsync(item);
            return new FastestResult(false, "代理内核下载失败：请检查网络后重试");
        }

        if (!CoreProcessRunning())
        {
            var free = await PickFreePortAsync(NormalizePort(item.ProxyPort));
            item.ProxyPort = free;
            var error = await StartCoreAsync(item);
            if (error.Length > 0)
            {
                KillCore();
                await RefreshAsync(item);
                return new FastestResult(false, error);
            }
        }

        // 强制更新订阅（provider 名固定为 sub）
        await ApiAsync(HttpMethod.Put, "providers/proxies/sub", "{}", 8000);
        await Task.Delay(800);

        var (ok, body) = await ApiAsync(HttpMethod.Get, "proxies", null, 3000);
        if (!ok)
        {
            await RefreshAsync(item);
            return new FastestResult(false, "读取节点列表失败：内核没有响应");
        }

        var nodes = new List<string>();
        var groupType = "";
        var group = GroupFor(item);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.GetProperty("proxies");
            if (root.TryGetProperty(group, out var selector))
            {
                if (selector.TryGetProperty("type", out var tp)) groupType = tp.GetString() ?? "";
                if (selector.TryGetProperty("all", out var all))
                {
                    foreach (var node in all.EnumerateArray())
                    {
                        var name = node.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) nodes.Add(name);
                    }
                }
            }
        }
        catch
        {
            await RefreshAsync(item);
            return new FastestResult(false, "节点列表解析失败");
        }

        if (!string.Equals(groupType, "Selector", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshAsync(item);
            return new FastestResult(false, "节点分组异常（类型 " + groupType + "），请检查订阅是否有效");
        }
        if (nodes.Count == 0)
        {
            await RefreshAsync(item);
            return new FastestResult(false, "订阅里没有可用节点：请检查订阅链接是否有效");
        }

        // 订阅节点来自 proxy-provider，只能按分组批量测速（/proxies/{name}/delay 对 provider 节点返回 404）
        var testUrl = Uri.EscapeDataString(DefaultTestUrl);
        var (delayOk, delayBody) = await ApiAsync(HttpMethod.Get,
            $"group/{Uri.EscapeDataString(group)}/delay?url={testUrl}&timeout=5000", null, 25000);

        var bestName = "";
        var bestDelay = int.MaxValue;
        var measured = 0;
        if (delayOk)
        {
            try
            {
                using var doc = JsonDocument.Parse(delayBody);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var ms = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt32() : 0;
                    if (ms <= 0) continue;
                    measured++;
                    if (ms < bestDelay) { bestDelay = ms; bestName = prop.Name; }
                }
            }
            catch
            {
                await RefreshAsync(item);
                return new FastestResult(false, "测速结果解析失败");
            }
        }

        if (bestName.Length == 0)
        {
            await RefreshAsync(item);
            return new FastestResult(false, $"{nodes.Count} 个节点测速全部超时：请检查订阅是否可用或网络是否通畅");
        }

        var switchBody = JsonSerializer.Serialize(new { name = bestName });
        var (swOk, _) = await ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(group), switchBody, 5000);
        item.ProxySelected = bestName;
        await RefreshAsync(item);
        return swOk
            ? new FastestResult(true, $"已切换到最快节点：{bestName}（{bestDelay} ms，共测 {measured} 个）", bestName)
            : new FastestResult(false, $"最快节点 {bestName}（{bestDelay} ms），但切换失败");
    }

    // ==================== 编辑页：节点列表（下拉用） ====================

    /// <summary>节点条目：名称 + 延迟毫秒（0=没测到/超时）</summary>
    public sealed record NodeEntry(string Name, int Delay);

    /// <summary>当前内核配置的指纹（端口|模式|订阅），用于判断设置改动后要不要重启内核</summary>
    private static string _configSig = "";

    private static string SignatureOf(LaunchItem item) =>
        $"{NormalizePort(item.ProxyPort)}|{ModeOf(item)}|{(item.ProxySubscription ?? "").Trim()}";

    /// <summary>
    /// 编辑页用：确保内核运行、强制更新一次订阅、对整组节点批量测速，返回可选的节点与延迟。
    /// 订阅 / 端口 / 模式跟正在运行的内核不一致时会先重启内核再拉取。
    /// </summary>
    public static async Task<(bool Ok, string Message, List<NodeEntry> Nodes)> FetchNodesAsync(LaunchItem item)
    {
        var url = (item.ProxySubscription ?? "").Trim();
        if (url.Length == 0) return (false, "先填订阅链接，保存后会自动获取节点列表", []);

        if (!await EnsureCoreAsync()) return (false, "代理内核准备失败：请检查网络后重试", []);

        if (!CoreProcessRunning() || _configSig != SignatureOf(item))
        {
            KillCore();
            item.ProxyPort = await PickFreePortAsync(NormalizePort(item.ProxyPort));
            var error = await StartCoreAsync(item);
            if (error.Length > 0)
            {
                KillCore();
                return (false, error, []);
            }
        }

        // 强制更新订阅（provider 名固定为 sub）
        await ApiAsync(HttpMethod.Put, "providers/proxies/sub", "{}", 8000);
        await Task.Delay(600);

        var group = GroupFor(item);
        var (ok, body) = await ApiAsync(HttpMethod.Get, "proxies/" + Uri.EscapeDataString(group), null, 3000);
        if (!ok) return (false, "读取节点列表失败：内核没有响应", []);

        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("all", out var all))
            {
                foreach (var node in all.EnumerateArray())
                {
                    var name = node.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
            }
        }
        catch
        {
            return (false, "节点列表解析失败", []);
        }

        if (names.Count == 0) return (false, "订阅里没有可用节点：请检查订阅链接是否有效", []);

        // 订阅节点来自 proxy-provider，只能按分组批量测速
        var testUrl = Uri.EscapeDataString(DefaultTestUrl);
        var (delayOk, delayBody) = await ApiAsync(HttpMethod.Get,
            $"group/{Uri.EscapeDataString(group)}/delay?url={testUrl}&timeout=5000", null, 30000);

        var delays = new Dictionary<string, int>();
        if (delayOk)
        {
            try
            {
                using var doc = JsonDocument.Parse(delayBody);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var ms = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt32() : 0;
                    if (ms > 0) delays[prop.Name] = ms;
                }
            }
            catch { }
        }

        var entries = names.Select(n => new NodeEntry(n, delays.GetValueOrDefault(n, 0))).ToList();
        // 缓存本次测速结果：再次进入编辑页 / 切换模式时直接复用，不再联网拉取
        _nodeCache[CacheKey(url, ModeOf(item))] = [.. entries];
        return (true, $"共 {names.Count} 个节点", entries);
    }

    /// <summary>最近一次「测速」拉到的节点（按 订阅|模式 缓存；没测过返回空列表）</summary>
    public static List<NodeEntry> CachedNodes(string subscription, string mode)
    {
        if (!_nodeCache.TryGetValue(CacheKey(subscription, mode), out var nodes)) return [];
        return [.. nodes];
    }

    private static string CacheKey(string subscription, string mode) =>
        subscription.Trim() + "|" + mode.Trim().ToLowerInvariant();

    /// <summary>编辑页节点缓存：每次测速的结果按 订阅|模式 保存</summary>
    private static readonly Dictionary<string, List<NodeEntry>> _nodeCache = [];

    /// <summary>把当前设置（代理模式 + 选中节点）应用到已运行的内核；内核没跑就直接返回</summary>
    public static async Task ApplyAsync(LaunchItem item)
    {
        if (!CoreProcessRunning()) return;
        await ApiAsync(HttpMethod.Put, "configs",
            JsonSerializer.Serialize(new { mode = ModeOf(item) }), 4000);
        var target = (item.ProxySelected ?? "").Trim();
        if (target.Length > 0)
            await ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(GroupFor(item)),
                JsonSerializer.Serialize(new { name = target }), 3000);
    }

    // ==================== 内核进程管理 ====================

    private static Process? _core;
    private static readonly StringBuilder CoreLog = new();

    private static bool CoreProcessRunning()
    {
        if (_core is { HasExited: false }) return true;
        try
        {
            foreach (var p in Process.GetProcessesByName("mihomo"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, CoreExe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    private static void KillCore()
    {
        try
        {
            if (_core is { HasExited: false }) _core.Kill(entireProcessTree: true);
            _core?.Dispose();
        }
        catch { }
        _core = null;
        try
        {
            foreach (var p in Process.GetProcessesByName("mihomo"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, CoreExe, StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    /// <summary>确保内核存在：优先使用工作目录里的 mihomo.exe，没有则从 GitHub 下载最新版</summary>
    private static async Task<bool> EnsureCoreAsync()
    {
        if (File.Exists(CoreExe)) return true;
        Directory.CreateDirectory(WorkDir);
        var downloadUrl = await FindCoreUrlAsync();
        if (downloadUrl is null) return false;
        var tempZip = Path.Combine(WorkDir, "mihomo.zip");
        // 国内直连 GitHub Releases 经常只有几十 KB/s，先走加速镜像，失败再直连
        foreach (var prefix in new[] { "https://ghfast.top/", "", "https://gh-proxy.com/" })
        {
            if (!await TryDownloadAsync(prefix + downloadUrl, tempZip)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(tempZip);
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null) break;
                entry.ExtractToFile(CoreExe, overwrite: true);
                return File.Exists(CoreExe);
            }
            catch { }
            finally
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
        return false;
    }

    // ==================== 内核版本 / 更新 ====================

    private static string _coreVersion = "";

    /// <summary>最近一次启动内核用的配置（更新内核后按它重启）</summary>
    private static LaunchItem? _lastItem;

    public sealed record CoreUpdateResult(bool Ok, string Message, string Version);

    /// <summary>读取当前内核版本（执行 mihomo -v；未下载返回空串），结果本次运行内缓存</summary>
    public static async Task<string> GetCoreVersionAsync()
    {
        if (_coreVersion.Length > 0) return _coreVersion;
        if (!File.Exists(CoreExe)) return "";
        try
        {
            var psi = new ProcessStartInfo(CoreExe, "-v")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = WorkDir
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            if (output.Length == 0) output = (await p.StandardError.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            var match = Regex.Match(output, @"v?\d+(?:\.\d+)+[^\s]*");
            _coreVersion = match.Success ? match.Value : output;
        }
        catch { }
        return _coreVersion;
    }

    /// <summary>检查 GitHub 上是否有更新的内核；有则下载替换（内核运行中会先停、替换后按原配置重启）</summary>
    public static async Task<CoreUpdateResult> CheckAndUpdateCoreAsync()
    {
        var current = await GetCoreVersionAsync();
        var (url, tag) = await FindCoreReleaseAsync();
        if (url is null) return new(false, "检查失败：无法访问 GitHub（请检查网络或代理）", current);

        if (current.Length > 0 && SameVersion(current, tag))
            return new(true, $"已是最新版本（{tag}）", current);

        var restart = CoreProcessRunning();
        Directory.CreateDirectory(WorkDir);
        var tempZip = Path.Combine(WorkDir, "mihomo-update.zip");
        var tempExe = Path.Combine(WorkDir, "mihomo-new.exe");
        foreach (var prefix in new[] { "https://ghfast.top/", "", "https://gh-proxy.com/" })
        {
            if (!await TryDownloadAsync(prefix + url, tempZip)) continue;
            try
            {
                using (var zip = ZipFile.OpenRead(tempZip))
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (entry is null) continue;
                    try { File.Delete(tempExe); } catch { }
                    entry.ExtractToFile(tempExe, overwrite: true);
                }
            }
            catch { }
            finally
            {
                try { File.Delete(tempZip); } catch { }
            }
            if (!File.Exists(tempExe)) continue;

            if (restart)
            {
                KillCore();
                await Task.Delay(300);   // 等文件句柄释放
            }
            try
            {
                File.Move(tempExe, CoreExe, overwrite: true);
            }
            catch (Exception ex)
            {
                return new(false, "替换内核失败：" + ex.Message, current);
            }

            _coreVersion = "";   // 版本变了，重新读取
            var version = await GetCoreVersionAsync();
            if (restart && _lastItem is not null)
            {
                _configSig = "";
                await StartCoreAsync(_lastItem);
            }
            return new(true,
                (current.Length > 0 ? $"已更新 {current} → {version}" : $"内核已下载（{version}）")
                + (restart ? "，并已重启内核" : ""), version);
        }
        try { File.Delete(tempExe); } catch { }
        return new(false, "下载新内核失败：请检查网络后重试（GitHub 可能需要代理）", current);
    }

    /// <summary>比较版本号（忽略前缀 v 与后缀）</summary>
    private static bool SameVersion(string current, string latest)
    {
        var a = Regex.Match(current, @"\d+(?:\.\d+)+").Value;
        var b = Regex.Match(latest, @"\d+(?:\.\d+)+").Value;
        return a.Length > 0 && a == b;
    }

    /// <summary>查询 GitHub 最新版的 windows-amd64-compatible 压缩包地址</summary>
    private static async Task<string?> FindCoreUrlAsync() => (await FindCoreReleaseAsync()).Url;

    /// <summary>查询 GitHub 最新版内核的下载地址与版本标签（失败返回 null / 空串）</summary>
    private static async Task<(string? Url, string Tag)> FindCoreReleaseAsync()
    {
        try
        {
            using var api = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest");
            api.Headers.TryAddWithoutValidation("User-Agent", "GameLocker");
            using var apiResponse = await Http.SendAsync(api);
            if (!apiResponse.IsSuccessStatusCode) return (null, "");
            using var doc = JsonDocument.Parse(await apiResponse.Content.ReadAsStringAsync());
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("windows-amd64-compatible", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return (asset.GetProperty("browser_download_url").GetString(), tag);
            }
        }
        catch { }
        return (null, "");
    }

    /// <summary>下载到临时文件（单次上限 2 分钟，太慢的源直接放弃换下一个）</summary>
    private static async Task<bool> TryDownloadAsync(string url, string target)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url),
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) return false;
            await using var file = File.Create(target);
            await response.Content.CopyToAsync(file, cts.Token);
            return true;
        }
        catch
        {
            try { File.Delete(target); } catch { }
            return false;
        }
    }

    /// <summary>写配置并启动内核，等待 API 与订阅就绪；成功返回空串，失败返回原因</summary>
    private static async Task<string> StartCoreAsync(LaunchItem item)
    {
        _lastItem = item.Clone();   // 内核更新后按这份配置重启
        var port = NormalizePort(item.ProxyPort);
        var url = (item.ProxySubscription ?? "").Trim();
        if (await IsPortInUseAsync(port)) return $"端口 {port} 已被占用，请在编辑页换一个端口";

        try
        {
            Directory.CreateDirectory(WorkDir);
            var yaml = BuildConfigYaml(port, url, ModeOf(item));
            await File.WriteAllTextAsync(ConfigPath, yaml, new UTF8Encoding(false));
            _configSig = SignatureOf(item);

            CoreLog.Clear();
            var psi = new ProcessStartInfo(CoreExe, $"-f \"{ConfigPath}\" -d \"{WorkDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = WorkDir
            };
            _core = Process.Start(psi);
            if (_core is null) return "启动代理内核失败";
            _core.OutputDataReceived += (_, e) => AppendLog(e.Data);
            _core.ErrorDataReceived += (_, e) => AppendLog(e.Data);
            _core.BeginOutputReadLine();
            _core.BeginErrorReadLine();

            // 等 API 起来
            var apiReady = false;
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(300);
                if (_core.HasExited) return "代理内核启动失败：" + CoreLogTail();
                var (ok, _) = await ApiAsync(HttpMethod.Get, "version", null, 1000);
                if (ok) { apiReady = true; break; }
            }
            if (!apiReady) return "代理内核没有响应（API 端口 " + ApiPort + "）";

            // 等订阅下载并解析出节点
            var group = GroupFor(item);
            for (var i = 0; i < 45; i++)
            {
                var (_, body) = await ApiAsync(HttpMethod.Get, "proxies/" + Uri.EscapeDataString(group), null, 1500);
                if (TryCountNodes(body) > 0)
                {
                    // 恢复上次选中的节点（mihomo 的 store-selected 也会记住，双保险）
                    var selected = (item.ProxySelected ?? "").Trim();
                    if (selected.Length > 0)
                        await ApiAsync(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(group),
                            JsonSerializer.Serialize(new { name = selected }), 3000);
                    return "";
                }
                await Task.Delay(300);
            }
            return "订阅加载失败：请检查订阅链接是否有效（需要 Clash / Mihomo 格式的订阅）";
        }
        catch (Exception ex)
        {
            return "启动代理内核失败：" + ex.Message;
        }
    }

    private static int TryCountNodes(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.TryGetProperty("all", out var all) ? all.GetArrayLength() : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (CoreLog)
        {
            if (CoreLog.Length > 4000) CoreLog.Clear();
            CoreLog.AppendLine(line);
        }
    }

    private static string CoreLogTail()
        => CoreLog.ToString().Trim().Split('\n').TakeLast(3).Aggregate("", (a, b) => a + b.Trim() + " ");

    /// <summary>从首选端口开始找第一个空闲端口（首选被占用时依次 +1，最多试 10 个）</summary>
    private static async Task<int> PickFreePortAsync(int preferred)
    {
        for (var i = 0; i < 10; i++)
        {
            var candidate = preferred + i;
            if (candidate <= 65535 && !await IsPortInUseAsync(candidate)) return candidate;
        }
        return preferred;
    }

    private static async Task<bool> IsPortInUseAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(400);
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>生成内核配置：mixed-port + 本地 API + 用 proxy-provider 直接引用订阅</summary>
    private static string BuildConfigYaml(int port, string subscriptionUrl, string mode)
    {
        var escaped = subscriptionUrl.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"""
mixed-port: {port}
allow-lan: false
bind-address: 127.0.0.1
mode: {mode}
log-level: silent
ipv6: true
unified-delay: true
tcp-concurrent: true
external-controller: 127.0.0.1:{ApiPort}
secret: "{ApiSecret}"
profile:
  store-selected: true
  store-fake-ip: true
proxy-providers:
  sub:
    type: http
    url: "{escaped}"
    path: ./sub.yaml
    interval: 3600
    header:
      User-Agent:
        - "clash-verge/v2.5.8"
    health-check:
      enable: true
      url: {DefaultTestUrl}
      interval: 300
proxy-groups:
  - name: {GroupName}
    type: select
    use:
      - sub
rules:
  - MATCH,{GroupName}
""";
    }
}
