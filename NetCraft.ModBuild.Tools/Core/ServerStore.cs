using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace NetCraft.ModBuild.Core;

//One entry from the index
//An empty hash means the index carries only paths, in which case the file's presence is all that matters
internal readonly record struct ServerFile(string Path, string Hash);

//Local cache of server runtime files
//It lives under the user cache directory and only fetches what is missing, retrying a few times per file
//Only Refresh compares hashes and re-downloads files whose content differs
//Both the cache location and the source can be overridden by the project config, see Configure
public static class ServerStore
{
    //Default index file name with one entry per line and two spaces between hash and path
    private const string DefaultIndexFileName = "index.txt";

    //Default download base for the index and the files it lists
    private const string DefaultBaseUrl =
        "https://raw.githubusercontent.com/NetCraft-Dev/NetCraft.Release/refs/heads/main/server/";

    //Retry limit per file
    private const int Attempts = 5;

    //Separator between hash and path in the index, two spaces to match sha256sum output
    private const string HashSeparator = "  ";

    private static string Source { get; set; } = DefaultBaseUrl;

    private static string Format { get; set; } = NcSource.DefaultFormat;

    private static Regex? Matcher { get; set; }

    //Local kernel directory from the project config, taken in place of the cache, null when unset
    private static string? Local { get; set; }

    //Whether the kernel is read from a local directory rather than the cache
    public static bool UsesLocalDirectory => Local is not null;

    //Directory ncm downloads into, overridden by the Server section of the project config when present
    //A local kernel directory does not move this, since that directory is not ncm's to fill or remove
    public static string CacheRoot { get; private set; } = CacheLayout.Server;

    //Directory the kernel is read from, which is the local directory when one is configured
    public static string Root => Local ?? CacheRoot;

    public static string IndexFileName { get; private set; } = DefaultIndexFileName;

    public static string IndexPath => Path.Combine(CacheRoot, IndexFileName);

    //Adjust the cache location and source from the project config, keeping defaults for anything unset
    //Cache resolves against the project root, and the source swaps address and rules together, accepting only the Formats list
    public static void Configure(NcProject? project)
    {
        if (project is null)
            return;

        Local = null;
        if (!string.IsNullOrWhiteSpace(project.Server.Cache))
            CacheRoot = Path.GetFullPath(Path.Combine(project.Directory, project.Server.Cache));

        var source = project.Sources;
        if (source is null)
            return;

        //A local directory stands in for the cache and nothing is downloaded into it
        //A project that names one which is not there falls through to the url, which is the fallback
        if (!string.IsNullOrWhiteSpace(source.Directory))
        {
            var local = Path.GetFullPath(Path.Combine(project.Directory, source.Directory));
            if (Directory.Exists(local))
            {
                Local = local;
                return;
            }

            Console.WriteLine($"warning: <Sources Directory=\"{source.Directory}\"> does not exist, the source is used instead");
        }

        //A source with no address is the local directory setting alone, leaving the download defaults as they are
        if (string.IsNullOrWhiteSpace(source.Url))
            return;

        Source = source.Url.EndsWith('/') ? source.Url : source.Url + "/";
        IndexFileName = source.Index;
        Format = source.Format;
        Matcher = BuildMatcher(source);
    }

    //Regex for the regex format, treated as unconfigured and falling back to the default rules when it fails to compile
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

    //Make sure the cached files are complete and return whether the cache is usable
    //Only the local index is read, leaving the remote check to Refresh
    public static bool Ensure()
    {
        //A local directory is the kernel itself, with nothing to fill in
        if (Local is not null)
        {
            Trace.Log($"server kernel taken from local directory {Local}");
            return true;
        }

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

    //Fetch the remote index over the local one and fill in files that differ by hash, returning whether the cache is usable
    //Unlike Ensure the index comes from the remote and files whose local content differs are re-downloaded
    public static bool Refresh()
    {
        //The local directory is never downloaded into, so there is nothing to compare against an index
        if (Local is not null)
        {
            Console.WriteLine($"Kernel comes from the local directory {Local}, nothing to refresh");
            return true;
        }

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

    //Download every entry, returning false as soon as one fails
    //Progress counts files because sizes vary too much for a byte count to show how many remain
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

    //Download every entry and stop at the first failure
    //completed fires once per finished file with its name, or null when no progress is drawn
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

    //Whether this index entry should be downloaded
    //The index covers all platforms and a native library built for another platform could not be loaded here
    //Entries under runtimes are split by RID and TraceEvent native components by architecture, both needing a pick, while other files are platform independent
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

    //Whether a platform directory name matches this machine
    //Names like win-x64 match directly, while system-only names like osx and distro variants like linux-musl-x64 are matched by system plus architecture
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

    //Whether the sha256 of a local file matches the index entry
    //A missing hash counts as a match while an unreadable file counts as a mismatch
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

    //Read the index, returning an empty list when it is absent or unreadable
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

    //Split one index line according to the configured format
    //The regex format expects the two capture groups, hash and relative path, and skips a line that does not match
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

        //The plain format gives only a path and treats the hash as absent, so presence alone decides
        if (string.Equals(Format, "plain", StringComparison.OrdinalIgnoreCase))
            return new ServerFile(Normalize(text), string.Empty);

        //The sha256-lines format mirrors sha256sum output, treating a line without a recognizable hash as a path
        var separator = text.IndexOf(HashSeparator, StringComparison.Ordinal);
        if (separator > 0 && IsHex(text.AsSpan(0, separator)))
            return new ServerFile(Normalize(text[(separator + HashSeparator.Length)..]), text[..separator]);

        return new ServerFile(Normalize(text), string.Empty);
    }

    //Normalize index paths to slash separators
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

    //Download one file, retrying up to the limit
    //The file lands in a temporary path first so a partial download never stays in the cache
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

    //Build the download address from a relative index path, escaping each segment so spaces cannot break the URL
    private static string Url(string relative)
        => Source + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

    //Resolve a relative path inside the cache directory, returning null when it escapes
    //The index comes from the network so a .. in a path would otherwise write outside the cache directory
    private static string? ResolveCachePath(string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(CacheRoot, normalized));
        var root = Path.GetFullPath(CacheRoot) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
