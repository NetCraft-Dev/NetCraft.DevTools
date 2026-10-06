using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Tools;

//RemoveTool 从项目配置里去掉一条依赖
//去掉之后落点里那份也清掉 再回头看看源码里还有没有它的残留引用
//判据是从依赖自己的程序集里反射出来的类型 不按名字瞎猜
internal static class RemoveTool
{
    //NugetKind 包种类 从 nuget 源取
    private const string NugetKind = "nuget";

    //ModKind 包种类 从云端 mod 服务器取 还没实现
    private const string ModKind = "mod";

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("remove", "Remove a dependency from the project config", Run,
        [
            new($"{NugetKind} <id>", "Remove a nuget package"),
            new($"{ModKind} <id>", "Remove a mod dependency, not implemented yet"),
        ]);

    //Run 认包种类 从配置里删 清落点 再查残留引用
    private static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Usage();
            return 1;
        }

        var kind = args[0];
        var id = args[1];

        if (string.Equals(kind, ModKind, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: {ModKind} dependencies are not implemented yet, they will come with the cloud mod server");
            return 1;
        }

        if (!string.Equals(kind, NugetKind, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: unknown kind {kind}, expected {NugetKind} or {ModKind}");
            Usage();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            Console.WriteLine("error: a package id is required");
            return 1;
        }

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        if (project is null)
        {
            Console.WriteLine($"error: no {NcProject.Extension} in this directory or any parent");
            return 1;
        }

        //落点里那份还没清 先拿它的程序集当判据 没有就翻全局缓存
        var assemblies = Assemblies(project, id);

        if (!project.RemovePackage(id, out var error))
        {
            Console.WriteLine($"error: {error}");
            return 1;
        }

        Console.WriteLine($"Removed {id} from {project.Path}");

        //编译要靠落点里那几份引用 所以先查一遍再清
        var files = DependencyReferences.Scan(project.Directory, project, assemblies, out var scanError);
        Clear(project.Directory, id);

        if (!string.IsNullOrEmpty(scanError))
        {
            Console.WriteLine($"warning: {scanError}, the leftover reference check was skipped");
            return 0;
        }

        foreach (var file in files)
            Console.WriteLine($"warning: your code still contains a reference to \"{id}\", at {file}");

        if (files.Count > 0)
            Console.WriteLine($"{files.Count} file(s) still mention {id}, they will not compile until that is cleaned up");
        return 0;
    }

    //Assemblies 这个包的程序集都在哪 落点优先 没还原过就翻全局缓存
    private static List<string> Assemblies(NcProject project, string id)
    {
        var directory = Path.Combine(project.Directory, ProjectLayout.Packages, id);
        if (Directory.Exists(directory))
            return Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).ToList();

        var package = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages",
            id.ToLowerInvariant());
        if (!Directory.Exists(package))
            return [];

        var versions = Directory.GetDirectories(package);
        var version = project.Packages.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.Version;
        var folder = versions.FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), version, StringComparison.OrdinalIgnoreCase))
            ?? versions.FirstOrDefault();
        if (folder is null)
            return [];

        //分析器与构建任务跟代码引用不是一回事 只看 lib 与 ref 那两层
        foreach (var kind in new[] { "lib", "ref" })
        {
            var path = Path.Combine(folder, kind);
            if (Directory.Exists(path))
                return Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).ToList();
        }

        return [];
    }

    //Clear 把落点里这个包的那两份清掉
    //不清下次构建扫落点还会把它当引用带进来 移除就白做了
    private static void Clear(string root, string id)
    {
        foreach (var relative in new[] { ProjectLayout.Packages, ProjectLayout.Targets })
        {
            var directory = Path.Combine(root, relative, id);
            if (!Directory.Exists(directory))
                continue;

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"warning: cannot remove {directory}: {e.Message}");
            }
        }
    }

    //Usage 参数没给对时打一遍用法
    private static void Usage()
    {
        Console.WriteLine($"Usage: ncm remove {NugetKind} <id>");
        Console.WriteLine($"       ncm remove {ModKind} <id>");
    }
}
