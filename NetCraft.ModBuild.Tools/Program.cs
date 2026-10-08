using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;
using NetCraft.ModBuild.Tools;

namespace NetCraft.ModBuild;

//Main entry point, the first argument is the tool name and a missing or unrecognized one prints help
public static class Program
{
    //Required by the windowing code, the window cannot even start on Windows without it
    [STAThread]
    public static int Main(string[] args)
    {
        //The engine comes from the local sdk, and the resolve event must be hooked before the first msbuild type loads
        MsBuildLibrary.Attach(out _);

        //Register a new tool with one line here
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
        ToolCommand.Register();

        if (args.Length == 0)
        {
            Help.Print(Console.Out);
            return 0;
        }

        //`help <tool>` shows that tool's arguments, bare `help` shows the full list
        if (args[0] is "help" or "--help" or "-h")
        {
            if (args.Length > 1)
                return Help.PrintTool(Console.Out, args[1]);

            Help.Print(Console.Out);
            return 0;
        }

        //Project tasks are matched by exact name first and built-in tools only as a loose fallback
        //So a task named Build and the built-in build stay independent and both keep working
        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        return Dispatch(args[0], args[1..], project);
    }

    //Resolves work by name, with project tasks taking priority over built-in tools
    private static int Dispatch(string name, string[] rest, NcProject? project)
    {
        var task = project?.FindTask(name);
        if (task is not null)
        {
            //Task arguments are not implemented yet, so they are accepted without error but reported as ignored
            if (rest.Length > 0)
                Console.WriteLine($"note: task arguments are not supported yet, {rest.Length} argument(s) ignored");

            return TaskRunner.Run(project!, task);
        }

        var tool = ToolRegistry.Find(name);
        if (tool is null)
            return Unknown(name, rest, project);

        //A misspelled subcommand gets a suggestion too, since fixing only the tool name would be half a fix
        var repaired = RepairSub(tool, rest);
        if (repaired is not null && AskSub(name, rest[0], repaired[0]))
            return tool.Run(repaired);

        return tool.Run(rest);
    }

    //Suggests a close subcommand, returning null when none fits
    //Options and positional arguments are out of scope because the usage text cannot tell them apart from a subcommand
    private static string[]? RepairSub(ToolEntry tool, string[] rest)
    {
        if (rest.Length == 0 || rest[0].StartsWith('-'))
            return null;

        var known = ToolRegistry.SubCommandsOf(tool);

        //a subcommand that already matches is left alone; an exact name is never suggested for itself, so the nearest
        //other one would win and a valid command would be rewritten, sending `template gui` off to `tui`
        if (known.Contains(rest[0], StringComparer.Ordinal))
            return null;

        var suggestion = Similarity.Closest(rest[0], known);
        if (suggestion is null)
            return null;

        var repaired = (string[])rest.Clone();
        repaired[0] = suggestion;
        return repaired;
    }

    //Reports an unknown subcommand and offers to run the suggested one
    //Stays silent when input is redirected so scripts never hit an unanswered prompt
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

    //Reports an unrecognized name in red and offers a close match when there is one
    //Repairs the subcommand too, so only half the command is not fixed
    //Prints the suggestion when input is redirected instead of deciding for a script
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
