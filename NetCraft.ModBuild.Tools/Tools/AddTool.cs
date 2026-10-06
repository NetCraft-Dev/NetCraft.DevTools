using NetCraft.ModBuild.Core;
using NuGet.Versioning;

namespace NetCraft.ModBuild.Tools;

//AddTool 往项目配置里加一条依赖或引用
//nuget 那条已经能用 mod 那条等云端 mod 服务器做出来再补
internal static class AddTool
{
    //NugetKind 包种类 从 nuget 源取
    private const string NugetKind = "nuget";

    //ModKind 包种类 从云端 mod 服务器取 还没实现
    private const string ModKind = "mod";

    //FileKind 直接引用的托管 dll 写进 <References> 的 <File>
    private const string FileKind = "file";

    //ProjectKind 引用的另一个工程 写进 <References> 的 <Project>
    private const string ProjectKind = "project";

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("add", "Add a dependency or a reference to the project config", Run,
        [
            new($"{NugetKind} <id> [version]", "Add a nuget package, the version takes the same syntax as dotnet add package"),
            new($"{FileKind} <path>", "Add a managed dll as a compile reference, relative to the project root"),
            new($"{ProjectKind} <path>", "Add another project as a compile reference, built first and referenced by its output"),
            new($"{ModKind} <id> [version]", "Add a mod dependency, not implemented yet"),
        ]);

    //Run 认种类 按种类校验参数 再交给配置去写
    private static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Usage();
            return 1;
        }

        var kind = args[0];
        var value = args[1];

        if (string.Equals(kind, ModKind, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: {ModKind} dependencies are not implemented yet, they will come with the cloud mod server");
            return 1;
        }

        var isNuget = string.Equals(kind, NugetKind, StringComparison.OrdinalIgnoreCase);
        var isFile = string.Equals(kind, FileKind, StringComparison.OrdinalIgnoreCase);
        var isProject = string.Equals(kind, ProjectKind, StringComparison.OrdinalIgnoreCase);
        if (!isNuget && !isFile && !isProject)
        {
            Console.WriteLine($"error: unknown kind {kind}");
            Usage();
            return 1;
        }

        //只有 nuget 收第二个可选参数 另两种多给一个就是敲错了
        var limit = isNuget ? 3 : 2;
        if (args.Length > limit)
        {
            Usage();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine(isNuget ? "error: a package id is required" : "error: a path is required");
            return 1;
        }

        var version = args.Length == 3 ? args[2] : string.Empty;

        //版本语法与 dotnet add package 一致 先在这儿验一遍 免得写进配置到 restore 才发现读不懂
        if (isNuget && version.Length > 0 && !VersionRange.TryParse(version, out _))
        {
            Console.WriteLine($"error: {version} is not a valid version or range");
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

        var added = isNuget
            ? project.AddPackage(value, version, out var error)
            : project.AddReference(value, isProject, out error);

        if (!added)
        {
            Console.WriteLine($"error: {error}");
            return 1;
        }

        if (!isNuget)
        {
            Console.WriteLine($"Added {(isProject ? "project" : "file")} {value} to {project.Path}");
            return 0;
        }

        Console.WriteLine(version.Length > 0
            ? $"Added {value} {version} to {project.Path}"
            : $"Added {value} to {project.Path}, the version is left open");
        Console.WriteLine("Run 'ncm restore' to fetch it");
        return 0;
    }

    //Usage 参数没给对时打一遍用法
    private static void Usage()
    {
        Console.WriteLine($"Usage: ncm add {NugetKind} <id> [version]");
        Console.WriteLine($"       ncm add {FileKind} <path>");
        Console.WriteLine($"       ncm add {ProjectKind} <path>");
        Console.WriteLine($"       ncm add {ModKind} <id> [version]");
    }
}
