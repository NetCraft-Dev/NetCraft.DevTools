using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//RunServerTool 在当前目录把 NetCraft 服务端跑起来
//运行时文件从程序根目录的 Server 取 存档与日志落在当前目录的 run 下
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
    private static int Run(string[] args)
    {
        if (!ServerStore.Ensure())
            return 1;

        //没有清单时把当前目录当根 与 build 那边同一套判定
        var project = ModProject.TryFind(Environment.CurrentDirectory);
        var root = project is null
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(project.ManifestPath)!;

        return ModStaging.Stage(root, project, ServerLauncher.RunDirectory)
            ? ServerLauncher.Launch(args)
            : 1;
    }
}
