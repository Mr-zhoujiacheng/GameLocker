using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GameLocker.Services;

/// <summary>
/// 局域网文件传输：UDP 广播发现同网段内同样开着「文件传输」的 GameLocker 设备，
/// TCP 直连传输文件。协议为逐行 JSON 控制消息 + 原始字节流（见 SendAsync / ServeAsync）。
/// 生命周期由主页文件传输浮层控制：打开浮层 Start，关闭 Stop（停止后不再监听端口）。
/// </summary>
public static class FileTransferService
{
    /// <summary>发现广播端口</summary>
    public const int UdpPort = 39871;
    /// <summary>传输端口（TCP 监听）</summary>
    public const int TcpPort = 39872;

    private const string Magic = "GAMELOCKER-FT-1";
    private const int ChunkSize = 64 * 1024;
    private const int PeerTimeoutMs = 12000;
    private const int OfferTimeoutMs = 60000;
    private const long MaxFileSize = 8L * 1024 * 1024 * 1024; // 单文件 8GB 上限，防异常报文

    /// <summary>发现到的对端</summary>
    public sealed record Peer(string Id, string Name, string Address);

    /// <summary>可发送的本地文件（发送目录顶层文件）</summary>
    public sealed record LocalFile(string Path, string Name, long Size);

    /// <summary>待确认的接收请求</summary>
    public sealed record IncomingOffer(string PeerName, int Count, long TotalBytes);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Peer> Peers = new();
    private static readonly List<LocalFile> LocalFiles = new();

    private static UdpClient? _udp;
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static System.Threading.Timer? _beacon;
    private static TaskCompletionSource<bool>? _offerDecision;
    private static TcpClient? _client;         // 主动连接对端时保持的长连接
    private static NetworkStream? _clientStream;
    private static Peer? _connectedPeer;       // 当前已连接的设备（发送时优先用它，避免误发到别的设备）

    private static string _deviceId = Guid.NewGuid().ToString("N");
    private static string _deviceName = Environment.MachineName;
    private static string _status = "";
    private static double _progress;
    private static bool _busy;
    private static IncomingOffer? _pendingOffer;
    private static int _running;

    /// <summary>状态变化（后台线程触发；界面需 marshal 回 UI 线程）</summary>
    public static event Action? StateChanged;

    /// <summary>是否已启动发现与监听</summary>
    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>发送目录（浮层里列出的文件）</summary>
    public static string SendFolder { get; private set; } = "";

    /// <summary>接收目录（收到的文件存到这里）</summary>
    public static string ReceiveFolder { get; private set; } = "";

    /// <summary>整体状态文字</summary>
    public static string Status { get { lock (Gate) return _status; } }

    /// <summary>传输进度 0..1（非传输时为 0）</summary>
    public static double Progress { get { lock (Gate) return _progress; } }

    /// <summary>是否有传输正在进行（含等待对方确认）</summary>
    public static bool Busy { get { lock (Gate) return _busy; } }

    /// <summary>待确认的接收请求（null=没有）</summary>
    public static IncomingOffer? PendingOffer { get { lock (Gate) return _pendingOffer; } }

    /// <summary>当前发现到的设备（按名称排序）</summary>
    public static IReadOnlyList<Peer> DiscoveredPeers
    {
        get { lock (Gate) return Peers.Values.OrderBy(p => p.Name).ToList(); }
    }

    /// <summary>发送目录下的文件（按修改时间新的在前，最多 200 个）</summary>
    public static IReadOnlyList<LocalFile> Files
    {
        get { lock (Gate) return LocalFiles.ToList(); }
    }

    /// <summary>系统「下载」文件夹（不存在时回落到用户目录）</summary>
    public static string DefaultReceiveFolder => KnownFolder("374DE290-123F-4565-9164-39C4925E467B") ?? UserProfile;

    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>启动发现与监听（重复调用只更新目录）</summary>
    public static void Start(string? sendFolder, string? receiveFolder)
    {
        SendFolder = string.IsNullOrWhiteSpace(sendFolder) ? DefaultReceiveFolder : sendFolder;
        ReceiveFolder = string.IsNullOrWhiteSpace(receiveFolder) ? DefaultReceiveFolder : receiveFolder;
        RefreshFiles();

        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        _deviceName = Environment.MachineName;
        _cts = new CancellationTokenSource();
        SetStatus("正在搜索局域网设备…");

        try
        {
            _udp = new UdpClient { EnableBroadcast = true };
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
        }
        catch (Exception ex)
        {
            _udp = null;
            SetStatus("局域网发现不可用：" + ex.Message);
        }

        if (_udp is not null) _ = DiscoverLoopAsync(_cts.Token);

        try
        {
            _listener = new TcpListener(IPAddress.Any, TcpPort);
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _listener = null;
            SetStatus("接收端口不可用：" + ex.Message);
        }

        // 每 2 秒广播一次自身；同时清理 12 秒未见的设备
        _beacon = new System.Threading.Timer(_ => Beacon(), null, 300, 2000);
    }

