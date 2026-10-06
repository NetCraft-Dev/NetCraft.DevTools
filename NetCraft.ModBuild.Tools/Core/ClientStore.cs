using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Spectre.Console;

namespace NetCraft.ModBuild.Core;

//ClientJarFile 版本 json 里客户端那一段
internal readonly record struct ClientJarFile(string Url, long Size, string Sha1);

//ClientStore 原版客户端 jar 的本地缓存
//版本号从内核的 SharedConstants 里读 下到的 jar 留在程序根目录的 Client/<版本>/ 下
//首次开服要靠它提 assets 之后的启动就不必再给了
public static class ClientStore
{
    //DirectoryName 缓存目录名
    private const string DirectoryName = "Client";

    //Mirror 镜像站 官方几个域名都换到它上面 国内直连官方太慢
    private const string Mirror = "https://bmclapi2.bangbang93.com";

    //MirrorManifestUrl 版本清单的镜像地址
    private const string MirrorManifestUrl = Mirror + "/mc/game/version_manifest.json";

    //OfficialManifestUrl 版本清单的官方地址 镜像不通时回退
    private const string OfficialManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest.json";

    //Attempts 单个地址最多试几次
    private const int Attempts = 3;

    //MinJarSize 小于这个字节数的一律当没下完整
    private const long MinJarSize = 1024L;

    //Mirrors 官方域名到镜像的对应 版本 json 与 jar 的地址都从这里面出
    private static readonly (string From, string To)[] Mirrors =
    [
        ("https://piston-meta.mojang.com", Mirror),
        ("https://piston-data.mojang.com", Mirror),
        ("https://launcher.mojang.com", Mirror),
        ("https://launchermeta.mojang.com", Mirror),
    ];

    //Root 缓存目录 程序根目录下的 Client
    public static string Root => Path.Combine(AppContext.BaseDirectory, DirectoryName);

    //ConfiguredVersion 配置里指定的 jar 版本 空表示照内核版本走
    private static string ConfiguredVersion { get; set; } = string.Empty;

    //ConfiguredJar 配置里给的直链 给了就不再查版本清单
    private static string ConfiguredJar { get; set; } = string.Empty;

    //Configure 按项目配置调整版本与直链 没配的项一律保持默认
    public static void Configure(NcProject? project)
    {
        if (project is null)
            return;

        ConfiguredVersion = project.Client.Version;
        ConfiguredJar = project.Client.Jar;
    }

    //Ensure 保证这台机器要的那份客户端 jar 在缓存里 返回它的路径 办不到返回 null
    //已有且看着完整就不再联网 每次开服都重下一遍太亏
    public static string? Ensure()
    {
        var version = Version();
        if (version is null)
        {
            Console.WriteLine("error: cannot read the kernel version from NetCraft.Config, pin one with <Client Version=\"...\">");
            return null;
        }

        var target = Path.Combine(Root, version, version + ".jar");
        var existing = new FileInfo(target);
        if (existing.Exists && existing.Length >= MinJarSize)
        {
            Trace.Log($"client jar cache hit {target}");
            return target;
        }

        Console.WriteLine($"Downloading Minecraft client jar {version} to {Path.GetDirectoryName(target)}");
        return Fetch(version, target) ? target : null;
    }

    //Version 用哪个版本 配置里指定了就以它为准 没指定才读内核里那个
    private static string? Version()
        => string.IsNullOrWhiteSpace(ConfiguredVersion) ? KernelVersion() : ConfiguredVersion;

    //KernelVersion 内核里的版本字符串 横杠前面那段就是 Minecraft 的版本号
    //读的是编译期常量 只借用元数据 用完把加载上下文卸掉
    private static string? KernelVersion()
    {
        var path = Path.Combine(ServerStore.Root, "NetCraft.Config.dll");
        if (!File.Exists(path))
        {
            Trace.Log($"kernel config assembly not found at {path}");
            return null;
        }

        try
        {
            var context = new AssemblyLoadContext("ncm-kernel-version", isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(path);
                var value = assembly.GetType("NetCraft.Config.SharedConstants")
                    ?.GetField("Version")?.GetRawConstantValue() as string;
                if (string.IsNullOrEmpty(value))
                    return null;

                var separator = value.IndexOf('-');
                return separator < 0 ? value : value[..separator];
            }
            finally
            {
                context.Unload();
            }
        }
        catch (Exception e)
        {
            Trace.Log($"cannot read the kernel version: {e.GetType().Name}");
            return null;
        }
    }

