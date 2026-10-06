using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Tools;

namespace NetCraft.ModBuild;

//Program 主入口 第一个参数是工具名 不带参数或认不出一律打印帮助
public static class Program
{
    //窗口要求 STA 线程 少了这一条在 Windows 上连窗都起不来
    [STAThread]
    public static int Main(string[] args)
    {
        //引擎要从本机 sdk 目录取 解析事件得赶在第一个 msbuild 类型被加载之前挂上
        MsBuildLibrary.Attach(out _);

        //加新工具在这里补一行登记
        IconTool.Register();
        InitTool.Register();
        TemplateTool.Register();
        AddTool.Register();
        RestoreTool.Register();
        BuildTool.Register();
        CleanTool.Register();
        AsmTool.Register();
        RunServerTool.Register();
        UpgradeTool.Register();
        UpdateTool.Register();

        if (args.Length == 0)
        {
            Help.Print(Console.Out);
            return 0;
        }

        //help 后面跟工具名就看那一个工具的参数 不带就跟整个列表
        if (args[0] is "help" or "--help" or "-h")
        {
            if (args.Length > 1)
                return Help.PrintTool(Console.Out, args[1]);

            Help.Print(Console.Out);
            return 0;
        }

        //项目任务先按精确名字找 内置工具再按宽松方式兜底
        //于是任务叫 Build 与内置的 build 两不相干 各自都还能用
        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        var task = project?.FindTask(args[0]);
        if (task is not null)
        {
            //任务参数还没做 收着不报错 但得让人知道没生效
            if (args.Length > 1)
                Console.WriteLine($"note: task arguments are not supported yet, {args.Length - 1} argument(s) ignored");

            return TaskRunner.Run(project!, task);
        }

        var tool = ToolRegistry.Find(args[0]);
        if (tool is null)
        {
            Help.Print(Console.Out, args[0]);
            return 1;
        }

        return tool.Run(args[1..]);
    }
}
