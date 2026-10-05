namespace NetCraft.ModBuild.Core;

//Help 帮助文本 不带参数或参数不认识时打印
public static class Help
{
    //Usage 命令行形状 工具名之后的参数由各工具自己解析
    public const string Usage = "ncm <tool> [options]";

    //Print 列出全部工具与各自的一句话说明 unknown 非空时先提一句没认出来
    public static void Print(TextWriter writer, string? unknown = null)
    {
        if (unknown is not null)
            writer.WriteLine($"Unknown tool: {unknown}");

        writer.WriteLine("NetCraft mod development tools");
        writer.WriteLine();
        writer.WriteLine($"Usage: {Usage}");
        writer.WriteLine();
        writer.WriteLine("Tools:");
        foreach (var tool in ToolRegistry.All)
            writer.WriteLine($"  {tool.Name,-12} {tool.Description}");
        writer.WriteLine();
        writer.WriteLine("Run 'ncm help <tool>' to see the parameters of one tool.");
    }

    //PrintTool 打印一个工具的参数说明 返回进程退出码
    public static int PrintTool(TextWriter writer, string name)
    {
        var tool = ToolRegistry.Find(name);
        if (tool is null)
        {
            writer.WriteLine($"Unknown tool: {name}");
            return 1;
        }

        writer.WriteLine($"{tool.Name} - {tool.Description}");
        writer.WriteLine();
        if (tool.Parameters.Count == 0)
        {
            writer.WriteLine("This tool takes no parameters.");
            return 0;
        }

        writer.WriteLine("Parameters:");
        //两列按最长的那个用法对齐 短名字后面补齐空格
        var width = tool.Parameters.Max(item => item.Syntax.Length);
        foreach (var parameter in tool.Parameters)
            writer.WriteLine($"  {parameter.Syntax.PadRight(width)}  {parameter.Description}");
        return 0;
    }
}
