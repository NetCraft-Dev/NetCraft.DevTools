using System.Diagnostics;
using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Tools;

//BuildTool diagnoses the project first, then hands it to dotnet build and collects the output into Build/
internal static class BuildTool
{
    private const string DefaultConfiguration = "Release";

    //OutputDirectoryName where build output is collected, both the auto server and auto client chains read it
    private static readonly string OutputDirectoryName = ProjectLayout.Output;

    //OutputPath the output directory under the project root, honoring a custom output path from the config
    internal static string OutputPath(string root)
    {
        var config = NcProject.TryFind(root, out _);
        return Path.Combine(root, config?.Build.Output ?? OutputDirectoryName);
    }

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("build", "Diagnose and build the mod project in the current directory", Run,
        [
            new("-c, --configuration <name>", "Build configuration, defaults to Release"),
            new("--no-check", "Skip the api and syntax checks and run dotnet build directly"),
            new("--check-only", "Run the api and syntax checks only, build nothing"),
            new("--no-manifest", "Skip the ncmod.json lookup, treat the current directory as a plain C# project (must contain a csproj)"),
        ]);

    //Run parses the arguments, diagnoses, builds and collects the output
    //The command line comes through here and the auto server chain calls it directly, empty arguments mean the default configuration and full checks
    internal static int Run(string[] args)
    {
        var configuration = DefaultConfiguration;
        var check = true;
        var checkOnly = false;
        var noManifest = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-c":
                case "--configuration":
                    if (index + 1 >= args.Length)
                    {
                        Console.WriteLine($"error: {args[index]} needs a value");
                        return 1;
                    }
                    configuration = args[++index];
                    break;
                case "--no-check":
                    check = false;
                    break;
                case "--check-only":
                    checkOnly = true;
                    break;
                case "--no-manifest":
                    noManifest = true;
                    break;
                default:
                    Console.WriteLine($"error: unknown build option {args[index]}");
                    Console.WriteLine("Usage: ncm build [-c|--configuration <name>] [--no-check] [--check-only] [--no-manifest]");
                    return 1;
            }
        }

        //Both switches together leave nothing to do, blocked up front
        if (checkOnly && !check)
        {
            Console.WriteLine("error: --check-only and --no-check cannot be used together");
            return 1;
        }

        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null && !noManifest)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: no {ModProject.ManifestName} in this directory or any parent");
            Console.ResetColor();
            return 1;
        }

        //Without a manifest the current directory is treated as a plain C# project and must contain a csproj
        //Otherwise the recursive walk would pull in subprojects and template sample sources
        if (project is null && !HasCsproj(Environment.CurrentDirectory))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("error: no .csproj in this directory, this is not a valid C# project");
            Console.ResetColor();
            return 1;
        }

        //Without a manifest the root is the current directory and the name is the directory name
        var root = project is null
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(project.ManifestPath)!;
        var display = project?.DisplayName ?? new DirectoryInfo(root).Name;

        var action = checkOnly ? "Checking" : "Building";
        Console.WriteLine($"{action} {display} ({configuration})");
        Console.WriteLine();

        //A broken config must be reported so nobody mistakes it for default settings
        var config = NcProject.TryFind(root, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {configError}");
            Console.ResetColor();
            return 1;
        }

        //The direct build and the checks both need references, so kernel assemblies and packages come first
        if (config is not null && !Prepare(root, config))
            return 1;

        if (check && !RunChecks(root, config))
            return 1;

        //Check-only stops here and leaves no build output
        if (checkOnly)
            return 0;

        //A project with ncproj builds through Roslyn, one without still goes to dotnet
        if (config is not null)
            return RunRoslynBuild(root, config);

        if (!RunDotnetBuild(root, configuration))
            return 1;

        return CollectOutput(root, configuration);
    }

    //Prepare readies what the direct build needs: kernel assemblies, declared packages and project reference outputs
    //Each one is fetched only when missing, present files are skipped to avoid the network and re-downloads
    private static bool Prepare(string root, NcProject config)
    {
        if (!KernelStore.Sync(root, config))
            return false;

        if (!RestoreTool.Ensure(config, RestoreTool.DirectoryOf(config), out var error))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {error}");
            Console.ResetColor();
            return false;
        }

        //Project references are resolved once here, the build and checks then read from the cache
        ProjectReferences.Clear();
        if (ProjectReferences.Build(root, config, out var referenceError))
            return true;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: {referenceError}");
        Console.ResetColor();
        return false;
    }

    //RunRoslynBuild compiles the mod assembly with Roslyn, bypassing dotnet and MSBuild projects
    //Whether to compile is decided by timestamps in the compiler, this only reports the result
    private static int RunRoslynBuild(string root, NcProject config)
    {
        var name = AssemblyNameOf(root, config);
        var target = Path.Combine(root, config.Build.Output, name + ".dll");

        if (!Compiler.Compile(root, config, name, target, out var error))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {error}");
            Console.ResetColor();
            return 1;
        }

        //Package targets run after compilation, which is the step UI library templates rely on
        if (!TargetRunner.Run(root, config, target, out var targetError))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {targetError}");
            Console.ResetColor();
            return 1;
        }

        return Deploy(root, config, target) ? 0 : 1;
    }

    //Deploy copies the output to every configured host directory
    //This is what the old DeployModToHosts target did, it does nothing when no host is configured
    private static bool Deploy(string root, NcProject config, string target)
    {
        var ok = true;
        foreach (var entry in config.DeployTargets)
        {
            var directory = Path.GetFullPath(Path.Combine(root, entry));
            var destination = Path.Combine(directory, Path.GetFileName(target));

            try
            {
                //Leave identical files untouched, a changed timestamp would force the host to rebuild
                var existing = new FileInfo(destination);
                if (existing.Exists && existing.Length == new FileInfo(target).Length
                    && existing.LastWriteTimeUtc == File.GetLastWriteTimeUtc(target))
                    continue;

                Directory.CreateDirectory(directory);
                File.Copy(target, destination, overwrite: true);
                Console.WriteLine($"Deployed to {destination}");
            }
            catch (IOException e)
            {
                Console.WriteLine($"error: cannot deploy to {directory}: {e.Message}");
                ok = false;
            }
        }

        return ok;
    }

    //AssemblyNameOf the output name from the config, or the directory name when unset
    //The directory name matches what the template used, which keeps the old artifact name
    private static string AssemblyNameOf(string root, NcProject config)
        => string.IsNullOrWhiteSpace(config.Build.AssemblyName)
            ? new DirectoryInfo(root).Name
            : config.Build.AssemblyName;

    //HasCsproj whether the directory holds a project file
    private static bool HasCsproj(string directory)
        => Directory.EnumerateFiles(directory, "*.csproj").Any();

    //RunChecks runs the api usage and C# syntax and semantic checks before building
    //Compile settings follow the project config so a passing check does not fail later on a different language version
    private static bool RunChecks(string root, NcProject? config)
    {
        var bag = new DiagnosticBag();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        //The template directory source follows the project config, needed for mirror environments
        TemplateStore.Configure(config);
        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
            Console.WriteLine("warning: template catalog is unavailable, the mod api check is skipped");
        else
            covered = ModApiAnalyzer.Analyze(root, catalog, bag);

        var options = config is null ? CompileOptions.Default : CompileOptions.From(config);
        CSharpAnalyzer.Analyze(root, options, bag, covered);

        var diagnostics = bag.Sorted().ToList();
        if (diagnostics.Count > 0)
            DiagnosticRenderer.Render(diagnostics);

        if (!bag.HasErrors)
        {
            if (bag.WarningCount > 0)
                Console.WriteLine($"{bag.WarningCount} warning(s)");
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: build aborted, {bag.ErrorCount} error(s) found before compiling");
        Console.ResetColor();
        return false;
    }

    //RunDotnetBuild hands off to the real build with output straight to the console
    //Compile errors are already caught earlier, so a failure here is likely environmental and the raw output is more useful
    private static bool RunDotnetBuild(string root, string configuration)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
        };
        //ncm output is English only, keep the child process from following the system language
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.WriteLine("error: could not start dotnet");
            return false;
        }

        process.WaitForExit();
        if (process.ExitCode == 0)
            return true;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: dotnet build exited with code {process.ExitCode}");
        Console.ResetColor();
        return false;
    }

    //CollectOutput collects the build output into Build/ under the project root
    private static int CollectOutput(string root, string configuration)
    {
        var output = FindOutputDirectory(root, configuration);
        if (output is null)
        {
            Console.WriteLine($"error: no build output under bin/{configuration}");
            return 1;
        }

        var target = OutputPath(root);
        Directory.CreateDirectory(target);

        var copied = 0;
        foreach (var pattern in new[] { "*.dll", "*.pdb" })
        {
            foreach (var file in Directory.EnumerateFiles(output, pattern))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                copied++;
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Finished {configuration} build, {copied} file(s) copied to {target}");
        Console.ResetColor();
        return 0;
    }

    //FindOutputDirectory the bin/<configuration> level that holds the dlls
    //The target framework directory name varies with the TFM and is not hardcoded
    private static string? FindOutputDirectory(string root, string configuration)
    {
        var directory = Path.Combine(root, "bin", configuration);
        if (!Directory.Exists(directory))
            return null;

        return Directory.GetDirectories(directory)
            .FirstOrDefault(path => Directory.EnumerateFiles(path, "*.dll").Any());
    }
}
