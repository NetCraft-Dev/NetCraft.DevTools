using NetCraft.ModBuild.Core;
using NuGet.Versioning;

namespace NetCraft.ModBuild.Tools;

//AddTool 往项目配置里加一条依赖
//nuget 那条已经能用 mod 那条等云端 mod 服务器做出来再补
internal static class AddTool
{
    //NugetKind 包种类 从 nuget 源取
    private const string NugetKind = "nuget";

    //ModKind 包种类 从云端 mod 服务器取 还没实现
    private const string ModKind = "mod";

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("add", "Add a dependency to the project config", Run,
        [
            new($"{NugetKind} <id> [version]", "Add a nuget package, the version takes the same syntax as dotnet add package"),
            new($"{ModKind} <id> [version]", "Add a mod dependency, not implemented yet"),
        ]);

    //Run 认包种类 验版本 交给配置去写
    private static int Run(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Usage();
            return 1;
        }

        var kind = args[0];
        var id = args[1];
        var version = args.Length == 3 ? args[2] : string.Empty;

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

        //版本语法与 dotnet add package 一致 先在这儿验一遍 免得写进配置到 restore 才发现读不懂
        if (version.Length > 0 && !VersionRange.TryParse(version, out _))
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

        if (!project.AddPackage(id, version, out var error))
        {
            Console.WriteLine($"error: {error}");
            return 1;
        }

        Console.WriteLine(version.Length > 0
            ? $"Added {id} {version} to {project.Path}"
            : $"Added {id} to {project.Path}, the version is left open");
        Console.WriteLine("Run 'ncm restore' to fetch it");
        return 0;
    }

    //Usage 参数没给对时打一遍用法
    private static void Usage()
    {
        Console.WriteLine($"Usage: ncm add {NugetKind} <id> [version]");
        Console.WriteLine($"       ncm add {ModKind} <id> [version]");
    }
}
