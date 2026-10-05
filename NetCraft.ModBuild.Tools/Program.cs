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
        //加新工具在这里补一行登记
        IconTool.Register();
        InitTool.Register();
        TemplateTool.Register();
        BuildTool.Register();
        AsmTool.Register();
        RunServerTool.Register();
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

        var tool = ToolRegistry.Find(args[0]);
        if (tool is null)
        {
            Help.Print(Console.Out, args[0]);
            return 1;
        }

        return tool.Run(args[1..]);
    }
}
