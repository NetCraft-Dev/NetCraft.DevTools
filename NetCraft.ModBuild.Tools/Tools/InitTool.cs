using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//InitTool creates a new mod from the project template and generates the icon along the way
internal static class InitTool
{
    private const string TemplateShortName = "ncm";

    //Register this tool with its name, description and parameters
    //The positional arguments follow the form order, and no arguments at all starts the interactive prompt
    public static void Register()
        => ToolRegistry.Register("init", "Create a new NetCraft mod project in a subdirectory", Run,
        [
            new("<name>", "Mod name, required"),
            new("[id]", "Mod id, derived from the name when omitted"),
            new("[description]", "Mod description"),
            new("[authors]", "Comma separated author names"),
            new("[homepage]", "Homepage url"),
            new("[sources]", "Source repository url"),
            new("[license]", "License name"),
        ]);

    //Run collects the content and materializes it
    //No arguments starts the form, arguments are taken positionally in the same order without asking
    private static int Run(string[] args)
    {
        var directory = Environment.CurrentDirectory;
        var fields = BuildFields(directory);

        if (args.Length > 0)
        {
            if (args.Length > fields.Count)
            {
                Console.WriteLine($"init takes at most {fields.Count} arguments: {string.Join(' ', fields.Select(field => field.Key))}");
                return 1;
            }

            //This path is for scripts where nobody sees a confirmation, so an existing project fails outright to avoid damage
            if (HasExistingProject(directory))
            {
                Console.WriteLine($"This directory already contains a csproj or {ModProject.ManifestName} file");
                return 1;
            }

            for (var index = 0; index < args.Length; index++)
                fields[index].Value = args[index];
        }
        else
        {
            if (HasExistingProject(directory) && !Prompt.Confirm(
                    $"This directory already contains a csproj or {ModProject.ManifestName} file. Creating here may affect the existing project. Continue?",
                    defaultYes: false, warn: true))
                return 0;

            if (!Prompt.Form(fields))
            {
                Console.WriteLine("A mod name is required");
                return 1;
            }
        }

        return Submit(directory, fields);
    }

    //BuildFields the form fields in positional argument order, changing one place keeps both in sync
    private static List<FormField> BuildFields(string directory) => new()
    {
        //Only the name is required, the rest may stay empty and fall back to the template or defaults
        //The name is validated on every keystroke, an existing directory is an error and invalid characters a warning
        new("name", "Mod name", required: true)
        {
            Validate = value => ValidateName(directory, value),
        },
        new("id", "Mod id"),
        new("description", "Description"),
        new("authors", "Authors"),
        new("homepage", "Homepage"),
        new("sources", "Sources"),
        new("license", "License"),
    };

    //Submit the shared tail of both entry points, validating the name and directory before materializing
    private static int Submit(string directory, List<FormField> fields)
    {
        string Value(string key) => fields.First(field => field.Key == key).Value.Trim();

        var name = Value("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("A mod name is required");
            return 1;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Console.WriteLine("The mod name contains characters that cannot be used in a directory name");
            return 1;
        }

        //The interactive path is guarded by form highlighting, this argument path has no watcher so the check moves here
        if (Directory.Exists(Path.Combine(directory, name)))
        {
            Console.WriteLine($"A directory named {name} already exists");
            return 1;
        }

        var id = Value("id");
        var answers = new Answers(
            name,
            string.IsNullOrWhiteSpace(id) ? DeriveId(name) : id,
            Value("description"),
            Value("authors"),
            Value("homepage"),
            Value("sources"),
            Value("license"));

        return Execute(directory, answers);
    }

