using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//CleanTool 清掉构建缓存
//默认只清项目里的 Build 想连下载缓存一起清就带 --all
internal static class CleanTool
{
    //AllOption 连下载下来的缓存一起清
    private const string AllOption = "--all";

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("clean", "Remove the build cache of the current project", Run,
        [
            new(AllOption, "Also remove the downloaded package cache and the kernel cache"),
        ]);

    //Run 先清项目里那份 带了 --all 再清下载缓存
    //清掉的东西下次构建会重新备 所以默认不动下载缓存 联网重下不值当
    private static int Run(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == AllOption)
                continue;

            Console.WriteLine($"error: unknown clean option {arg}");
            Console.WriteLine($"Usage: ncm clean [{AllOption}]");
            return 1;
        }

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        var freed = 0L;
        if (project is not null)
            freed += Remove(Path.Combine(project.Directory, ProjectLayout.Build), "build cache");

        if (args.Contains(AllOption))
        {
            freed += Remove(PackageResolver.CacheRoot, "package cache");

            ServerStore.Configure(project);
            freed += Remove(ServerStore.Root, "kernel cache");
        }

        Console.WriteLine($"Freed {freed / 1024.0 / 1024.0:F1} MB");
        return 0;
    }

    //Remove 删一个目录 返回释放的字节数
    private static long Remove(string directory, string what)
    {
        if (!Directory.Exists(directory))
            return 0;

        var freed = Size(directory);
        try
        {
            Directory.Delete(directory, recursive: true);
            Console.WriteLine($"Removed {what} at {directory}");
            return freed;
        }
        catch (IOException e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: cannot remove {directory}: {e.Message}");
            Console.ResetColor();
            return 0;
        }
    }

    //Size 目录占用
    private static long Size(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
