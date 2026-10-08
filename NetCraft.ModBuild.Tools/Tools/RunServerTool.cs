using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//RunServerTool runs the NetCraft server against the current directory
//Runtime files come from the Server folder under the program root, while saves and logs land in run here
//It stages the current mod into run/mods first and passes the remaining arguments to the server untouched
internal static class RunServerTool
{
    //RefreshOption compares the cache against the remote index instead of only filling in what is missing
    private const string RefreshOption = "--refresh";

    public static void Register()
        => ToolRegistry.Register("runserver", "Build the current mod and run the NetCraft server", Run,
        [
            new("--refresh", "Refresh the server cache against the remote index first, outside a mod project the refresh is all that runs"),
            new("[server args]", "Everything after runserver is passed to the server unchanged"),
        ]);

    //Run prepares the runtime files and the client jar, stages the mod and hands off to the launcher
    private static int Run(string[] args)
    {
        //The config is optional so the runtime files can be refreshed outside a mod project
        //It supplies the cache location, kernel source and jar version; when unreadable it warns and continues with defaults
        var config = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"warning: {configError}");
            Console.ResetColor();
        }

        ServerStore.Configure(config);
        ClientStore.Configure(config);

        //The kernel restarts the process once to get its native layer loaded and repeats this command line to do it
        //Everything was prepared before the first launch, and a repeat could not refresh the native layer this process
        //has mapped anyway, so the restarted process goes straight to the launch
        if (ServerLauncher.Launched)
            return ServerLauncher.Launch(Arguments(config, WithoutRefresh(args)));

        //--refresh is not a reason to skip the mod, a refreshed kernel running a stale mod is the combination this
        //command exists to prevent; it only changes how the runtime files are brought up to date
        var refresh = Array.IndexOf(args, RefreshOption) >= 0;
        if (refresh)
        {
            if (!ServerStore.Refresh())
                return 1;
        }
        else if (!ServerStore.Ensure())
        {
            return 1;
        }

        //Check for the run directory before staging, since staging creates it
        var firstRun = !Directory.Exists(ServerLauncher.RunDirectory);
        var arguments = Arguments(config, WithoutRefresh(args));

        //--refresh stands on its own outside a mod project, where the runtime files are the whole point
        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
        {
            if (refresh)
                return ServerLauncher.Launch(arguments);

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: no {ModProject.ManifestName} in this directory or any parent");
            Console.ResetColor();
            return 1;
        }

        //The client jar comes from the predownload; the first launch uses it to extract the assets
        var jar = ClientStore.Ensure();
        if (jar is null)
            return 1;

        //Build and deploy both work from the project root, so nothing can launch outside a mod project
        var root = Path.GetDirectoryName(project.ManifestPath)!;
        if (!ModStaging.Stage(root, ServerLauncher.RunDirectory,
            config?.Build.DependsOnModApi ?? NcBuild.DefaultDependsOnModApi))
            return 1;

        return ServerLauncher.Launch(firstRun ? WithJarPath(arguments, jar) : arguments);
    }

    //Arguments puts the config's own arguments and debug flag ahead of the user's
    //User arguments come last so they override the config ones
    private static string[] Arguments(NcProject? config, string[] args)
    {
        var combined = new List<string>(ArgumentLine.Split(config?.Server.Args ?? string.Empty));
        if (config?.Server.Debug == true && !combined.Contains("--debug"))
            combined.Add("--debug");

        combined.AddRange(args);
        return combined.ToArray();
    }

    //WithJarPath points the server at the client jar on the first launch
    //The assets stay in run after being extracted once, so it defers to any --jar-path the user already passed
    private static string[] WithJarPath(string[] args, string jar)
        => args.Contains("--jar-path") ? args : [.. args, "--jar-path", jar];

    //WithoutRefresh strips --refresh, which is ncm's own switch and must not reach the server
    private static string[] WithoutRefresh(string[] args)
        => args.Where(arg => arg != RefreshOption).ToArray();
}