    /// <summary>停止发现与监听，断开连接</summary>
    public static void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        _beacon?.Dispose();
        _beacon = null;
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _clientStream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
        _udp = null;
        _listener = null;
        _client = null;
        _clientStream = null;
        _connectedPeer = null;
        _cts?.Dispose();
        _cts = null;
        lock (Gate)
        {
            Peers.Clear();
            LocalFiles.Clear();
            _status = "";
            _progress = 0;
            _busy = false;
            _pendingOffer = null;
        }
        _offerDecision?.TrySetResult(false);
        _offerDecision = null;
        Raise();
    }

    /// <summary>重新扫描发送目录</summary>
    public static void RefreshFiles()
    {
        var list = new List<(LocalFile File, DateTime Stamp)>();
        try
        {
            if (Directory.Exists(SendFolder))
            {
                foreach (var path in Directory.EnumerateFiles(SendFolder).Take(200))
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        list.Add((new LocalFile(path, fi.Name, fi.Length), fi.LastWriteTimeUtc));
                    }
                    catch { }
                }
            }
        }
        catch { }
        lock (Gate)
        {
            LocalFiles.Clear();
            LocalFiles.AddRange(list.OrderByDescending(f => f.Stamp).Select(f => f.File));
        }
        Raise();
    }

    /// <summary>连接指定设备（供界面选择对端后调用）</summary>
    public static async Task ConnectAsync(Peer peer)
    {
        if (!IsRunning || peer is null) return;
        if (await EnsureConnectedAsync(peer))
        {
            SetStatus($"已连接 {peer.Name}");
        }
        else
        {
            SetStatus($"连接 {peer.Name} 失败，对方可能未打开文件传输");
        }
    }

    /// <summary>把选中的文件发送给已连接设备</summary>
    public static async Task SendAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { SetStatus("没有待发送文件"); return; }
        if (!IsRunning) { SetStatus("文件传输未启动"); return; }

        var peer = _connectedPeer ?? DiscoveredPeers.FirstOrDefault();
        if (peer is null) { SetStatus("没有已连接的设备，请先选择设备按 A 连接"); return; }
        if (!await EnsureConnectedAsync(peer)) { SetStatus("连接已断开，请重新连接设备"); return; }

        var stream = _clientStream;
        if (stream is null) return;

        // 文件可能来自任意目录（拖入浮层），按传入的绝对路径逐一读取
        var files = new List<FileEntry>();
        var sources = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) continue;
                files.Add(new FileEntry { name = fi.Name, size = fi.Length });
                sources.Add(fi.FullName);
            }
            catch { }
        }
        if (files.Count == 0) { SetStatus("待发送文件已不存在"); return; }

        var total = files.Sum(f => f.size);
        lock (Gate) { _busy = true; _progress = 0; }
        SetStatus($"正在等待 {peer.Name} 确认…");

        try
        {
            var token = _cts?.Token ?? CancellationToken.None;
            await WriteLineAsync(stream, Serialize(new Msg
            {
                t = "offer", files = files, n = files.Count, total = total, name = _deviceName
            }), token);

            var reply = await ReadLineAsync(stream, token);
            if (reply is null || Deserialize(reply)?.t != "accept")
            {
                SetStatus("对方拒绝了传输");
                lock (Gate) { _busy = false; _pendingOffer = null; }
                return;
            }

            long sent = 0;
            SetStatus($"正在发送 {files.Count} 个文件…");
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                await WriteLineAsync(stream, Serialize(new Msg { t = "file", name = f.name, total = f.size }), token);
                await using var fs = File.OpenRead(sources[i]);
                var buf = new byte[ChunkSize];
                int read;
                while ((read = await fs.ReadAsync(buf, token)) > 0)
                {
                    await stream.WriteAsync(buf.AsMemory(0, read), token);
                    sent += read;
                    SetProgress(total > 0 ? (double)sent / total : 0);
                }
            }
            await WriteLineAsync(stream, Serialize(new Msg { t = "end", n = files.Count }), token);
            var done = await ReadLineAsync(stream, token);
            SetStatus(Deserialize(done ?? "")?.t == "done" ? $"发送完成（{files.Count} 个文件）" : "发送结束");
        }
        catch (Exception ex)
        {
            SetStatus("发送失败：" + ex.Message);
        }
        finally
        {
            lock (Gate) { _busy = false; _progress = 0; }
            Raise();
        }
    }

    /// <summary>接受对端的传输请求（界面 A 键调用）</summary>
    public static void AcceptOffer() => _offerDecision?.TrySetResult(true);

    /// <summary>拒绝对端的传输请求</summary>
    public static void RejectOffer() => _offerDecision?.TrySetResult(false);

    // ---------------- 发现 ----------------

    private static async Task DiscoverLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                var parts = text.Split('|');
                if (parts.Length < 3 || parts[0] != Magic) continue;
                var id = parts[1];
                var name = parts[2];
                if (id == _deviceId) continue;

                var address = result.RemoteEndPoint.Address.ToString();
                var changed = false;
                lock (Gate)
                {
                    if (!Peers.TryGetValue(id, out var old) || old.Name != name || old.Address != address)
                        changed = true;
                    Peers[id] = new Peer(id, name, address);
                    _lastSeen[id] = DateTime.UtcNow;
                }
                if (changed) Raise();
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
            catch { }
        }
    }

    private static void Beacon()
    {
        if (!IsRunning) return;
        try
        {
            // 清理超时设备
            var now = DateTime.UtcNow;
            var removed = false;
            lock (Gate)
            {
                foreach (var (id, peer) in Peers.ToList())
                {
                    if ((now - _lastSeen.GetValueOrDefault(id)).TotalMilliseconds > PeerTimeoutMs)
                    {
                        Peers.Remove(id);
                        _lastSeen.Remove(id);
                        removed = true;
                    }
                }
            }
            if (removed) Raise();

            var payload = Encoding.UTF8.GetBytes($"{Magic}|{_deviceId}|{_deviceName}");
            using var sender = new UdpClient { EnableBroadcast = true };
            sender.Send(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort));
        }
        catch { }
    }

    private static readonly Dictionary<string, DateTime> _lastSeen = new();

    // ---------------- 接收 ----------------

    private static async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            catch { return; }

            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private static async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var helloLine = await ReadLineAsync(stream, ct);
                var hello = Deserialize(helloLine ?? "");
                if (hello?.t != "hello") return;
                var peerName = string.IsNullOrWhiteSpace(hello.name) ? "未知设备" : hello.name!;

                // 已有传输在进行：直接告知忙，避免并发写同一目录
                if (Busy)
                {
                    await WriteLineAsync(stream, "{\"t\":\"busy\"}", ct);
                    return;
                }

                await WriteLineAsync(stream, Serialize(new Msg { t = "welcome", name = _deviceName }), ct);

                while (!ct.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(stream, ct);
                    if (line is null) return;
                    var msg = Deserialize(line);
                    if (msg is null) return;

                    if (msg.t == "offer")
                    {
                        var files = msg.files ?? [];
                        var total = msg.total;
                        if (files.Count == 0 || total < 0 || total > MaxFileSize * 8)
                        {
                            await WriteLineAsync(stream, "{\"t\":\"reject\"}", ct);
                            continue;
                        }

                        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _offerDecision = decision;
                        lock (Gate)
                        {
                            _pendingOffer = new IncomingOffer(peerName, files.Count, total);
                            _busy = true;
                            _progress = 0;
                        }
                        SetStatus($"{peerName} 想发送 {files.Count} 个文件（{FormatSize(total)}），等待接受…");

                        var accepted = await WaitDecisionAsync(decision, ct);
                        lock (Gate) { _pendingOffer = null; }
                        if (!accepted)
                        {
                            await WriteLineAsync(stream, "{\"t\":\"reject\"}", ct);
                            SetStatus("已拒绝接收");
                            lock (Gate) _busy = false;
                            return;
                        }

                        await WriteLineAsync(stream, "{\"t\":\"accept\"}", ct);
                        SetStatus($"正在接收 {peerName} 的文件…");

                        var received = 0;
                        long got = 0;
                        var target = ResolveReceiveFolder();
                        Directory.CreateDirectory(target);

                        while (true)
                        {
                            var fileLine = await ReadLineAsync(stream, ct);
                            if (fileLine is null) break;
                            var fm = Deserialize(fileLine);
                            if (fm?.t == "end") break;
                            if (fm?.t != "file" || string.IsNullOrWhiteSpace(fm.name)) continue;

                            var size = fm.total;
                            if (size < 0 || size > MaxFileSize) throw new InvalidDataException("文件过大");
                            var dest = UniquePath(target, SanitizeName(fm.name!));

                            await using (var fs = new FileStream(dest + ".part", FileMode.Create, FileAccess.Write, FileShare.None,
                                             ChunkSize, FileOptions.Asynchronous))
                            {
                                var buf = new byte[ChunkSize];
                                long left = size;
                                while (left > 0)
                                {
                                    var want = (int)Math.Min(buf.Length, left);
                                    var read = await stream.ReadAsync(buf.AsMemory(0, want), ct);
                                    if (read <= 0) throw new EndOfStreamException("连接中断");
                                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                                    left -= read;
                                    got += read;
                                    SetProgress(total > 0 ? (double)got / total : 0);
                                }
                            }
                            File.Move(dest + ".part", dest, true);
                            received++;
                        }

                        await WriteLineAsync(stream, Serialize(new Msg { t = "done", n = received }), ct);
                        SetStatus($"接收完成（{received} 个文件）→ {target}");
                        lock (Gate) { _busy = false; _progress = 0; }
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("接收中断：" + ex.Message);
            lock (Gate) { _busy = false; _progress = 0; _pendingOffer = null; }
        }
        finally
        {
            _offerDecision = null;
            Raise();
        }
    }

    private static async Task<bool> WaitDecisionAsync(TaskCompletionSource<bool> tcs, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(OfferTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using (linked.Token.Register(() => tcs.TrySetResult(false)))
        {
            return await tcs.Task;
        }
    }

    // ---------------- 连接 ----------------

    private static async Task<bool> EnsureConnectedAsync(Peer peer)
    {
        if (_client is { Connected: true } && _clientStream is not null) return true;
        try
        {
            _clientStream?.Dispose();
            _client?.Dispose();
            var client = new TcpClient { NoDelay = true };
            using var timeout = new CancellationTokenSource(6000);
            await client.ConnectAsync(IPAddress.Parse(peer.Address), TcpPort, timeout.Token);
            var stream = client.GetStream();
            var token = _cts?.Token ?? CancellationToken.None;

            await WriteLineAsync(stream, Serialize(new Msg { t = "hello", id = _deviceId, name = _deviceName }), token);
            var reply = await ReadLineAsync(stream, token);
            var msg = Deserialize(reply ?? "");
            if (msg?.t != "welcome")
            {
                client.Dispose();
                if (msg?.t == "busy") SetStatus("对方正在传输，请稍后再试");
                return false;
            }

            _client = client;
            _clientStream = stream;
            _connectedPeer = peer;
            return true;
        }
        catch { return false; }
    }

    // ---------------- 协议工具 ----------------

    private sealed class Msg
    {
        public string? t { get; set; }
        public string? id { get; set; }
        public string? name { get; set; }
        public int n { get; set; }
        public long total { get; set; }
        public List<FileEntry>? files { get; set; }
    }

    private sealed class FileEntry
    {
        public string name { get; set; } = "";
        public long size { get; set; }
    }

    private static string Serialize(Msg m) => JsonSerializer.Serialize(m) + "\n";

    private static Msg? Deserialize(string line)
    {
        try { return JsonSerializer.Deserialize<Msg>(line); }
        catch { return null; }
    }

    /// <summary>写一行控制消息（自动补 '\n'，接收端按行解析）</summary>
    private static async Task WriteLineAsync(Stream s, string line, CancellationToken ct)
    {
        if (!line.EndsWith('\n')) line += "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            int n;
            try { n = await s.ReadAsync(one, ct); }
            catch { return null; }
            if (n == 0) return buf.Count == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
            if (one[0] != (byte)'\r') buf.Add(one[0]);
            if (buf.Count > 16384) return null;
        }
    }

    /// <summary>文件名只取末段并清理非法字符，避免 ../ 之类的路径穿越</summary>
    private static string SanitizeName(string name)
    {
        var baseName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "未命名";
        foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        return baseName.Length > 180 ? baseName[..180] : baseName;
    }

    /// <summary>同名文件自动追加 (1)(2)…，不覆盖既有文件</summary>
    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(folder, $"{stem} ({DateTime.Now:HHmmss}){ext}");
    }

    private static string ResolveReceiveFolder()
    {
        var folder = ReceiveFolder;
        if (string.IsNullOrWhiteSpace(folder)) folder = DefaultReceiveFolder;
        return folder;
    }

    private static void SetStatus(string text)
    {
        lock (Gate) _status = text;
        Raise();
    }

    private static long _lastProgressTick;

    /// <summary>更新进度并按 120ms 节流通知界面（传输时每 64KB 一次，不能每次都重绘）</summary>
    private static void SetProgress(double value)
    {
        lock (Gate) _progress = value;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastProgressTick) < 120) return;
        Interlocked.Exchange(ref _lastProgressTick, now);
        Raise();
    }

    private static void Raise()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.0}GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.0}MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0}KB";
        return $"{bytes}B";
    }

    /// <summary>把单个文件大小格式化成短文本（界面用）</summary>
    public static string SizeText(long bytes) => FormatSize(bytes);

    /// <summary>已知文件夹路径（失败返回 null）</summary>
    private static string? KnownFolder(string guid)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            var id = new Guid(guid);
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out ptr) != 0) return null;
            var path = Marshal.PtrToStringUni(ptr);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch { return null; }
        finally { if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
