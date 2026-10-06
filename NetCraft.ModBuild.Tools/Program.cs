using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;
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

        return Dispatch(args[0], args[1..], project);
    }

    //Dispatch 按名字找活干 项目任务优先于内置工具
    private static int Dispatch(string name, string[] rest, NcProject? project)
    {
        var task = project?.FindTask(name);
        if (task is not null)
        {
            //任务参数还没做 收着不报错 但得让人知道没生效
            if (rest.Length > 0)
                Console.WriteLine($"note: task arguments are not supported yet, {rest.Length} argument(s) ignored");

            return TaskRunner.Run(project!, task);
        }

        var tool = ToolRegistry.Find(name);
        if (tool is not null)
            return tool.Run(rest);

        return Unknown(name, rest, project);
    }

    //Unknown 没认出来的名字 红色报一句 挑得出相近的就问一句要不要照那个跑
    //输入被重定向时问不了 只把建议打出来 免得在脚本里替人做决定
    private static int Unknown(string name, string[] rest, NcProject? project)
    {
        var suggestion = Similarity.Closest(name, Help.Candidates(project));
        Help.UnknownTool(Console.Out, name, suggestion);

        if (suggestion is not null && !Console.IsInputRedirected
            && Prompt.Confirm($"Run \"ncm {suggestion}\" instead?", defaultYes: true, warn: true))
            return Dispatch(suggestion, rest, project);

        Console.WriteLine();
        Help.Print(Console.Out);
        return 1;
    }
}
