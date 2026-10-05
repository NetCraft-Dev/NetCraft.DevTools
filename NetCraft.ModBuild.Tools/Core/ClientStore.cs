using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    //Root 缓存目录 程序根目录下的 Client
    public static string Root => Path.Combine(AppContext.BaseDirectory, DirectoryName);

    //Ensure 保证内核版本那份客户端 jar 在缓存里 返回它的路径 办不到返回 null
    //已有且看着完整就不再联网 每次开服都重下一遍太亏
    public static string? Ensure()
    {
        var version = KernelVersion();
        if (version is null)
        {
            Console.WriteLine("error: cannot read the kernel version from NetCraft.Config");
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

    //Fetch 按版本名把 jar 拉下来 落盘前对数与哈希都对一遍
    private static bool Fetch(string version, string target)
    {
        if (FindClient(version) is not { } client)
            return false;

        var bytes = TryGet(client.Url, out var error);
        if (bytes is null)
        {
            Console.WriteLine($"error: failed to download {client.Url}: {error}");
            return false;
        }

        if (client.Size > 0 && bytes.LongLength != client.Size)
        {
            Console.WriteLine($"error: client jar size mismatch, expected {client.Size} got {bytes.LongLength}");
            return false;
        }

        var actual = Convert.ToHexStringLower(SHA1.HashData(bytes));
        if (client.Sha1.Length > 0 && !string.Equals(actual, client.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: client jar sha1 mismatch, expected {client.Sha1} got {actual}");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
        Trace.Log($"client jar saved {target}");
        return true;
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

    //TryGet 下载一份文件 先走镜像再走原地址 每个地址各有几次重试
    private static byte[]? TryGet(string url, out string error)
    {
        error = string.Empty;
        foreach (var candidate in new[] { MirrorUrl(url), url }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    return Http.GetByteArrayAsync(candidate).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    error = $"{e.GetType().Name}: {e.Message}";
                    Trace.Log($"download {candidate} attempt {attempt} failed: {error}");
                }
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
