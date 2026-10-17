namespace GameLocker.Services;

/// <summary>
/// 带进度回调的流式拷贝：边读边写，按 ~200ms 节流回报 (已接收字节, 总字节)，结束时无论是否
/// 落在节流点上都会补报一次，保证提示条里的小进度条能走到 100%。
/// 服务端未给出 Content-Length 时 total 为 null，调用方退化为只显示已下载体积。
/// </summary>
internal static class DownloadProgress
{
    private const int BufferSize = 1 << 16; // 64KB

    /// <summary>把 source 拷到 destination，边拷边回调进度（回调可能来自后台线程）</summary>
    public static async Task CopyAsync(Stream source, long? total, Stream destination,
        Action<long, long?> onProgress)
    {
        var buffer = new byte[BufferSize];
        long got = 0;
        var last = Environment.TickCount64;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read));
            got += read;
            if (Environment.TickCount64 - last < 200) continue;
            last = Environment.TickCount64;
            onProgress(got, total);
        }
        onProgress(got, total);
    }

    /// <summary>字节数 → MB 文本（1 位小数），用于提示条里的「3.2/25.0 MB」</summary>
    public static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1");
}
