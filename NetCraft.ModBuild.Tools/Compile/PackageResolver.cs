using NetCraft.ModBuild.Core;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NetCraft.ModBuild.Compile;

//PackageResolver 按配置里的包声明解析依赖并还原到目录
//复用全局包缓存 缓存里没有才联网 版本按传递依赖与版本区间一起定
//落点固定 编译与打包都直接扫落点 不另记清单
public static class PackageResolver
{
    //Root ncm 自己那份包缓存 全局缓存里没命中的包下到这里 下次不必再下
    private static string Root => Path.Combine(AppContext.BaseDirectory, "packages");

    //CacheRoot ncm 自己那份包缓存 清理时要用
    public static string CacheRoot => Root;

    //Source 打包源
    private const string Source = "https://api.nuget.org/v3/index.json";

    //Rounds 依赖图迭代上限 版本之间互相拉扯时总得有个停的地方
    private const int Rounds = 16;

    private static readonly ILogger Logger = NullLogger.Instance;

    //Restore 解析并还原 返回是否成功
    //target 是还原目录 每个包在它下面占一个以包 id 命名的子目录
    public static bool Restore(NcProject project, string target, out string error)
    {
        error = string.Empty;
        if (project.Packages.Count == 0)
        {
            Console.WriteLine("No <Package> declared in the project config, nothing to restore");
            return true;
        }

        try
        {
            var (ok, failure) = RestoreAsync(project, target).GetAwaiter().GetResult();
            error = failure;
            return ok;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Trace.Log($"restore failed: {error}");
            return false;
        }
    }

    private static async Task<(bool Ok, string Error)> RestoreAsync(NcProject project, string target)
    {
        var repository = Repository.Factory.GetCoreV3(Source);
        var cache = new SourceCacheContext();
        var finder = await repository.GetResourceAsync<FindPackageByIdResource>().ConfigureAwait(false);
        if (finder is null)
            return (false, "the package source has no finder resource");

        var packages = await ResolveAsync(project, finder, cache).ConfigureAwait(false);
        if (packages is null)
            return (false, "cannot resolve the package graph");

        var folder = GlobalPackagesFolder();

        foreach (var (id, version) in packages.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var directory = await EnsureAsync(finder, id, version, folder, cache).ConfigureAwait(false);
            if (directory is null)
                return (false, $"cannot get {id} {version.ToNormalizedString()}");

            var files = Copy(directory, id, target);
            var analyzers = CopyAnalyzers(directory, id, target);

            //包带的 props 与 targets 顺手收进项目 构建时就不再依赖包缓存
            //配置里点名的是直接依赖 看 build 其余是传递依赖 看 buildTransitive
            var direct = project.Packages.Any(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            var collected = PackageTargets.Collect(project.Directory, directory, id, direct);

            Console.WriteLine($"Restored {id} {version.ToNormalizedString()} {Summarize(files.Count, analyzers, collected)}");
        }

        return (true, string.Empty);
    }

    //Summarize 一个包落下了什么
    private static string Summarize(int assemblies, int analyzers, int targets)
    {
        var parts = new List<string> { $"{assemblies} assembly(ies)" };
        if (analyzers > 0)
            parts.Add($"{analyzers} analyzer(s)");
        if (targets > 0)
            parts.Add($"{targets} build file(s)");

        return "(" + string.Join(", ", parts) + ")";
    }

    //ResolveAsync 把声明的包连传递依赖一起定下版本
    //同一个包被要求过多个版本区间时就取能同时满足它们的最高版本 反复几轮直到不再变
    private static async Task<Dictionary<string, NuGetVersion>?> ResolveAsync(NcProject project,
        FindPackageByIdResource finder, SourceCacheContext cache)
    {
        var constraints = new Dictionary<string, List<VersionRange>>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in project.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.Version))
            {
                Constrain(constraints, package.Id, VersionRange.All);
                continue;
            }

            if (!VersionRange.TryParse(package.Version, out var range))
            {
                Console.WriteLine($"error: {package.Id} has an invalid version range \"{package.Version}\"");
                return null;
            }

