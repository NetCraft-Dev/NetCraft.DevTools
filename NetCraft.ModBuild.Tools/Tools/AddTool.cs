using NetCraft.ModBuild.Core;
using NuGet.Versioning;

namespace NetCraft.ModBuild.Tools;

//AddTool adds a dependency or a reference to the project config
//nuget already works, mod waits for the cloud mod server
internal static class AddTool
{
    private const string NugetKind = "nuget";
    private const string ModKind = "mod";

    //FileKind lands in the <File> element of <References>
    private const string FileKind = "file";

    //ProjectKind lands in the <Project> element of <References>
    private const string ProjectKind = "project";

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("add", "Add a dependency or a reference to the project config", Run,
        [
            new($"{NugetKind} <id> [version]", "Add a nuget package, the version takes the same syntax as dotnet add package"),
            new($"{FileKind} <path>", "Add a managed dll as a compile reference, relative to the project root"),
            new($"{ProjectKind} <path>", "Add another project as a compile reference, built first and referenced by its output"),
            new($"{ModKind} <id> [version]", "Add a mod dependency, not implemented yet"),
        ]);

    //Run validates the arguments for the chosen kind and hands them to the config
    private static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Usage();
            return 1;
        }

        var kind = args[0];
        var value = args[1];

        if (string.Equals(kind, ModKind, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: {ModKind} dependencies are not implemented yet, they will come with the cloud mod server");
            return 1;
        }

        var isNuget = string.Equals(kind, NugetKind, StringComparison.OrdinalIgnoreCase);
        var isFile = string.Equals(kind, FileKind, StringComparison.OrdinalIgnoreCase);
        var isProject = string.Equals(kind, ProjectKind, StringComparison.OrdinalIgnoreCase);
        if (!isNuget && !isFile && !isProject)
        {
            Console.WriteLine($"error: unknown kind {kind}");
            Usage();
            return 1;
        }

        //Only nuget takes an optional version, the other kinds reject an extra argument
        var limit = isNuget ? 3 : 2;
        if (args.Length > limit)
        {
            Usage();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine(isNuget ? "error: a package id is required" : "error: a path is required");
            return 1;
        }

        var version = args.Length == 3 ? args[2] : string.Empty;

        //Validate the version here because a bad one would only surface at restore
        if (isNuget && version.Length > 0 && !VersionRange.TryParse(version, out _))
        {
            Console.WriteLine($"error: {version} is not a valid version or range");
            return 1;
        }

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        if (project is null)
        {
            Console.WriteLine($"error: no {NcProject.Extension} in this directory or any parent");
            return 1;
        }

        var added = isNuget
            ? project.AddPackage(value, version, out var error)
            : project.AddReference(value, isProject, out error);

        if (!added)
        {
            Console.WriteLine($"error: {error}");
            return 1;
        }

        if (!isNuget)
        {
            Console.WriteLine($"Added {(isProject ? "project" : "file")} {value} to {project.Path}");
            return 0;
        }

        Console.WriteLine(version.Length > 0
            ? $"Added {value} {version} to {project.Path}"
            : $"Added {value} to {project.Path}, the version is left open");
        Console.WriteLine("Run 'ncm restore' to fetch it");
        return 0;
    }

    //Usage prints the accepted forms when the arguments are wrong
    private static void Usage()
    {
        Console.WriteLine($"Usage: ncm add {NugetKind} <id> [version]");
        Console.WriteLine($"       ncm add {FileKind} <path>");
        Console.WriteLine($"       ncm add {ProjectKind} <path>");
        Console.WriteLine($"       ncm add {ModKind} <id> [version]");
    }
}
