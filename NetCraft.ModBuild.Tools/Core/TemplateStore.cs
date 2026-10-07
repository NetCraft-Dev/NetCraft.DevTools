namespace NetCraft.ModBuild.Core;

//Local cache of template assets under the user cache; the catalog only hits the network when missing locally and example files are cache-first too
public static class TemplateStore
{
    public const string CatalogFileName = "NetCraftTemplate.yaml";

    //Built-in catalog bootstrap URL used only when no local catalog exists
    private const string DefaultCatalogUrl =
        "https://raw.githubusercontent.com/NetCraft-Dev/NetCraftTemplate/refs/heads/main/NetCraftTemplate.yaml";

    //Catalog URL in use, which project config can override; the base inside the catalog is the example baseline and is a separate thing
    private static string CatalogUrl { get; set; } = DefaultCatalogUrl;

    //Override for the example baseline that replaces the catalog's base when set
    private static string BaseOverride { get; set; } = string.Empty;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    //Whether this run wants the latest; setting it bypasses stale CDN copies, and the static value is fixed when the tool starts since one command runs per process
    public static bool Refresh { get; set; }

    //Cache directory under the user folder, unaffected by ncm version changes
    public static string Root => CacheLayout.Template;

    public static string CatalogPath => Path.Combine(Root, CatalogFileName);

    //Adjust sources from project config, keeping defaults for anything unset; mirror prefixes are prepended verbatim and ncm adds no processing
    public static void Configure(NcProject? project)
    {
        var template = project?.Template;
        if (template is null)
            return;

        if (!string.IsNullOrWhiteSpace(template.Url))
            CatalogUrl = template.Url;

        BaseOverride = template.Base;
    }

    //Read the catalog, downloading one first when the local copy is absent
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
            Trace.Log($"catalog cache hit {CatalogPath}");
        }

        var catalog = TemplateCatalog.Read(CatalogPath);
        if (catalog is not null && !string.IsNullOrWhiteSpace(BaseOverride))
            catalog.Base = BaseOverride;

        return catalog;
    }

    //Fetch a template file preferring the local copy; returns the on-disk path, or null with error set on failure
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

    //Map a catalog-relative path inside the cache, returning null when it escapes; the catalog comes from the network so .. must be blocked to keep writes contained
    private static string? ResolveCachePath(string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Root, normalized));
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    //Download to a path through a temp file moved in place so partial content never stays in the cache
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

    //Swap in a URL that bypasses the stale CDN copy when the latest is required
    //raw.githubusercontent.com honors neither no-cache nor query parameters, while the github.com raw path redirects to a temporary-token URL that is always fresh
    //Non-GitHub raw URLs pass through untouched since their caching is not ours to manage
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
