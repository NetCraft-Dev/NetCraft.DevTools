using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//KernelStore the kernel reference assemblies in a project
//They come from the same place as the server copy, reusing the same cache and download, just placed in the project
internal static class KernelStore
{
    //Sync makes sure the kernel references are complete and reports whether they are usable
    //The direct build can only compile against a complete kernel, a single missing assembly makes the whole source report missing types
    public static bool Sync(string root, NcProject? project)
    {
        var destination = Path.Combine(root, ProjectLayout.Kernel);
        if (HasAssemblies(destination))
        {
            Trace.Log($"kernel of {root} is ready in {destination}");
            return true;
        }

        ServerStore.Configure(project);
        if (!ServerStore.Ensure())
            return false;

        //The mod api lives in the loader assembly, so the compile reference must include it
        //A project that declares it does not use the api skips it, keeping that assembly out of a pure mod
        ServerLauncher.SyncKernel(ServerStore.Root, destination, includeModLoader: true,
            includeModApi: project?.Build.DependsOnModApi ?? NcBuild.DefaultDependsOnModApi);
        if (HasAssemblies(destination))
            return true;

        Console.WriteLine($"error: no kernel assembly found in {ServerStore.Root}");
        return false;
    }

    //HasAssemblies whether the kernel assemblies in a directory are complete
    //The root assembly NetCraft.dll counts on its own and holds the settings and launch argument types
    //Only the dotted sub assemblies decide completeness, so an old directory is detected and synced again
    private static bool HasAssemblies(string directory)
        => Directory.Exists(directory)
            && File.Exists(Path.Combine(directory, "NetCraft.dll"))
            && Directory.EnumerateFiles(directory, "NetCraft.*.dll").Any();
}
