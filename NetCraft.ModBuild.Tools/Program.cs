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
        if (tool is null)
            return Unknown(name, rest, project);

        //子命令写错也提一句 只修工具名那一层等于修一半
        var repaired = RepairSub(tool, rest);
        if (repaired is not null && AskSub(name, rest[0], repaired[0]))
            return tool.Run(repaired);

        return tool.Run(rest);
    }

    //RepairSub 子命令写错时换成相近的那个 换不动返回 null
    //选项与位置参数不在这里管 工具的用法表里的那种写法认不出子命令
    private static string[]? RepairSub(ToolEntry tool, string[] rest)
    {
        if (rest.Length == 0 || rest[0].StartsWith('-'))
            return null;

        var suggestion = Similarity.Closest(rest[0], ToolRegistry.SubCommandsOf(tool));
        if (suggestion is null)
            return null;

        var repaired = (string[])rest.Clone();
        repaired[0] = suggestion;
        return repaired;
    }

    //AskSub 报一句子命令没认出来 再问一句要不要照着改的跑
    //输入被重定向时一声不吭 交给工具自己去报错 免得脚本里冒出没人应答的问句
    private static bool AskSub(string tool, string written, string suggestion)
    {
        if (Console.IsInputRedirected)
            return false;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Unknown {tool} subcommand: {written}");
        Console.ResetColor();
        Console.WriteLine($"Did you mean \"{suggestion}\"?");

        return Prompt.Confirm($"Run \"ncm {tool} {suggestion}\" instead?", defaultYes: true, warn: true);
    }

    //Unknown 没认出来的名字 红色报一句 挑得出相近的就问一句要不要照那个跑
    //工具名修对了 后面的子命令照它的用法表也修一道 免得只修一半
    //输入被重定向时问不了 只把建议打出来 免得在脚本里替人做决定
    private static int Unknown(string name, string[] rest, NcProject? project)
    {
        var suggestion = Similarity.Closest(name, Help.Candidates(project));
        var tool = suggestion is null ? null : ToolRegistry.Find(suggestion);
        var repaired = tool is null ? rest : RepairSub(tool, rest) ?? rest;
        var target = suggestion is null ? null : string.Join(' ', new[] { suggestion }.Concat(repaired));

        Help.UnknownTool(Console.Out, name, target);

        if (target is not null && !Console.IsInputRedirected
            && Prompt.Confirm($"Run \"ncm {target}\" instead?", defaultYes: true, warn: true))
            return Dispatch(suggestion!, repaired, project);

        Console.WriteLine();
        Help.Print(Console.Out);
        return 1;
    }
}
