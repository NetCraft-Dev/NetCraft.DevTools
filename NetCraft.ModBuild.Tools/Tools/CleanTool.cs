using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//CleanTool removes the build cache
//Inside a project it only clears Build, elsewhere only the shared download cache exists and removal asks first
internal static class CleanTool
{
    //AllOption also clears the download cache inside a project and skips the question outside one
    private const string AllOption = "--all";

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("clean", "Remove the project build cache, or the shared download cache when there is no project here", Run,
        [
            new(AllOption, "Remove everything ncm downloaded without asking: the client jar, the server runtime, the template files, the packages and the update package"),
        ]);

    //Run clears the project copy inside a project, or the shared copy outside one after asking
    //Everything removed is re-fetched on the next build, so the download cache is left alone inside a project to avoid a pointless re-download
    private static int Run(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == AllOption)
                continue;

            Console.WriteLine($"error: unknown clean option {arg}");
            Console.WriteLine($"Usage: ncm clean [{AllOption}]");
            return 1;
        }

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        var all = args.Contains(AllOption);
        var freed = 0L;

        if (project is not null)
        {
            freed += Remove(Path.Combine(project.Directory, ProjectLayout.Build), "build cache");

            if (all)
                freed += RemoveDownloads(project);
        }
        //Outside a project only the shared cache can be cleared, ask before touching it
        else if (all || AskDownloads())
        {
            freed += RemoveDownloads(project);
        }
        else
        {
            Console.WriteLine("Nothing removed");
        }

        Console.WriteLine($"Freed {freed / 1024.0 / 1024.0:F1} MB");
        return 0;
    }

    //AskDownloads asks whether to clear the shared cache, showing its location and size
    //With redirected input nobody can answer, so it only explains what to do instead of deleting
    private static bool AskDownloads()
    {
        //The update cache sits beside the download cache, together they make up everything ncm keeps in the user directory
        var size = (Size(CacheLayout.Root) + Size(CacheLayout.Update)) / 1024.0 / 1024.0;
        if (!Directory.Exists(CacheLayout.Root) && !Directory.Exists(CacheLayout.Update))
        {
            Console.WriteLine($"No project here and everything ncm downloaded under {CacheLayout.Home} is already gone");
            return false;
        }

        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"No project here, what ncm downloaded under {CacheLayout.Home} takes {size:F1} MB, pass {AllOption} to remove it");
            return false;
        }

        Console.WriteLine($"No project here, what ncm downloaded under {CacheLayout.Home} takes {size:F1} MB");
        return Prompt.Confirm("Remove it?", defaultYes: true, warn: true);
    }

    //RemoveDownloads clears the shared download cache and, when the project points the server cache elsewhere, that copy too
    private static long RemoveDownloads(NcProject? project)
    {
        var freed = Remove(CacheLayout.Root, "download cache");

        //The package and scripts kept in the update cache are useless, the next update re-downloads them
        freed += Remove(CacheLayout.Update, "update cache");

        ServerStore.Configure(project);
        if (!SamePath(ServerStore.Root, CacheLayout.Server))
            freed += Remove(ServerStore.Root, "server cache");

        return freed;
    }

    //SamePath whether two paths point to the same place, ignoring case and trailing separators
    private static bool SamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    //Remove deletes one directory and returns the freed bytes
    private static long Remove(string directory, string what)
    {
        if (!Directory.Exists(directory))
            return 0;

        var freed = Size(directory);
        try
        {
            Directory.Delete(directory, recursive: true);
            Console.WriteLine($"Removed {what} at {directory}");
            return freed;
        }
        catch (IOException e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: cannot remove {directory}: {e.Message}");
            Console.ResetColor();
            return 0;
        }
    }

    //Size the directory footprint in bytes
    private static long Size(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
