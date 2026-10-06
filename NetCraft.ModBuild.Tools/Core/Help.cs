namespace NetCraft.ModBuild.Core;

//Help 帮助文本 不带参数或参数不认识时打印
public static class Help
{
    //Usage 命令行形状 工具名之后的参数由各工具自己解析
    public const string Usage = "ncm <tool> [options]";

    //Print 列出全部工具与各自的一句话说明 unknown 非空时先提一句没认出来
    //当前目录属于一个项目时再补一段这个项目自己的任务
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

        WriteProjectTasks(writer);

        writer.WriteLine();
        writer.WriteLine("Run 'ncm help <tool>' to see the parameters of one tool.");
    }

    //PrintTool 打印一个工具的参数说明 内置里没有就看是不是项目任务 返回进程退出码
    public static int PrintTool(TextWriter writer, string name)
    {
        //与执行那边同一套优先级 先精确命中项目任务 再宽松找内置
        var project = NcProject.TryFind(Environment.CurrentDirectory, out _);
        var task = project?.FindTask(name);
        if (task is not null)
        {
            WriteTask(writer, task);
            return 0;
        }

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

    //WriteProjectTasks 补一段项目任务 撞上内置的名字另外提一句
    private static void WriteProjectTasks(TextWriter writer)
    {
        var project = NcProject.TryFind(Environment.CurrentDirectory, out var error);
        if (!string.IsNullOrEmpty(error))
        {
            writer.WriteLine();
            Warning(writer, error);
            return;
        }

        if (project is null)
            return;

        var tasks = project.Runnable();
        if (tasks.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Project tasks:");
            var width = tasks.Max(task => task.Name.Length);
            foreach (var task in tasks)
                writer.WriteLine($"  {task.Name.PadRight(width)}  {task.Title}");
        }

        foreach (var task in project.Shadowed())
            Warning(writer, $"task {task.Name} conflicts with a built-in tool name, consider renaming it");
    }

    //WriteTask 打印一个项目任务 说明 前置与每条命令
    private static void WriteTask(TextWriter writer, NcTask task)
    {
        writer.WriteLine($"{task.Name} - {task.Title}");
        if (task.Depends.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Depends on: {string.Join(", ", task.Depends)}");
        }

        writer.WriteLine();
        if (task.Commands.Count == 0)
        {
            writer.WriteLine("This task has no command.");
            return;
        }

        writer.WriteLine("Commands:");
        foreach (var command in task.Commands)
            writer.WriteLine($"  {command}");
    }

    //Warning 黄色警告一行
    private static void Warning(TextWriter writer, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        writer.WriteLine($"warning: {message}");
        Console.ForegroundColor = previous;
    }
}
