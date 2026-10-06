using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//RunServerTool 在当前目录把 NetCraft 服务端跑起来
//运行时文件从程序根目录的 Server 取 存档与日志落在当前目录的 run 下
//开服前把当前模组备进 run/mods 后面的参数一个不解析 原样交给服务端
internal static class RunServerTool
{
    //RefreshOption 按远端清单刷运行时文件 带上它就不要求模组项目也不构建
    private const string RefreshOption = "--refresh";

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("runserver", "Build the current mod and run the NetCraft server", Run,
        [
            new("--refresh", "Refresh the server cache against the remote index, no ncmod.json and no build needed"),
            new("[server args]", "Everything after runserver is passed to the server unchanged"),
        ]);

    //Run 备齐运行时文件与客户端 jar 备好模组 再交给启动器
    //前置步骤任何一步没过都到不了启动那一步
    private static int Run(string[] args)
    {
        //配置是可选的 不在模组项目里也要能把运行时文件刷起来
        //缓存位置 内核来源 jar 版本都从它来 读不动就说一声接着走默认
        var config = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"warning: {configError}");
            Console.ResetColor();
        }

        ServerStore.Configure(config);
        ClientStore.Configure(config);

        //--refresh 只管刷运行时文件 当前目录是不是模组项目都无所谓
        if (Array.IndexOf(args, RefreshOption) >= 0)
            return ServerStore.Refresh() ? ServerLauncher.Launch(Arguments(config, WithoutRefresh(args))) : 1;

        if (!ServerStore.Ensure())
            return 1;

        //run 目录在不在得赶在备模组之前看 那一步会把 run 建出来
        var firstRun = !Directory.Exists(ServerLauncher.RunDirectory);

        //客户端 jar 跟着预下载一起备好 首次开服要拿它把 assets 提出来
        var jar = ClientStore.Ensure();
        if (jar is null)
            return 1;

        //构建与部署都按项目根来 不在模组项目里就没什么可开的
        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: no {ModProject.ManifestName} in this directory or any parent");
            Console.ResetColor();
            return 1;
        }

        var root = Path.GetDirectoryName(project.ManifestPath)!;
        if (!ModStaging.Stage(root, ServerLauncher.RunDirectory))
            return 1;

        var arguments = Arguments(config, args);
        return ServerLauncher.Launch(firstRun ? WithJarPath(arguments, jar) : arguments);
    }

    //Arguments 把配置里的自带参数与调试开关并到用户参数前面
    //用户参数在后 同名的能压过配置里那份
    private static string[] Arguments(NcProject? config, string[] args)
    {
        var combined = new List<string>(ArgumentLine.Split(config?.Server.Args ?? string.Empty));
        if (config?.Server.Debug == true && !combined.Contains("--debug"))
            combined.Add("--debug");

        combined.AddRange(args);
        return combined.ToArray();
    }

    //WithJarPath 首次开服把客户端 jar 指给服务端
    //assets 提过一次就长在 run 里了 之后每次再指它只是白跑一趟 自己传了参数的就按自己那份来
    private static string[] WithJarPath(string[] args, string jar)
        => args.Contains("--jar-path") ? args : [.. args, "--jar-path", jar];

    //WithoutRefresh --refresh 是 ncm 自己的开关 不往服务端传
    private static string[] WithoutRefresh(string[] args)
        => args.Where(arg => arg != RefreshOption).ToArray();
}
