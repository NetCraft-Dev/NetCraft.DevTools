using System.Diagnostics;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NetCraft.ModBuild.Tools;

//System.Diagnostics also defines a Trace, so name the project's own
using Trace = NetCraft.ModBuild.Core.Trace;

namespace NetCraft.ModBuild.Compile;

//Compile references for plain csproj projects, obtained by running msbuild's ResolveReferences instead of parsing the xml ourselves
//Assembly name resolution, package restore and project references are msbuild and nuget's job and reimplementing them only misses cases
//ncproj projects do not come through here, their references come from the config and Build directory
internal static class CsprojReferences
{
    //One resolve per project is enough since check and build both ask for it
    private static readonly Dictionary<string, IReadOnlyList<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    //Empty when there is no csproj or the engine is unavailable, which leaves the caller with framework references only
    public static IReadOnlyList<string> Assemblies(string root)
    {
        var project = Directory.EnumerateFiles(root, "*.csproj").FirstOrDefault();
        if (project is null)
            return [];

        if (Cache.TryGetValue(project, out var cached))
            return cached;

        var resolved = Resolve(project);
        Cache[project] = resolved;
        return resolved;
    }

    //Runs ResolveReferences once and collects the ReferencePath items
    private static IReadOnlyList<string> Resolve(string project)
    {
        if (!MsBuildLibrary.Attach(out var engineError))
        {
            Console.WriteLine($"warning: {engineError}, the references of {Path.GetFileName(project)} are skipped");
            return [];
        }

        var directory = Path.GetDirectoryName(project)!;
        //Restore first when the assets file is missing, otherwise ResolvePackageAssets fails outright
        if (!File.Exists(Path.Combine(directory, "obj", "project.assets.json")) && !Restore(directory))
            return [];

        try
        {
            TargetRunner.Sdks();
            var collection = new ProjectCollection();
            var loaded = new Project(project, globalProperties: null, toolsVersion: null, projectCollection: collection);
            //ReferencePath is only populated by a target run, so the instance is needed instead of the evaluated project
            var instance = loaded.CreateProjectInstance();
            if (!instance.Build("ResolveReferences", [new ConsoleLogger(LoggerVerbosity.Quiet)]))
            {
                Trace.Log($"ResolveReferences failed for {project}");
                return [];
            }

            var paths = new List<string>();
            foreach (var item in instance.GetItems("ReferencePath"))
            {
                if (File.Exists(item.EvaluatedInclude))
                    paths.Add(item.EvaluatedInclude);
            }

            Trace.Log($"csproj {project} resolved {paths.Count} reference(s)");
            return paths;
        }
        catch (Exception e)
        {
            Trace.Log($"cannot resolve the references of {project}: {e.GetType().Name}: {e.Message}");
            return [];
        }
    }

    //Restores through dotnet, which also prepares the output of referenced projects
    private static bool Restore(string directory)
    {
        Console.WriteLine($"Restoring {new DirectoryInfo(directory).Name} before resolving its references");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
        };
        //Keep child process output in English regardless of the system language
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.ArgumentList.Add("restore");
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

        Console.WriteLine($"error: dotnet restore exited with code {process.ExitCode}");
        return false;
    }
}
