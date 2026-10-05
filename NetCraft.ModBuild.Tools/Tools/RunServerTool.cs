using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//RunServerTool 在当前目录把 NetCraft 服务端跑起来
//只认模组项目 运行时文件从程序根目录的 Server 取 存档与日志落在当前目录的 run 下
//开服前把当前模组备进 run/mods 后面的参数一个不解析 原样交给服务端
internal static class RunServerTool
{
    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("runserver", "Build the current mod and run the NetCraft server", Run,
        [
            new("[server args]", "Everything after runserver is passed to the server unchanged"),
        ]);

    //Run 备齐运行时文件 备好模组 再交给启动器
    //前置步骤任何一步没过都到不了启动那一步
    private static int Run(string[] args)
    {
        if (!ServerStore.Ensure())
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
        return ModStaging.Stage(root, ServerLauncher.RunDirectory)
            ? ServerLauncher.Launch(args)
            : 1;
    }
}
