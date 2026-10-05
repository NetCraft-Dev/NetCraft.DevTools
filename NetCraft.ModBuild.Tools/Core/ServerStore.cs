using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NetCraft.ModBuild.Core;

//ServerFile 清单里的一条
//Hash 为空表示这份清单只有路径 那种情况下只按文件在不在判断
internal readonly record struct ServerFile(string Path, string Hash);

//ServerStore 服务端运行时文件的本地缓存
//目录落在程序根目录 本地齐备就不联网 缺哪个补哪个 每个文件给几次重试
//只有 Refresh 那条路会按哈希比对 它会让内容对不上的文件重下
public static class ServerStore
{
    //DirectoryName 缓存目录名
    private const string DirectoryName = "Server";

    //IndexFileName 清单文件名 一行一条 哈希与相对路径之间两个空格
    public const string IndexFileName = "index.txt";

    //BaseUrl 清单与各文件的下载基准
    private const string BaseUrl =
        "https://raw.githubusercontent.com/NetCraft-Dev/NetCraft.Release/refs/heads/main/server/";

    //Attempts 单个文件最多试几次
    private const int Attempts = 5;

    //HashSeparator 清单里哈希与路径的分隔 两个空格 与 sha256sum 的输出一致
    private const string HashSeparator = "  ";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    //Root 缓存目录 程序根目录下的 Server
    public static string Root => Path.Combine(AppContext.BaseDirectory, DirectoryName);

    //IndexPath 清单的本地路径
    public static string IndexPath => Path.Combine(Root, IndexFileName);

    //Ensure 保证缓存里的文件齐备 返回是否可用
    //只认本地那份清单 不联网 远端有没有更新交给 Refresh
    public static bool Ensure()
    {
        var entries = ReadIndex();
        if (entries.Count == 0)
        {
            Console.WriteLine($"Server cache is empty, downloading {IndexFileName}");
            if (!TryDownload(IndexFileName, out var indexError))
            {
                Console.WriteLine($"error: failed to download {BaseUrl}{IndexFileName}: {indexError}");
                return false;
            }

            entries = ReadIndex();
        }

        if (entries.Count == 0)
        {
            Console.WriteLine($"error: {IndexPath} lists no file");
            return false;
        }

        var missing = new List<ServerFile>();
        foreach (var entry in entries)
        {
            if (!IsForHost(entry.Path))
                continue;

            var target = ResolveCachePath(entry.Path);
            if (target is null || !File.Exists(target))
                missing.Add(entry);
        }

        if (missing.Count == 0)
        {
            Trace.Log($"server cache hit {Root} ({RuntimeInformation.RuntimeIdentifier})");
            return true;
        }

        Console.WriteLine($"Downloading {missing.Count} server file(s) to {Root}");
        return Download(missing);
    }

    //Refresh 拉远端清单覆盖本地 再按哈希补齐差异文件 返回是否可用
    //与 Ensure 的区别是清单来自远端 且本地内容对不上也要重下
    public static bool Refresh()
    {
        Console.WriteLine($"Refreshing server cache from {BaseUrl}{IndexFileName}");
        if (!TryDownload(IndexFileName, out var error))
        {
            Console.WriteLine($"error: failed to download {BaseUrl}{IndexFileName}: {error}");
            return false;
        }

        var entries = ReadIndex();
        if (entries.Count == 0)
        {
            Console.WriteLine($"error: {IndexPath} lists no file");
            return false;
        }

        var stale = new List<ServerFile>();
        foreach (var entry in entries)
        {
            if (!IsForHost(entry.Path))
                continue;

            var target = ResolveCachePath(entry.Path);
            if (target is null || !File.Exists(target) || !HashMatches(target, entry.Hash))
                stale.Add(entry);
        }

        if (stale.Count == 0)
        {
            Console.WriteLine("Server cache is up to date");
            return true;
        }

        Console.WriteLine($"Downloading {stale.Count} changed server file(s) to {Root}");
        return Download(stale);
    }

    //Download 逐条下载 有一条失败就返回 false
    private static bool Download(List<ServerFile> files)
    {
        for (var index = 0; index < files.Count; index++)
        {
            var entry = files[index];
            if (TryDownload(entry.Path, out var error))
            {
                Trace.Log($"server file {index + 1}/{files.Count} done {entry.Path}");
                continue;
            }

            Console.WriteLine($"error: failed to download {entry.Path} after {Attempts} attempt(s): {error}");
            return false;
        }
        return true;
    }

