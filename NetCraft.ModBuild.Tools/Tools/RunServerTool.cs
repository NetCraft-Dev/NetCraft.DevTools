using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//RunServerTool runs the NetCraft server against the current directory
//Runtime files come from the Server folder under the program root, while saves and logs land in run here
//It stages the current mod into run/mods first and passes the remaining arguments to the server untouched
internal static class RunServerTool
{
    //RefreshOption refreshes the runtime files against the remote index, skipping both the mod project and the build
    private const string RefreshOption = "--refresh";

    public static void Register()
        => ToolRegistry.Register("runserver", "Build the current mod and run the NetCraft server", Run,
        [
            new("--refresh", "Refresh the server cache against the remote index, no ncmod.json and no build needed"),
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

        //--refresh only refreshes the runtime files, mod project or not
        if (Array.IndexOf(args, RefreshOption) >= 0)
            return ServerStore.Refresh() ? ServerLauncher.Launch(Arguments(config, WithoutRefresh(args))) : 1;

        if (!ServerStore.Ensure())
            return 1;

        //Check for the run directory before staging, since staging creates it
        var firstRun = !Directory.Exists(ServerLauncher.RunDirectory);

        //The client jar comes from the predownload; the first launch uses it to extract the assets
        var jar = ClientStore.Ensure();
        if (jar is null)
            return 1;

        //Build and deploy both work from the project root, so nothing can launch outside a mod project
        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: no {ModProject.ManifestName} in this directory or any parent");
            Console.ResetColor();
            return 1;
        }

        var root = Path.GetDirectoryName(project.ManifestPath)!;
        if (!ModStaging.Stage(root, ServerLauncher.RunDirectory))
            return 1;

        var arguments = Arguments(config, args);
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