            Constrain(constraints, package.Id, range);
        }

        var chosen = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var expanded = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);

        for (var round = 0; round < Rounds; round++)
        {
            var changed = false;
            foreach (var (id, ranges) in constraints.ToList())
            {
                var versions = await finder.GetAllVersionsAsync(id, cache, Logger, default).ConfigureAwait(false) ?? [];
                var candidates = versions.Where(candidate => ranges.All(range => range.Satisfies(candidate))).ToList();

                //没写版本的取最新 写了就取满足区间的最低版本
                //这与 NuGet 一致 声明 13.0.3 认作 [13.0.3,) 落下来的正是 13.0.3 不会跳到更高的大版本
                var version = ranges.All(range => range.Equals(VersionRange.All))
                    ? candidates.Max()
                    : candidates.Min();

                if (version is null)
                {
                    Console.WriteLine($"error: no version of {id} satisfies the requested range");
                    return null;
                }

                chosen[id] = version;

                //同一版本依赖只需展开一次 展开过就跳过 否则会一直绕
                if (expanded.TryGetValue(id, out var done) && done == version)
                    continue;

                expanded[id] = version;
                changed = true;

                var info = await finder.GetDependencyInfoAsync(id, version, cache, Logger, default).ConfigureAwait(false);
                foreach (var dependency in Dependencies(info, TargetFramework.NuGet))
                    Constrain(constraints, dependency.Id, dependency.VersionRange);
            }

            if (!changed)
                break;
        }

        return chosen;
    }

    //Dependencies 挑出与目标框架最贴合的那组依赖
    private static IEnumerable<PackageDependency> Dependencies(FindPackageByIdDependencyInfo info,
        NuGetFramework framework)
    {
        var groups = info.DependencyGroups.ToList();
        if (groups.Count == 0)
            yield break;

        var reducer = new FrameworkReducer();
        var nearest = reducer.GetNearest(framework, groups.Select(group => group.TargetFramework));
        if (nearest is null)
            yield break;

        var group = groups.FirstOrDefault(item => item.TargetFramework.Equals(nearest));
        if (group is null)
            yield break;

        foreach (var dependency in group.Packages)
            yield return dependency;
    }

    //Constrain 给某个包再加一条版本约束
    private static void Constrain(Dictionary<string, List<VersionRange>> constraints, string id, VersionRange range)
    {
        if (!constraints.TryGetValue(id, out var ranges))
        {
            ranges = new List<VersionRange>();
            constraints[id] = ranges;
        }

        if (!ranges.Any(existing => existing.Equals(range)))
            ranges.Add(range);
    }

    //EnsureAsync 弄到一份能读到程序集的包目录
    //全局缓存里有就直接用 平时 dotnet build 攒下的包大多能命中 没有才下到 ncm 自己那份缓存
    private static async Task<string?> EnsureAsync(FindPackageByIdResource finder, string id, NuGetVersion version,
        string globalFolder, SourceCacheContext cache)
    {
        var cached = Path.Combine(globalFolder, id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant());
        if (HasAssemblies(cached))
        {
            Trace.Log($"package cache hit {cached}");
            return cached;
        }

        var directory = Path.Combine(Root, id, version.ToNormalizedString());
        if (HasAssemblies(directory))
            return directory;

        if (await DownloadAsync(finder, id, version, directory, cache).ConfigureAwait(false))
            return directory;

        Console.WriteLine($"error: cannot fetch {id} {version.ToNormalizedString()}");
        return null;
    }

    //HasAssemblies 目录里有没有可用的程序集
    private static bool HasAssemblies(string directory)
        => Directory.Exists(Path.Combine(directory, "lib")) || Directory.Exists(Path.Combine(directory, "ref"));

    //DownloadAsync 下一个包并把它解出来
    private static async Task<bool> DownloadAsync(FindPackageByIdResource finder, string id, NuGetVersion version,
        string directory, SourceCacheContext cache)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"{id}.{version.ToNormalizedString()}.{Guid.NewGuid():N}.nupkg");
        try
        {
            using (var stream = File.Create(temporary))
            {
                if (!await finder.CopyNupkgToStreamAsync(id, version, stream, cache, Logger, default).ConfigureAwait(false))
                    return false;
            }

            Extract(temporary, directory);
            return true;
        }
        catch (Exception e)
        {
            Trace.Log($"cannot download {id} {version}: {e.GetType().Name}");
            return false;
        }
        finally
        {
            Discard(temporary);
        }
    }

    //Extract 把包里 lib 与 ref 下的程序集按原结构解到目标目录
    //只留这两处 其余内容模组用不上
    private static void Extract(string archive, string directory)
    {
        using var reader = new PackageArchiveReader(archive);
        foreach (var entry in reader.GetFiles())
        {
            if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!entry.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                && !entry.StartsWith("ref/", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var source = reader.GetStream(entry);
            using var destination = File.Create(target);
            source.CopyTo(destination);
        }
    }

    //Discard 临时包文件没用了就删掉
    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            //删不掉不影响结果
        }
    }

    //Copy 把包里的程序集按包名收进还原目录 返回落下的文件完整路径
    private static List<string> Copy(string packageDirectory, string id, string target)
    {
        var source = Choose(packageDirectory);
        if (source.Count == 0)
        {
            Trace.Log($"no assembly for {TargetFramework.Name} in {packageDirectory}");
            return [];
        }

        var destination = Path.Combine(target, id);
        Directory.CreateDirectory(destination);

        var files = new List<string>();
        foreach (var path in source)
        {
            var copy = Path.Combine(destination, Path.GetFileName(path));
            try
            {
                File.Copy(path, copy, overwrite: true);
                files.Add(copy);
            }
            catch (IOException e)
            {
                Trace.Log($"cannot copy {path}: {e.Message}");
            }
        }

        return files;
    }

    //Choose 包目录里该拿哪一份程序集
    //lib/<框架> 下挑离目标框架最近的那个 包只带引用程序集时退回 ref/<框架>
    private static List<string> Choose(string packageDirectory)
    {
        foreach (var kind in new[] { "lib", "ref" })
        {
            var directory = Path.Combine(packageDirectory, kind);
            if (!Directory.Exists(directory))
                continue;

            var candidates = Directory.GetDirectories(directory)
                .Select(path => (Path: path, Framework: NuGetFramework.Parse(Path.GetFileName(path))))
                .Where(item => !item.Framework.IsUnsupported)
                .ToList();

            if (candidates.Count == 0)
                continue;

            var reducer = new FrameworkReducer();
            var nearest = reducer.GetNearest(TargetFramework.NuGet, candidates.Select(item => item.Framework));
            if (nearest is null)
                continue;

            var chosen = candidates.FirstOrDefault(item => item.Framework.Equals(nearest));
            if (chosen.Path is null)
                continue;

            var files = Directory.EnumerateFiles(chosen.Path, "*.dll").ToList();
            if (files.Count > 0)
                return files;
        }

        return [];
    }

    //GlobalPackagesFolder 全局包缓存目录
    private static string GlobalPackagesFolder()
    {
        var settings = Settings.LoadDefaultSettings(null);
        var folder = SettingsUtility.GetGlobalPackagesFolder(settings);
        return string.IsNullOrEmpty(folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
            : folder;
    }

    //CopyAnalyzers 把包带的生成器与分析器收进还原目录
    //它们只在编译期用 但一起收进来项目就齐了 编译不必再回头翻包缓存
    private static int CopyAnalyzers(string packageDirectory, string id, string target)
    {
        var source = Path.Combine(packageDirectory, "analyzers");
        if (!Directory.Exists(source))
            return 0;

        var destination = Path.Combine(target, id, "analyzers");
        var copied = 0;
        foreach (var path in Directory.EnumerateFiles(source, "*.dll", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(destination, Path.GetRelativePath(source, path));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(path, copy, overwrite: true);
                copied++;
            }
            catch (IOException e)
            {
                Trace.Log($"cannot copy {path}: {e.Message}");
            }
        }

        return copied;
    }

    //Assemblies 还原目录里各包的程序集
    //一个包占一层 只扫顶层 analyzers 那类子目录不该当引用带进来
    public static IReadOnlyList<string> Assemblies(string root)
    {
        var directory = Path.Combine(root, ProjectLayout.Packages);
        if (!Directory.Exists(directory))
            return [];

        var paths = new List<string>();
        foreach (var package in Directory.EnumerateDirectories(directory))
            paths.AddRange(Directory.EnumerateFiles(package, "*.dll", SearchOption.TopDirectoryOnly));
        return paths;
    }

    //Analyzers 各包带的生成器与分析器程序集
    //界面库的 x:Name 字段与 InitializeComponent 都是它们现产的 少了整片源码都报找不到名字
    public static IReadOnlyList<string> Analyzers(string root)
    {
        var directory = Path.Combine(root, ProjectLayout.Packages);
        if (!Directory.Exists(directory))
            return [];

        var paths = new List<string>();
        foreach (var package in Directory.EnumerateDirectories(directory))
        {
            var analyzers = Path.Combine(package, "analyzers");
            if (Directory.Exists(analyzers))
                paths.AddRange(Directory.EnumerateFiles(analyzers, "*.dll", SearchOption.AllDirectories));
        }
        return paths;
    }
}