    //Execute creates the project, fills in the manifest fields, deletes the template icon and draws a new one
    //Separated from collection so this chain can run without interaction
    private static int Execute(string directory, Answers answers)
    {
        if (!RunTemplate(directory, answers.Name))
            return 1;

        var target = Path.Combine(directory, answers.Name);
        if (!File.Exists(Path.Combine(target, ModProject.ManifestName)))
        {
            Console.WriteLine($"Project created but {ModProject.ManifestName} was not found in {target}");
            return 1;
        }

        var project = ModProject.TryFind(target);
        if (project is null)
        {
            Console.WriteLine($"Project created but {ModProject.ManifestName} could not be read in {target}");
            return 1;
        }

        ApplyAnswers(project, answers);

        //The manifest just changed so the display name is re-read, the icon text uses it
        var refreshed = ModProject.TryFind(target) ?? project;

        //The template icon is a placeholder, replaced by one generated from the mod name
        if (File.Exists(refreshed.IconPath))
            File.Delete(refreshed.IconPath);
        IconTool.Generate(refreshed);

        Console.WriteLine($"Created {target}");
        return 0;
    }

    //RunTemplate runs dotnet new and surfaces dotnet's own output on failure
    private static bool RunTemplate(string directory, string name)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        //ncm output is English only, keep the child process from following the system language
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add(TemplateShortName);
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(name);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            PrintTemplateError();
            return false;
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0)
            return true;

        if (!string.IsNullOrWhiteSpace(output))
            Console.WriteLine(output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(error))
            Console.WriteLine(error.TrimEnd());

        PrintTemplateError();
        return false;
    }

    //PrintTemplateError the one message for any failure, almost always a missing template
    private static void PrintTemplateError()
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: the '{TemplateShortName}' project template is not installed. Install it and try again.");
        Console.ResetColor();
    }

    //HasExistingProject whether the directory already holds a project file or a mod manifest
    private static bool HasExistingProject(string directory)
        => Directory.EnumerateFiles(directory, "*.csproj").Any()
            || File.Exists(Path.Combine(directory, ModProject.ManifestName));

    //BadNameChars characters not allowed in a directory name, including both quotes since pasted names often carry them
    private static readonly char[] BadNameChars = BuildBadNameChars();

    //BuildBadNameChars the platform's invalid characters plus the two quotes
    private static char[] BuildBadNameChars()
    {
        var chars = new List<char>(Path.GetInvalidFileNameChars()) { '\'', '"' };
        return chars.ToArray();
    }

    //ValidateName live validation of the name, an existing directory is an error, special characters a warning and empty is neutral
    private static FieldState ValidateName(string directory, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return FieldState.Normal;
        if (Directory.Exists(Path.Combine(directory, value)))
            return FieldState.Error;
        return value.IndexOfAny(BadNameChars) >= 0 ? FieldState.Warn : FieldState.Normal;
    }

    //DeriveId derives the id from the name, aligned with the template's lowerCase rule and folding spaces and symbols into hyphens
    private static string DeriveId(string name)
    {
        var builder = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    //ApplyAnswers writes the filled fields into the manifest and leaves empty ones, keeping the template defaults
    private static void ApplyAnswers(ModProject project, Answers answers)
        => project.Edit(manifest =>
        {
            var changed = false;
            //The template already sets name from -n, but older templates lack the field, so set it to make the icon text use the display name
            changed |= SetString(manifest, "name", answers.Name);
            changed |= SetString(manifest, "id", answers.Id);
            changed |= SetString(manifest, "description", answers.Description);
            changed |= SetString(manifest, "license", answers.License);
            changed |= SetAuthors(manifest, answers.Authors);
            changed |= SetContact(manifest, "homepage", answers.Homepage);
            changed |= SetContact(manifest, "sources", answers.Sources);
            return changed;
        });

    //SetString overwrites only when there is a new value
    private static bool SetString(JsonObject manifest, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        manifest[key] = value;
        return true;
    }

    //SetAuthors splits a comma separated list into an array
    private static bool SetAuthors(JsonObject manifest, string value)
    {
        var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
            return false;

        var array = new JsonArray();
        foreach (var author in names)
            array.Add(author);
        manifest["authors"] = array;
        return true;
    }

    //SetContact the contact info is a nested object, created when missing
    private static bool SetContact(JsonObject manifest, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (manifest["contact"] is not JsonObject contact)
        {
            contact = new JsonObject();
            manifest["contact"] = contact;
        }

        contact[key] = value;
        return true;
    }

    //Answers what the form collected
    private sealed record Answers(
        string Name,
        string Id,
        string Description,
        string Authors,
        string Homepage,
        string Sources,
        string License);
}
