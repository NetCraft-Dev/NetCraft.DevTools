namespace NetCraft.ModBuild.Core;

//TemplateStore 模板资产的本地缓存
//目录落在程序根目录 清单只有本地没有时才联网 示例文件同样缓存优先
public static class TemplateStore
{
    //DirectoryName 缓存目录名
    private const string DirectoryName = "Template";

    //CatalogFileName 清单文件名
    public const string CatalogFileName = "NetCraftTemplate.yaml";

    //CatalogUrl 清单的引导地址 只有本地还没有清单时才走它
    //清单里的 base 是示例文件的基准 与这里不是一回事
    private const string CatalogUrl =
        "https://raw.githubusercontent.com/XSY-HYH/NetCraftTemplate/refs/heads/main/NetCraftTemplate.yaml";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    //Refresh 本次运行是否要求拿最新 置位后下载会绕开 CDN 那份陈旧副本
    //静态是因为一次只跑一条命令 值在进工具时定下就不再变
    public static bool Refresh { get; set; }

    //Root 缓存目录 程序根目录下的 Template
    public static string Root => Path.Combine(AppContext.BaseDirectory, DirectoryName);

    //CatalogPath 清单的本地路径
    public static string CatalogPath => Path.Combine(Root, CatalogFileName);

    //LoadCatalog 读清单 本地没有先建目录拉一份
    public static TemplateCatalog? LoadCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            Console.WriteLine($"Template cache is empty, downloading {CatalogFileName}");
            if (!TryDownload(CatalogUrl, CatalogPath, out var error))
            {
                Console.WriteLine($"Failed to download {CatalogUrl}: {error}");
                return null;
            }
        }
        else
        {
            Trace.Log($"清单缓存命中 {CatalogPath}");
        }

        return TemplateCatalog.Read(CatalogPath);
    }

    //FetchFile 取一份模板文件 本地有就用本地 没有才下载并写进缓存
    //返回落盘的本地路径 失败返回 null 并填 error
    public static string? FetchFile(string url, string relative, out string error)
    {
        var target = ResolveCachePath(relative);
        if (target is null)
        {
            error = $"template path escapes the cache directory: {relative}";
            return null;
        }

        if (File.Exists(target))
        {
            error = string.Empty;
            return target;
        }

        return TryDownload(url, target, out error) ? target : null;
    }

    //ResolveCachePath 把清单里的相对路径落到缓存目录内 越界返回 null
    //清单来自网络 路径里带 .. 时不挡住就会写到缓存目录外面去
    private static string? ResolveCachePath(string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Root, normalized));
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    //TryDownload 下载到指定路径 先落临时文件再整体挪过去 半截内容不会留在缓存里
    private static bool TryDownload(string url, string path, out string error)
    {
        try
        {
            var bytes = Http.GetByteArrayAsync(Refresh ? FreshUrl(url) : url).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    //FreshUrl 要求拿最新时换一条能绕开 CDN 陈旧缓存的地址
    //raw.githubusercontent.com 那份缓存既不认 no-cache 也不认查询参数 刚推上去的内容要等它自己过期
    //走 github.com 的 raw 路径会被 302 到带临时 token 的地址 那个地址只在这一次有效 必然是新的
    //不是 GitHub raw 的地址原样返回 那份缓存不归我们管
    private static string FreshUrl(string url)
    {
        const string host = "https://raw.githubusercontent.com/";
        if (!url.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            return url;

        var segments = url[host.Length..].Split('/');
        if (segments.Length < 3)
            return url;

        var tail = string.Join('/', segments[2..]);
        const string heads = "refs/heads/";
        if (tail.StartsWith(heads, StringComparison.Ordinal))
            tail = tail[heads.Length..];

        return $"https://github.com/{segments[0]}/{segments[1]}/raw/{tail}";
    }
}