    //IsForHost 清单里这条要不要下
    //清单是全平台的 原生库按平台分了目录 别的平台那份下下来也加载不了
    //runtimes 下按 RID 分 TraceEvent 的原生组件按架构分 两种都要挑 其余文件平台无关一律要
    private static bool IsForHost(string entry)
    {
        var segments = entry.Split('/');
        if (segments.Length >= 2 && segments[0] == "runtimes")
        {
            return MatchesRuntime(segments[1]);
        }

        var architecture = RuntimeInformation.ProcessArchitecture;
        return segments[0] switch
        {
            "amd64" => architecture == Architecture.X64,
            "x86" => architecture == Architecture.X86,
            "arm64" => architecture == Architecture.Arm64,
            "arm" => architecture == Architecture.Arm,
            _ => true,
        };
    }

    //MatchesRuntime 平台目录名与本机是否对得上
    //win-x64 这类直接相等 也有 osx 这种只带系统的 和 linux-musl-x64 这种带发行版变体的 后两种按系统加架构段认
    private static bool MatchesRuntime(string rid)
    {
        var host = RuntimeInformation.RuntimeIdentifier;
        if (string.Equals(rid, host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var separator = host.IndexOf('-');
        if (separator < 0)
        {
            return false;
        }

        var system = host[..separator];
        var architecture = host[(separator + 1)..];
        return string.Equals(rid, system, StringComparison.OrdinalIgnoreCase)
            || (rid.StartsWith(system + "-", StringComparison.OrdinalIgnoreCase)
                && rid.EndsWith("-" + architecture, StringComparison.OrdinalIgnoreCase));
    }

    //HashMatches 本地文件的 sha256 与清单里那条是否一致
    //清单没带哈希就无从比 按一致处理 读不出内容同样按不一致处理
    private static bool HashMatches(string path, string expected)
    {
        if (expected.Length == 0)
            return true;

        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException e)
        {
            Trace.Log($"cannot hash {path}: {e.Message}");
            return false;
        }
    }

    //ReadIndex 读清单 没有或读不出返回空表
    private static List<ServerFile> ReadIndex()
    {
        if (!File.Exists(IndexPath))
            return new List<ServerFile>();

        try
        {
            return File.ReadAllLines(IndexPath)
                .Select(ParseEntry)
                .Where(entry => entry.Path.Length > 0)
                .ToList();
        }
        catch (IOException e)
        {
            Trace.Log($"cannot read {IndexPath}: {e.Message}");
            return new List<ServerFile>();
        }
    }

    //ParseEntry 拆开清单里的一条 认不出哈希时整行当路径
    private static ServerFile ParseEntry(string line)
    {
        var text = line.Trim();
        var separator = text.IndexOf(HashSeparator, StringComparison.Ordinal);
        if (separator > 0 && IsHex(text.AsSpan(0, separator)))
            return new ServerFile(text[(separator + HashSeparator.Length)..].Replace('\\', '/'), text[..separator]);

        return new ServerFile(text.Replace('\\', '/'), string.Empty);
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
            return false;

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    //TryDownload 下载一份文件 失败重试到上限
    //先落临时文件再整体挪过去 半截内容不会留在缓存里
    private static bool TryDownload(string relative, out string error)
    {
        var target = ResolveCachePath(relative);
        if (target is null)
        {
            error = $"path escapes the cache directory: {relative}";
            return false;
        }

        error = string.Empty;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                var bytes = Http.GetByteArrayAsync(Url(relative)).GetAwaiter().GetResult();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, target, overwrite: true);
                return true;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                Trace.Log($"download {relative} attempt {attempt} failed: {error}");
            }
        }
        return false;
    }

    //Url 清单里的相对路径拼成下载地址 逐段转义免得空格之类破坏地址
    private static string Url(string relative)
        => BaseUrl + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

    //ResolveCachePath 把相对路径落到缓存目录内 越界返回 null
    //清单来自网络 路径里带 .. 时不挡住就会写到缓存目录外面去
    private static string? ResolveCachePath(string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Root, normalized));
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
