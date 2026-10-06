using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//RestoreTool 按项目配置里的包声明把依赖还原到项目里
//还原出来的是编译期引用 也是之后要嵌进模组程序集的那批
internal static class RestoreTool
{
    //DefaultDirectory 没给参数时的还原目录
    private static readonly string DefaultDirectory = ProjectLayout.Packages;

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("restore", "Resolve the declared packages and copy their assemblies into the project", Run,
        [
            new("[directory]", $"Where to restore, relative to the project root, defaults to {DefaultDirectory}"),
        ]);

    //Run 找项目 定目录 交给解析器
    private static int Run(string[] args)
    {
        if (args.Length > 1)
        {
            Console.WriteLine("Usage: ncm restore [directory]");
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

        var directory = DirectoryOf(project, args.Length == 1 ? args[0] : null);
        Console.WriteLine($"Restoring packages of {new DirectoryInfo(project.Directory).Name} into {directory}");

        if (!Ensure(project, directory, out var error))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {error}");
            Console.ResetColor();
            return 1;
        }

        return 0;
    }

    //Ensure 还原项目的依赖 已经就绪就直接过
    //build 那条链也走这里 还原只有这一份实现
    internal static bool Ensure(NcProject project, string directory, out string error)
    {
        error = string.Empty;
        if (Ready(project, directory))
        {
            Trace.Log($"packages of {project.Directory} are ready in {directory}");
            return true;
        }

        return PackageResolver.Restore(project, directory, out error);
    }

    //DirectoryOf 还原落点 给了就用给的 没给走默认
    internal static string DirectoryOf(NcProject project, string? directory = null)
        => Path.GetFullPath(Path.Combine(project.Directory,
            string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory));

    //Ready 声明的包都落在目录里了
    //传递依赖跟着声明走 restore 一次就齐 这里只认声明过的那些
    private static bool Ready(NcProject project, string directory)
    {
        if (project.Packages.Count == 0 || !Directory.Exists(directory))
            return false;

        var folders = Directory.EnumerateDirectories(directory)
            .Select(Path.GetFileName)
            .ToList();

        foreach (var package in project.Packages)
        {
            if (!folders.Any(name => string.Equals(name, package.Id, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }
}
