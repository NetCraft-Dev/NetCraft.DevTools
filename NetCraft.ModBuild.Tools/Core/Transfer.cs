namespace NetCraft.ModBuild.Core;

//Transfer 下载入口
//清单那类小块一次性读回 大文件流式落盘 落盘途中把字节进度报给回调
internal static class Transfer
{
    //Http 共用一份 超时按大文件放宽
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    //BufferSize 流式读取的块大小
    private const int BufferSize = 128 * 1024;

    //Enabled 是否画进度条
    //输出被重定向时不画 否则会把控制字符灌进管道 脚本里读输出会被弄脏
    internal static bool Enabled { get; set; } = !Console.IsOutputRedirected;

    //GetBytes 取一小段内容 几 KB 的清单走这里 不值得为它开进度
    public static byte[]? GetBytes(string url, out string error)
    {
        error = string.Empty;
        try
        {
            return Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Trace.Log($"download {url} failed: {error}");
            return null;
        }
    }

    //DownloadToFile 流式下载到目标路径 先落临时文件 整份写完再挪过去
    //report 每读到一块回调一次 服务端不给 Content-Length 时总长为 null
    public static bool DownloadToFile(string url, string target, Action<long, long?> report, out string error)
    {
        error = string.Empty;
        var temp = target + ".tmp";
        try
        {
            using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            using var source = response.Content.ReadAsStream();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using (var destination = File.Create(temp))
            {
                var buffer = new byte[BufferSize];
                long received = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    destination.Write(buffer, 0, read);
                    received += read;
                    report(received, total);
                }
            }

            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Trace.Log($"download {url} failed: {error}");
            Discard(temp);
            return false;
        }
    }

    //Discard 删掉写了一半的临时文件 删不掉就留给下次覆盖
    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException e)
        {
            Trace.Log($"cannot remove {path}: {e.Message}");
        }
    }
}
