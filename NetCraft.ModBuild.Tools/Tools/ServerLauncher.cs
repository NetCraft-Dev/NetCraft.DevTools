using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//ServerLauncher loads the server kernel dynamically and runs it
//The flow matches NetCraft.ServerExe, except there are no compile-time kernel references, everything is resolved by name
//The main library and the loader are loaded by path here, while the remaining kernel assemblies go through the kernel's own resolve callback
internal static class ServerLauncher
{
    //KernelDirectoryName is the kernel assembly subfolder, aligned with the convention on the kernel side
    private const string KernelDirectoryName = "kernel";

    //RunDirectoryName is the server run folder holding saves, logs and config
    internal const string RunDirectoryName = "run";

    //RunDirectory is this run's folder under the current working directory, holding the mods and saves
    internal static string RunDirectory => Path.Combine(Environment.CurrentDirectory, RunDirectoryName);

    private const string ModLoaderAssemblyName = "NetCraft.ModLoader";

    //ModApiAssemblyName the mod api assembly name
    private const string ModApiAssemblyName = "NetCraft.ModApi";

    private const string ServerAssemblyName = "NetCraft.Server";

    //Launch prepares the run folder, syncs the kernel and starts through the same path as the server entry point
    public static int Launch(string[] args)
    {
        var source = ServerStore.Root;
        var run = RunDirectory;
        Directory.CreateDirectory(run);

        //The kernel has to live under the program root to be found, and since the root later points at run the kernel must move with it
        SyncKernel(source, Path.Combine(run, KernelDirectoryName));

        Directory.SetCurrentDirectory(run);

        //The main library loads first so the embedded resolver and the program root are set before other kernel assemblies resolve
        var main = Load(Path.Combine(source, "NetCraft.dll"));
        main.GetType("NetCraft.EmbeddedAssemblyLoader", throwOnError: true)!
            .GetMethod("Initialize")!.Invoke(null, null);
        main.GetType("NetCraft.AppPaths", throwOnError: true)!
            .GetMethod("SetOverride")!.Invoke(null, new object?[] { run });

        //Kernel assemblies stay with the kernel's own resolver, which runs them through the mod rewriter, so loading them here would break the injection
        RegisterResolver(source);

        //The log sink must be enabled before mod bootstrap, since the injection and kernel Initialize come after it and debug output would otherwise be lost
        EnableDebug(args);

        //Mod bootstrap must run before any server type is resolved
        var loader = Load(Path.Combine(source, ModLoaderAssemblyName + ".dll"));
        var bootstrap = loader.GetType("NetCraft.ModLoader.ModBootstrap", throwOnError: true)!;
        var environment = loader.GetType("NetCraft.ModLoader.ModEnvironment", throwOnError: true)!;
        bootstrap.GetMethod("Run")!.Invoke(null, new object?[] { Enum.Parse(environment, "Server"), null });

        //The server layer goes through the kernel resolver, which the bootstrap already preloaded as a rewritten assembly when injection applies
        //Loading it by path again would produce a second copy with mismatched types and undo that injection
        var server = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(ServerAssemblyName));
        server.GetType("NetCraft.Game.ServerMain", throwOnError: true)!
            .GetMethod("Run")!.Invoke(null, new object?[] { args });
        return 0;
    }

    //EnableDebug turns on the global debug mode and log levels for --debug
    //Same purpose as BootMods in ServerExe, except the types are not available at compile time so the two embedded libraries are loaded by name
    private static void EnableDebug(string[] args)
    {
        if (Array.IndexOf(args, "--debug") < 0)
            return;

        var config = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("NetCraft.Config"));
        var util = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("NetCraft.Util"));

        config.GetType("NetCraft.Config.DebugMode", throwOnError: true)!
            .GetProperty("IsEnabled")!.SetValue(null, true);

        var log = util.GetType("NetCraft.Logging.Log", throwOnError: true)!;
        var level = Enum.Parse(util.GetType("NetCraft.Logging.LogLevel", throwOnError: true)!, "Debug");
        log.GetMethod("SetConsoleLevel")!.Invoke(null, new[] { level });
        log.GetMethod("SetFileLevel")!.Invoke(null, new[] { level });
        log.GetMethod("SetVerbose")!.Invoke(null, new object?[] { true });
    }

    //SyncKernel copies the cached kernel assemblies into the target directory
    //Files matching on name and size are skipped, keeping later runs cheap
    //includeModLoader adds the loader for the project's compile references, which need the mod interfaces it carries
    //The run folder must not have it, since the main library and loader are already loaded here
    //includeModApi adds the mod api for projects built against it, a pure mod leaves it out
    //The root assembly NetCraft.dll also carries types those compile references need
    internal static void SyncKernel(string source, string destination, bool includeModLoader = false,
        bool includeModApi = true)
    {
        Directory.CreateDirectory(destination);

        var pattern = includeModLoader ? "NetCraft*.dll" : "NetCraft.*.dll";
        var copied = 0;
        foreach (var path in Directory.EnumerateFiles(source, pattern))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!includeModLoader && name == ModLoaderAssemblyName)
                continue;
            if (!includeModApi && name == ModApiAssemblyName)
                continue;

            var target = Path.Combine(destination, Path.GetFileName(path));
            var existing = new FileInfo(target);
            var origin = new FileInfo(path);
            //Treat a file as unchanged only when both size and write time match
            //Size alone would miss an edit that keeps the length, and such a file would never sync again
            if (existing.Exists && existing.Length == origin.Length
                && existing.LastWriteTimeUtc == origin.LastWriteTimeUtc)
                continue;

            File.Copy(path, target, overwrite: true);
            copied++;
        }

        Trace.Log($"kernel synced {copied} file(s) to {destination}");
    }

    //RegisterResolver handles everything that is not a kernel assembly, which the kernel resolves itself
    //Loading kernel assemblies here would run before the mod rewriter and make the injection rules too late
    private static void RegisterResolver(string source)
    {
        var resolver = new AssemblyDependencyResolver(Path.Combine(source, ServerAssemblyName + ".dll"));

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is null || name.Name.StartsWith("NetCraft", StringComparison.Ordinal))
                return null;

            var path = resolver.ResolveAssemblyToPath(name);
            if (path is null)
            {
                //Fall back to the directory for assemblies the deps file does not list
                var candidate = Path.Combine(source, name.Name + ".dll");
                path = File.Exists(candidate) ? candidate : null;
            }
            return path is null ? null : context.LoadFromAssemblyPath(path);
        };

        //Native libraries work the same way, taking the registered location or leaving it to the system
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
        };
    }

    //Load loads an assembly by path; there is no fallback here, failure throws
    private static Assembly Load(string path)
    {
        Trace.Log($"loading assembly {path}");
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
}
