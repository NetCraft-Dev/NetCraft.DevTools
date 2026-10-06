using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//CleanTool 清掉构建缓存
//项目里默认只清 Build 不在项目里就只有共享下载缓存可清 删之前问一句
internal static class CleanTool
{
    //AllOption 项目里连下载缓存一起清 不在项目里跳过那一问
    private const string AllOption = "--all";

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("clean", "Remove the project build cache, or the shared download cache when there is no project here", Run,
        [
            new(AllOption, "Remove the whole shared download cache without asking: the client jar, the server runtime, the template files and the packages"),
        ]);

    //Run 项目里清项目那份 不在项目里清共享那份并先问一句
    //清掉的东西下次构建会重新备 所以项目里默认不动下载缓存 联网重下不值当
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

        var all = args.Contains(AllOption);
        var freed = 0L;

        if (project is not null)
        {
            freed += Remove(Path.Combine(project.Directory, ProjectLayout.Build), "build cache");

            if (all)
                freed += RemoveDownloads(project);
        }
        //不在项目里 能清的只有共享缓存 问一句再动手
        else if (all || AskDownloads())
        {
            freed += RemoveDownloads(project);
        }
        else
        {
            Console.WriteLine("Nothing removed");
        }

        Console.WriteLine($"Freed {freed / 1024.0 / 1024.0:F1} MB");
        return 0;
    }

    //AskDownloads 问一句要不要清共享缓存 提示里带上位置与占用
    //输入被重定向时没人应答 那就只把该怎么做说清楚 不擅自删
    private static bool AskDownloads()
    {
        if (!Directory.Exists(CacheLayout.Root))
        {
            Console.WriteLine($"No project here and the shared download cache at {CacheLayout.Root} is already empty");
            return false;
        }

        var size = Size(CacheLayout.Root) / 1024.0 / 1024.0;
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"No project here, the shared download cache at {CacheLayout.Root} is {size:F1} MB, pass {AllOption} to remove it");
            return false;
        }

        Console.WriteLine($"No project here, the shared download cache at {CacheLayout.Root} is {size:F1} MB");
        return Prompt.Confirm("Remove it?", defaultYes: true, warn: true);
    }

    //RemoveDownloads 清共享下载缓存 项目把服务端缓存指到别处时那一份也一起
    private static long RemoveDownloads(NcProject? project)
    {
        var freed = Remove(CacheLayout.Root, "download cache");

        ServerStore.Configure(project);
        if (!SamePath(ServerStore.Root, CacheLayout.Server))
            freed += Remove(ServerStore.Root, "server cache");

        return freed;
    }

    //SamePath 两个路径是不是同一处 大小写与末尾斜杠都不计较
    private static bool SamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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
