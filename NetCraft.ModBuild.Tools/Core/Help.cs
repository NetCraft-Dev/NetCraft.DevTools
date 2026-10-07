using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Core;

//Help text printed when no argument is given or the argument is unknown
public static class Help
{
    //Command line shape; each tool parses the arguments that follow its name
    public const string Usage = "ncm <tool> [options]";

    //Print every tool with its one-line description, plus the current project's own tasks when inside one
    public static void Print(TextWriter writer)
    {
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

    //Report an unrecognized name in red, adding a suggestion when a close match exists
    public static void UnknownTool(TextWriter writer, string name, string? suggestion)
    {
        Red(writer, $"Unknown tool: {name}");
        if (!string.IsNullOrEmpty(suggestion))
            writer.WriteLine($"Did you mean \"{suggestion}\"?");
    }

    //Names eligible as suggestions, built-in tools plus the project's runnable tasks
    public static List<string> Candidates(NcProject? project)
    {
        var names = ToolRegistry.All.Select(entry => entry.Name).ToList();
        if (project is not null)
            names.AddRange(project.Runnable().Select(task => task.Name));
        return names;
    }

    //Print one tool's parameter help, falling back to a project task when no built-in matches, and return the process exit code
    public static int PrintTool(TextWriter writer, string name)
    {
        //Same precedence as execution, an exact project task wins over a loose built-in match
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
            UnknownTool(writer, name, Similarity.Closest(name, Candidates(project)));
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
        //Align the two columns on the longest syntax by padding shorter names
        var width = tool.Parameters.Max(item => item.Syntax.Length);
        foreach (var parameter in tool.Parameters)
            writer.WriteLine($"  {parameter.Syntax.PadRight(width)}  {parameter.Description}");
        return 0;
    }

    //Append the project's tasks, warning when a name shadows a built-in
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

    //Print a project task's title, dependencies and steps
    private static void WriteTask(TextWriter writer, NcTask task)
    {
        writer.WriteLine($"{task.Name} - {task.Title}");
        if (task.Depends.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Depends on: {string.Join(", ", task.Depends)}");
        }

        writer.WriteLine();
        if (task.Steps.Count == 0)
        {
            writer.WriteLine("This task has no step.");
            return;
        }

        writer.WriteLine("Steps:");
        foreach (var step in task.Steps)
            writer.WriteLine($"  {step.Text}");
    }

    private static void Warning(TextWriter writer, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        writer.WriteLine($"warning: {message}");
        Console.ForegroundColor = previous;
    }

    private static void Red(TextWriter writer, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        writer.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
