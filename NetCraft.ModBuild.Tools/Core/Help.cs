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
    }
}
