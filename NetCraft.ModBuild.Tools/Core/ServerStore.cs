using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace NetCraft.ModBuild.Core;

//ServerFile 清单里的一条
//Hash 为空表示这份清单只有路径 那种情况下只按文件在不在判断
internal readonly record struct ServerFile(string Path, string Hash);

//ServerStore 服务端运行时文件的本地缓存
//目录落在程序根目录 本地齐备就不联网 缺哪个补哪个 每个文件给几次重试
//只有 Refresh 那条路会按哈希比对 它会让内容对不上的文件重下
//缓存位置与来源都能被项目配置改写 见 Configure
public static class ServerStore
{
    //DirectoryName 默认缓存目录名
    private const string DirectoryName = "Server";

    //DefaultIndexFileName 默认清单文件名 一行一条 哈希与相对路径之间两个空格
    private const string DefaultIndexFileName = "index.txt";

    //DefaultBaseUrl 默认清单与各文件的下载基准
    private const string DefaultBaseUrl =
        "https://raw.githubusercontent.com/NetCraft-Dev/NetCraft.Release/refs/heads/main/server/";

    //Attempts 单个文件最多试几次
    private const int Attempts = 5;

    //HashSeparator 清单里哈希与路径的分隔 两个空格 与 sha256sum 的输出一致
    private const string HashSeparator = "  ";

    private static string Source { get; set; } = DefaultBaseUrl;

    private static string Format { get; set; } = NcSource.DefaultFormat;

    private static Regex? Matcher { get; set; }

    //Root 缓存目录 程序根目录下的 Server 配置里指定了就按配置来
    public static string Root { get; private set; } = Path.Combine(AppContext.BaseDirectory, DirectoryName);

    //IndexFileName 清单文件名
    public static string IndexFileName { get; private set; } = DefaultIndexFileName;

    //IndexPath 清单的本地路径
    public static string IndexPath => Path.Combine(Root, IndexFileName);

    //Configure 按项目配置调整缓存位置与来源 没配的项一律保持默认
    //Cache 相对项目根解析 来源那块地址与规则一起换 只认 Formats 里的几种
    public static void Configure(NcProject? project)
    {
        if (project is null)
            return;

        if (!string.IsNullOrWhiteSpace(project.Server.Cache))
            Root = Path.GetFullPath(Path.Combine(project.Directory, project.Server.Cache));

        var source = project.Sources;
        if (source is null)
            return;

        Source = source.Url.EndsWith('/') ? source.Url : source.Url + "/";
        IndexFileName = source.Index;
        Format = source.Format;
        Matcher = BuildMatcher(source);
    }

    //BuildMatcher regex 格式的正则 编译不出来就当没配 退回默认规则
    private static Regex? BuildMatcher(NcSource source)
    {
        if (!string.Equals(source.Format, "regex", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return new Regex(source.Pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException e)
        {
            Console.WriteLine($"error: Source Pattern \"{source.Pattern}\" does not compile: {e.Message}");
            return null;
        }
    }

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
                Console.WriteLine($"error: failed to download {Source}{IndexFileName}: {indexError}");
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
        Console.WriteLine($"Refreshing server cache from {Source}{IndexFileName}");
        if (!TryDownload(IndexFileName, out var error))
        {
            Console.WriteLine($"error: failed to download {Source}{IndexFileName}: {error}");
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
    //进度按文件计数 各份大小差得多 按字节算反倒看不出还剩几份
    private static bool Download(List<ServerFile> files)
    {
        if (!Transfer.Enabled)
        {
            if (DownloadCore(files, null, out var failure))
                return true;

            Console.WriteLine($"error: {failure}");
            return false;
        }

        var ok = false;
        var error = string.Empty;
        AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn())
            .Start(context =>
            {
                var task = context.AddTask("downloading server files", maxValue: files.Count);
                ok = DownloadCore(files, name =>
                {
                    task.Description = $"[green]{Markup.Escape(name)}[/]";
                    task.Increment(1);
                }, out error);
            });

        if (ok)
            return true;

        Console.WriteLine($"error: {error}");
        return false;
    }

    //DownloadCore 逐条下载 有一条失败就停下
    //completed 每备好一份回调一次 给的是文件名 不画进度时传 null
    private static bool DownloadCore(List<ServerFile> files, Action<string>? completed, out string error)
    {
        error = string.Empty;
        for (var index = 0; index < files.Count; index++)
        {
            var entry = files[index];
            if (TryDownload(entry.Path, out var failure))
            {
                Trace.Log($"server file {index + 1}/{files.Count} done {entry.Path}");
                completed?.Invoke(Path.GetFileName(entry.Path));
                continue;
            }

            error = $"failed to download {entry.Path} after {Attempts} attempt(s): {failure}";
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

    //ParseEntry 按配置的格式拆开清单里的一条
    //regex 认 Pattern 里两个捕获组 依次是哈希与相对路径 对不上就跳过这一条
    private static ServerFile ParseEntry(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
            return new ServerFile(string.Empty, string.Empty);

        if (string.Equals(Format, "regex", StringComparison.OrdinalIgnoreCase) && Matcher is not null)
        {
            var match = Matcher.Match(text);
            return match.Success && match.Groups.Count >= 3
                ? new ServerFile(Normalize(match.Groups[2].Value), match.Groups[1].Value)
                : new ServerFile(string.Empty, string.Empty);
        }

        //plain 一行就只有路径 哈希一律当没有 那样只按文件在不在判断
        if (string.Equals(Format, "plain", StringComparison.OrdinalIgnoreCase))
            return new ServerFile(Normalize(text), string.Empty);

        //sha256-lines 是 sha256sum 的输出 认不出哈希时整行当路径
        var separator = text.IndexOf(HashSeparator, StringComparison.Ordinal);
        if (separator > 0 && IsHex(text.AsSpan(0, separator)))
            return new ServerFile(Normalize(text[(separator + HashSeparator.Length)..]), text[..separator]);

        return new ServerFile(Normalize(text), string.Empty);
    }

    //Normalize 清单里的路径统一成斜杠分隔
    private static string Normalize(string path)
        => path.Trim().Replace('\\', '/');

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
            if (Transfer.DownloadToFile(Url(relative), target, static (_, _) => { }, out error))
                return true;

            Trace.Log($"download {relative} attempt {attempt} failed: {error}");
        }
        return false;
    }

    //Url 清单里的相对路径拼成下载地址 逐段转义免得空格之类破坏地址
    private static string Url(string relative)
        => Source + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

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