    //Fetch 把 jar 拉下来 配置给了直链就直接下 否则先查版本清单再下
    //落盘前把能校验的都过一遍 大小与哈希只有清单那条路才有
    private static bool Fetch(string version, string target)
    {
        ClientJarFile? entry = null;
        string url;
        if (string.IsNullOrWhiteSpace(ConfiguredJar))
        {
            if (FindClient(version) is not { } client)
                return false;

            entry = client;
            url = client.Url;
        }
        else
        {
            url = ConfiguredJar;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";

        string error;
        var ok = Transfer.Enabled
            ? DownloadWithProgress(url, temp, version, out error)
            : TryDownload(url, temp, static (_, _) => { }, out error);
        if (!ok)
        {
            Console.WriteLine($"error: failed to download {url}: {error}");
            return false;
        }

        var info = new FileInfo(temp);
        var size = entry?.Size ?? 0L;
        if (size > 0 && info.Length != size)
        {
            Console.WriteLine($"error: client jar size mismatch, expected {size} got {info.Length}");
            Discard(temp);
            return false;
        }

        var sha1 = entry?.Sha1 ?? string.Empty;
        var actual = Sha1(temp);
        if (sha1.Length > 0 && !string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: client jar sha1 mismatch, expected {sha1} got {actual}");
            Discard(temp);
            return false;
        }

        //直链那边没有清单可依 至少把明显没下全的挡下来
        if (info.Length < MinJarSize)
        {
            Console.WriteLine($"error: the downloaded jar is only {info.Length} byte(s), treating it as incomplete");
            Discard(temp);
            return false;
        }

        File.Move(temp, target, overwrite: true);
        Trace.Log($"client jar saved {target}");
        return true;
    }

    //DownloadWithProgress 边下边画一条字节进度
    //服务端不给总长时退成滚动条 只在描述里报已下字节
    private static bool DownloadWithProgress(string url, string target, string label, out string error)
    {
        var ok = false;
        var failure = string.Empty;
        AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new TransferSpeedColumn())
            .Start(context =>
            {
                var task = context.AddTask($"[green]{Markup.Escape(label)}[/]");
                ok = TryDownload(url, target, (received, total) =>
                {
                    if (total is { } length and > 0)
                    {
                        task.MaxValue = length;
                        task.Value = received;
                        return;
                    }

                    task.IsIndeterminate = true;
                    task.Description = $"[green]{Markup.Escape(label)}[/] {Human(received)}";
                }, out failure);
            });

        error = failure;
        return ok;
    }

    //TryDownload 流式下载 先走镜像再走原地址 每个地址各有几次重试
    private static bool TryDownload(string url, string target, Action<long, long?> report, out string error)
    {
        error = string.Empty;
        foreach (var candidate in new[] { MirrorUrl(url), url }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (Transfer.DownloadToFile(candidate, target, report, out error))
                    return true;

                Trace.Log($"download {candidate} attempt {attempt} failed: {error}");
            }
        }
        return false;
    }

    //Sha1 算文件的 sha1 十六进制小写 读不出返回空串
    private static string Sha1(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA1.HashData(stream));
        }
        catch (IOException e)
        {
            Trace.Log($"cannot hash {path}: {e.Message}");
            return string.Empty;
        }
    }

    //Human 字节数改成人读的单位 总长拿不到时进度条上用它报数
    private static string Human(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";

    //Discard 删掉没通过校验的临时文件
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

    //FindClient 版本清单里按版本名找到 json 再从 json 里取客户端那一段
    private static ClientJarFile? FindClient(string version)
    {
        var manifest = TryGet(MirrorManifestUrl, out var error) ?? TryGet(OfficialManifestUrl, out error);
        if (manifest is null)
        {
            Console.WriteLine($"error: failed to download the version manifest: {error}");
            return null;
        }

        string? jsonUrl = null;
        using (var document = JsonDocument.Parse(manifest))
        {
            if (!document.RootElement.TryGetProperty("versions", out var versions))
            {
                Console.WriteLine("error: the version manifest has no version list");
                return null;
            }

            foreach (var item in versions.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || id.GetString() != version)
                    continue;

                jsonUrl = item.TryGetProperty("url", out var url) ? url.GetString() : null;
                break;
            }
        }

        if (string.IsNullOrEmpty(jsonUrl))
        {
            Console.WriteLine($"error: version {version} is not listed in the manifest");
            return null;
        }

        var json = TryGet(jsonUrl, out var jsonError);
        if (json is null)
        {
            Console.WriteLine($"error: failed to download {jsonUrl}: {jsonError}");
            return null;
        }

        using var versionDocument = JsonDocument.Parse(json);
        if (!versionDocument.RootElement.TryGetProperty("downloads", out var downloads)
            || !downloads.TryGetProperty("client", out var client)
            || !client.TryGetProperty("url", out var clientUrl)
            || clientUrl.GetString() is not { Length: > 0 } address)
        {
            Console.WriteLine($"error: version {version} has no client download entry");
            return null;
        }

        var size = client.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt64() : 0L;
        var sha1 = client.TryGetProperty("sha1", out var sha1Value) ? sha1Value.GetString() ?? string.Empty : string.Empty;
        return new ClientJarFile(address, size, sha1);
    }

    //TryGet 下载一份小内容 先走镜像再走原地址 每个地址各有几次重试
    private static byte[]? TryGet(string url, out string error)
    {
        error = string.Empty;
        foreach (var candidate in new[] { MirrorUrl(url), url }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var bytes = Transfer.GetBytes(candidate, out error);
                if (bytes is not null)
                    return bytes;

                Trace.Log($"download {candidate} attempt {attempt} failed: {error}");
            }
        }
        return null;
    }

    //MirrorUrl 官方地址换到镜像上 本来就是镜像或别的域名就原样返回
    private static string MirrorUrl(string url)
    {
        foreach (var (from, to) in Mirrors)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + url[from.Length..];
        }
        return url;
    }
}
