using System.Xml;
using System.Xml.Linq;

namespace NetCraft.ModBuild.Core;

//C# syntax check settings for the project
public sealed class NcCheck
{
    //Language version used when the config omits one
    public const string DefaultLangVersion = "latest";

    public NcCheck(string? langVersion)
    {
        LangVersion = string.IsNullOrWhiteSpace(langVersion) ? DefaultLangVersion : langVersion;
    }

    //C# language version used to parse the sources
    public string LangVersion { get; }
}

//Build settings for the project
public sealed class NcBuild
{
    //Configuration used when the config omits one
    public const string DefaultConfiguration = "Release";

    //Default output directory, relative to the project root
    public static readonly string DefaultOutput = ProjectLayout.Output;

    //Output type used when the config omits one, which is what every mod uses
    public const string DefaultOutputType = "Library";

    //Output type name for a console executable
    public const string ExeOutputType = "Exe";

    public static readonly string[] OutputTypes = [DefaultOutputType, ExeOutputType];

    public NcBuild(string assemblyName, string configuration, string output, string extraArgs, bool nullable,
        bool implicitUsings, string defineConstants, string outputType, string rootNamespace)
    {
        AssemblyName = assemblyName;
        Configuration = string.IsNullOrWhiteSpace(configuration) ? DefaultConfiguration : configuration;
        Output = string.IsNullOrWhiteSpace(output) ? DefaultOutput : output;
        ExtraArgs = extraArgs;
        Nullable = nullable;
        ImplicitUsings = implicitUsings;
        DefineConstants = defineConstants;
        OutputType = Normalized(outputType);
        RootNamespace = rootNamespace;
    }

    //Assembly name, empty falls back to the manifest id and then to the directory name
    public string AssemblyName { get; }

    //Build configuration name
    public string Configuration { get; }

    //Output directory, relative to the project root
    public string Output { get; }

    //Extra arguments passed to the compiler invocation
    public string ExtraArgs { get; }

    //Nullable reference type checking
    public bool Nullable { get; }

    //Implicit usings
    public bool ImplicitUsings { get; }

    //Compile-time symbols, semicolon separated
    public string DefineConstants { get; }

    //Product kind, see OutputTypes
    public string OutputType { get; }

    //Root namespace, empty follows the assembly name and only affects the default resource names
    public string RootNamespace { get; }

    //Normalize an output type ignoring case, falling back to the library type
    private static string Normalized(string value)
        => OutputTypes.FirstOrDefault(type => string.Equals(type, value, StringComparison.OrdinalIgnoreCase))
            ?? DefaultOutputType;
}

//One package dependency
public sealed record NcPackage(string Id, string Version);

//A resource embedded into the output assembly
//The logical name defaults to the root namespace plus the relative path
public sealed record NcResource(string Include, string LogicalName);

//Development-time server settings
//Cache sets the runtime file cache location, Args are applied on every launch and Debug keeps debug mode always on
public sealed class NcServer
{
    public NcServer(string cache, string args, bool debug)
    {
        Cache = cache;
        Args = args;
        Debug = debug;
    }

    //Runtime cache directory, empty uses the one bundled with ncm
    public string Cache { get; }

    //Arguments applied on every launch, whitespace separated with double quotes around values containing spaces
    public string Args { get; }

    //Keeps debug mode always on, same as the --debug command line flag
    public bool Debug { get; }
}

//Development-time client settings
//The client launcher is not implemented yet, these are parsed ahead of time
public sealed class NcClient
{
    public NcClient(string args, string version, string jar)
    {
        Args = args;
        Version = version;
        Jar = jar;
    }

    //Arguments applied when launching the client
    public string Args { get; }

    //Client jar version, empty follows the kernel version
    public string Version { get; }

    //Direct download URL for the client jar, which skips the version manifest
    public string Jar { get; }
}

//Kernel source and index parsing rules
//Url is the base address, Index the index file name, Format how the index is read and Pattern only applies to the regex format
public sealed class NcSource
{
    //Default format reads one hash and path per line like sha256sum output
    public const string DefaultFormat = "sha256-lines";

    //Default index file name
    public const string DefaultIndex = "index.txt";

    //Index formats the loader understands
    public static readonly string[] Formats = [DefaultFormat, "plain", "regex"];

    public NcSource(string url, string index, string format, string pattern)
    {
        Url = url;
        Index = string.IsNullOrWhiteSpace(index) ? DefaultIndex : index;
        Format = string.IsNullOrWhiteSpace(format) ? DefaultFormat : format;
        Pattern = pattern;
    }

    //Download base address for kernel files, empty uses the built-in one
    public string Url { get; }

    //Index file name, relative to the base address
    public string Index { get; }

    //Index parsing format, see Formats
    public string Format { get; }

    //Pattern for the regex format, whose two capture groups are the hash and the relative path
    public string Pattern { get; }
}

//Source of the template directory
//Url is the index address and Base the sample file address, both accepting a mirror prefix
//Base defaults to whatever the index declares
public sealed class NcTemplate
{
    public NcTemplate(string url, string baseUrl)
    {
        Url = url;
        Base = baseUrl;
    }

    //Index address, empty uses the built-in one
    public string Url { get; }

    //Sample file base address, empty uses the one from the index
    public string Base { get; }
}

//Kind of a task step
public enum NcStepKind
{
    Exec,
    Copy,
    Zip,
}

//A step inside a task
//Exec uses Command while Copy and Zip use From and To, leaving the unused fields empty
public sealed class NcStep
{
    private NcStep(NcStepKind kind, string command, string from, string to)
    {
        Kind = kind;
        Command = command;
        From = from;
        To = to;
    }

    public static NcStep Exec(string command) => new(NcStepKind.Exec, command, string.Empty, string.Empty);

    public static NcStep Copy(string from, string to) => new(NcStepKind.Copy, string.Empty, from, to);

    public static NcStep Zip(string from, string to) => new(NcStepKind.Zip, string.Empty, from, to);

    //Kind of this step
    public NcStepKind Kind { get; }

    //Command line for the Exec kind
    public string Command { get; }

    //Source, relative to the project root
    public string From { get; }

    //Destination, relative to the project root
    public string To { get; }

    //Text used for display and fingerprinting
    public string Text => Kind switch
    {
        NcStepKind.Copy => $"copy {From} -> {To}",
        NcStepKind.Zip => $"zip {From} -> {To}",
        _ => Command,
    };
}

//A task declared in the project config
//Steps run in order, with dependencies completed first
public sealed class NcTask
{
    //Description shown in help and listings when a task omits one
    public const string DefaultDescription = "Project custom task";

    public NcTask(string name, string description, IReadOnlyList<string> depends, bool overrides,
        IReadOnlyList<NcStep> steps)
    {
        Name = name;
        Description = description;
        Depends = depends;
        Overrides = overrides;
        Steps = steps;
    }

    //Task name, used to invoke it from the command line
    public string Name { get; }

    //One-line description from the config, empty when omitted
    public string Description { get; }

    //Names of tasks that must finish first
    public IReadOnlyList<string> Depends { get; }

    //Whether this task may override a built-in tool of the same name
    public bool Overrides { get; }

    //Steps executed in order
    public IReadOnlyList<NcStep> Steps { get; }

    //Display description, falling back to the default text
    public string Title => string.IsNullOrWhiteSpace(Description) ? DefaultDescription : Description;
}

//The .ncproj development config of a project
//Holds development-time items such as tasks, run arguments and sources, and is never shipped with a mod
//Distinct from ncmod.json, the mod data manifest embedded into the dll
public sealed class NcProject
{
    public const string Extension = ".ncproj";

    //Config version this build understands
    private const string SupportedVersion = "1";

    private NcProject(string path, bool overrides, NcCheck check, NcBuild build,
        IReadOnlyList<NcPackage> packages, IReadOnlyList<NcTask> tasks, NcServer server, NcClient client,
        NcSource? sources, NcTemplate? template, IReadOnlyList<string> friends, IReadOnlyList<string> deploy,
        IReadOnlyList<string> resources, IReadOnlyList<NcResource> embedded, IReadOnlyList<string> files,
        IReadOnlyList<string> projects)
    {
        Path = path;
        Directory = System.IO.Path.GetDirectoryName(path)!;
        Overrides = overrides;
        Check = check;
        Build = build;
        Packages = packages;
        Tasks = tasks;
        Server = server;
        Client = client;
        Sources = sources;
        Template = template;
        InternalsVisibleTo = friends;
        DeployTargets = deploy;
        AvaloniaResources = resources;
        EmbeddedResources = embedded;
        FileReferences = files;
        ProjectReferences = projects;
    }

    //Full path of the config file
    public string Path { get; }

    //Directory containing the config file, which is the project root
    public string Directory { get; }

    //Global default applied to tasks that do not set Override themselves
    public bool Overrides { get; }

    //Syntax check settings
    public NcCheck Check { get; }

    //Build settings
    public NcBuild Build { get; }

    //Declared package dependencies
    public IReadOnlyList<NcPackage> Packages { get; }

    //All tasks defined in the config, including those shadowed by built-ins
    public IReadOnlyList<NcTask> Tasks { get; }

    //Development-time server settings
    public NcServer Server { get; }

    //Development-time client settings
    public NcClient Client { get; }

    //Kernel source, null uses the built-in one
    public NcSource? Sources { get; }

    //Template directory source, null uses the built-in one
    public NcTemplate? Template { get; }

    //Assemblies allowed to access internal members
    public IReadOnlyList<string> InternalsVisibleTo { get; }

    //Directories the build output is copied to, relative to the project root or absolute
    public IReadOnlyList<string> DeployTargets { get; }

    //Patterns packed into !AvaloniaResources, relative to the project root
    public IReadOnlyList<string> AvaloniaResources { get; }

    //Plain resources embedded into the output assembly
    public IReadOnlyList<NcResource> EmbeddedResources { get; }

    //dll patterns referenced directly at compile time, relative to the project root and supporting wildcards
    public IReadOnlyList<string> FileReferences { get; }

    //Other projects whose output is referenced, relative to the project root and either a directory or a project file
    public IReadOnlyList<string> ProjectReferences { get; }

    //Search upward from the given directory for a project config, returning null when none is found
    //A non-empty error means one was found but could not be read, which is different from having none
    public static NcProject? TryFind(string directory, out string error)
    {
        error = string.Empty;
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var candidate = FindIn(current.FullName);
            if (candidate is not null)
            {
                Trace.Log($"found project config {candidate}");
                return Load(candidate, out error);
            }
        }

        Trace.Log($"no {Extension} from {directory} upward");
        return null;
    }

    //Find a config in the directory, preferring the one matching the directory name when several exist
    private static string? FindIn(string directory)
    {
        var files = System.IO.Directory.EnumerateFiles(directory, "*" + Extension).ToList();
        if (files.Count == 0)
            return null;

        if (files.Count == 1)
            return files[0];

        var preferred = System.IO.Path.Combine(directory, new DirectoryInfo(directory).Name + Extension);
        return files.FirstOrDefault(file => string.Equals(file, preferred, StringComparison.OrdinalIgnoreCase))
            ?? files[0];
    }

    //Add a package dependency, replacing the version when the id already exists, and return whether it succeeded
    //Only the <Packages> element is touched, leaving every other node unchanged
    public bool AddPackage(string id, string version, out string error)
    {
        error = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Load(Path);
        }
        catch (XmlException e)
        {
            error = $"{Path} is not valid xml: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = $"{Path} has no root element";
            return false;
        }

        var packages = root.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "Packages"));
        if (packages is null)
        {
            packages = new XElement("Packages");
            root.Add(packages);
        }

        var existing = packages.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "Package")
            && string.Equals((string?)element.Attribute("Id"), id, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var package = new XElement("Package", new XAttribute("Id", id));
            if (!string.IsNullOrWhiteSpace(version))
                package.SetAttributeValue("Version", version);
            packages.Add(package);
        }
        //A missing version leaves the existing entry untouched, matching the rule that a blank field never changes anything
        else if (!string.IsNullOrWhiteSpace(version))
        {
            existing.SetAttributeValue("Version", version);
        }

        try
        {
            XmlFile.Save(Path, document);
        }
        catch (IOException e)
        {
            error = $"cannot write {Path}: {e.Message}";
            return false;
        }

        return true;
    }

    //Add a reference, skipping an existing one with the same kind and path, and return whether it succeeded
    //isProject writes into <Project> and otherwise into <File>
    public bool AddReference(string include, bool isProject, out string error)
    {
        error = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Load(Path);
        }
        catch (XmlException e)
        {
            error = $"{Path} is not valid xml: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = $"{Path} has no root element";
            return false;
        }

        var references = root.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "References"));
        if (references is null)
        {
            references = new XElement("References");
            root.Add(references);
        }

        var name = isProject ? "Project" : "File";
        var relative = ToRelative(include);
        var existing = references.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, name)
            && string.Equals((string?)element.Attribute("Include"), relative, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            references.Add(new XElement(name, new XAttribute("Include", relative)));

        try
        {
            XmlFile.Save(Path, document);
        }
        catch (IOException e)
        {
            error = $"cannot write {Path}: {e.Message}";
            return false;
        }

        return true;
    }

    //Convert a path to be relative to the project root
    //Wildcards are left as is because they are not a resolvable path in the first place
    private string ToRelative(string include)
    {
        var normalized = include.Trim().Replace('\\', '/');
        if (normalized.IndexOfAny(['*', '?']) >= 0)
            return normalized.Replace('/', System.IO.Path.DirectorySeparatorChar);

        var absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, normalized));
        return System.IO.Path.GetRelativePath(Directory, absolute);
    }

    //Find a runnable task by name, case sensitively, returning null when there is none
    //Tasks shadowed by a built-in tool are excluded so callers cannot reach them
    public NcTask? FindTask(string name)
        => Tasks.FirstOrDefault(task => !IsShadowed(task)
            && string.Equals(task.Name, name, StringComparison.Ordinal));

    //Tasks defined by this project that can run, excluding those shadowed by built-ins
    public IReadOnlyList<NcTask> Runnable()
        => Tasks.Where(task => !IsShadowed(task)).ToList();

    //Tasks ignored because their name collides with a built-in tool
    public IReadOnlyList<NcTask> Shadowed()
        => Tasks.Where(IsShadowed).ToList();

    //Whether this task is shadowed by a built-in tool of the same name
    //Without Override it is ignored, with Override it replaces the built-in
    public bool IsShadowed(NcTask task)
        => !task.Overrides && HasBuiltIn(task.Name);

    //Whether a built-in tool has this name, compared case sensitively
    private static bool HasBuiltIn(string name)
        => ToolRegistry.All.Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

    //Read a config, writing the reason into error when it cannot be loaded
    private static NcProject? Load(string path, out string error)
    {
        error = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException e)
        {
            error = $"{path} is not valid xml: {e.Message}";
            return null;
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "ncproj", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{path} must have a <ncproj> root element";
            return null;
        }

        var version = (string?)root.Attribute("version");
        if (!string.IsNullOrEmpty(version) && !string.Equals(version, SupportedVersion, StringComparison.Ordinal))
            Warn($"{path} declares version {version}, this ncm only knows {SupportedVersion}");

        var overrides = ReadBool(root, "Override", false, path);
        var tasks = new List<NcTask>();
        var packages = new List<NcPackage>();
        var check = new NcCheck(string.Empty);
        var build = new NcBuild(string.Empty, string.Empty, string.Empty, string.Empty, true, true, string.Empty,
            string.Empty, string.Empty);
        var server = new NcServer(string.Empty, string.Empty, false);
        var client = new NcClient(string.Empty, string.Empty, string.Empty);
        NcSource? sources = null;
        NcTemplate? template = null;
        var friends = new List<string>();
        var deploy = new List<string>();
        var resources = new List<string>();
        var embedded = new List<NcResource>();
        var files = new List<string>();
        var projects = new List<string>();

        foreach (var element in root.Elements())
        {
            var name = element.Name.LocalName;
            if (Folded(name, "Tasks"))
                ReadTasks(element, overrides, path, tasks);
            else if (Folded(name, "Check"))
                check = ReadCheck(element);
            else if (Folded(name, "Build"))
                build = ReadBuild(element, path);
            else if (Folded(name, "Packages"))
                ReadPackages(element, path, packages);
            else if (Folded(name, "Server"))
                server = ReadServer(element, path);
            else if (Folded(name, "Client"))
                client = ReadClient(element);
            else if (Folded(name, "Sources"))
                sources = ReadSources(element, path);
            else if (Folded(name, "Template"))
                template = ReadTemplate(element, path);
            else if (Folded(name, "InternalsVisibleTo"))
                ReadFriends(element, path, friends);
            else if (Folded(name, "Deploy"))
                deploy.AddRange(Split((string?)element.Attribute("To"), ';'));
            else if (Folded(name, "AvaloniaResources"))
                ReadResources(element, path, resources);
            else if (Folded(name, "EmbeddedResources"))
                ReadEmbeddedResources(element, path, embedded);
            else if (Folded(name, "References"))
                ReadReferences(element, path, files, projects);
            else
                Warn($"unknown element <{name}> in {path}, ignored");
        }

        return new NcProject(path, overrides, check, build, packages, tasks, server, client, sources,
            template, friends, deploy, resources, embedded, files, projects);
    }

    //Read each embedded resource under <EmbeddedResources>
    //LogicalName is optional and defaults to the root namespace plus the relative path
    private static void ReadEmbeddedResources(XElement parent, string path, List<NcResource> resources)
    {
        foreach (var element in parent.Elements())
        {
            if (!Folded(element.Name.LocalName, "Resource"))
            {
                Warn($"unknown element <{element.Name.LocalName}> under <EmbeddedResources> in {path}, ignored");
                continue;
            }

            var include = (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(include))
            {
                Warn($"a <Resource> under <EmbeddedResources> in {path} has no Include, skipped");
                continue;
            }

            resources.Add(new NcResource(include, (string?)element.Attribute("LogicalName") ?? string.Empty));
        }
    }

    //Read the two kinds of reference under <References>
    //File is a dll or pattern and Project another project, both relative to the project root
    private static void ReadReferences(XElement parent, string path, List<string> files, List<string> projects)
    {
        foreach (var element in parent.Elements())
        {
            var include = (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(include))
            {
                Warn($"a <{element.Name.LocalName}> under <References> in {path} has no Include, skipped");
                continue;
            }

            if (Folded(element.Name.LocalName, "File"))
                files.Add(include);
            else if (Folded(element.Name.LocalName, "Project"))
                projects.Add(include);
            else
                Warn($"unknown element <{element.Name.LocalName}> under <References> in {path}, ignored");
        }
    }

    //Read each assembly granted internal access under <InternalsVisibleTo>
    private static void ReadFriends(XElement parent, string path, List<string> friends)
    {
        foreach (var element in parent.Elements())
        {
            //Both Name, the convention here, and Include, carried over from csproj, are accepted
            var name = (string?)element.Attribute("Name") ?? (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                Warn($"an element under <InternalsVisibleTo> in {path} has no Name, skipped");
                continue;
            }

            friends.Add(name);
        }
    }

    //Read each pattern packed into the resource bundle under <AvaloniaResources>
    private static void ReadResources(XElement parent, string path, List<string> resources)
    {
        foreach (var element in parent.Elements())
        {
            var include = (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(include))
            {
                Warn($"an element under <AvaloniaResources> in {path} has no Include, skipped");
                continue;
            }

            resources.Add(include);
        }
    }

    //Read the development-time server settings under <Server>
    private static NcServer ReadServer(XElement element, string path)
        => new(
            (string?)element.Attribute("Cache") ?? string.Empty,
            (string?)element.Attribute("Args") ?? string.Empty,
            ReadBool(element, "Debug", false, path));

    //Read the development-time client settings under <Client>
    private static NcClient ReadClient(XElement element)
        => new(
            (string?)element.Attribute("Args") ?? string.Empty,
            (string?)element.Attribute("Version") ?? string.Empty,
            (string?)element.Attribute("Jar") ?? string.Empty);

    //Read the kernel source under <Sources>, treating a missing address as unconfigured since the built-in source is better
    private static NcSource? ReadSources(XElement element, string path)
    {
        var url = (string?)element.Attribute("Url") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            Warn($"<Sources> in {path} has no Url, the built-in source is used");
            return null;
        }

        var format = (string?)element.Attribute("Format") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(format) && !NcSource.Formats.Contains(format, StringComparer.OrdinalIgnoreCase))
            Warn($"unknown Format=\"{format}\" in {path}, known ones are {string.Join(", ", NcSource.Formats)}");

        var pattern = (string?)element.Attribute("Pattern") ?? string.Empty;
        if (string.Equals(format, "regex", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(pattern))
            Warn($"Format=\"regex\" in {path} needs a Pattern, the built-in source is used");

        return new NcSource(url, (string?)element.Attribute("Index") ?? string.Empty, format, pattern);
    }

    //Read the template source under <Template>, treating both fields being absent as unconfigured since the built-in source is better
    private static NcTemplate? ReadTemplate(XElement element, string path)
    {
        var url = (string?)element.Attribute("Url") ?? string.Empty;
        var baseUrl = (string?)element.Attribute("Base") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(baseUrl))
        {
            Warn($"<Template> in {path} has neither Url nor Base, the built-in source is used");
            return null;
        }

        return new NcTemplate(url, baseUrl);
    }

    //Read the check settings under <Check>
    private static NcCheck ReadCheck(XElement element)
        => new((string?)element.Attribute("LangVersion"));

    //Read the build settings under <Build>
    private static NcBuild ReadBuild(XElement element, string path)
    {
        var outputType = (string?)element.Attribute("OutputType") ?? string.Empty;
        if (outputType.Length > 0 && !NcBuild.OutputTypes.Contains(outputType, StringComparer.OrdinalIgnoreCase))
            Warn($"unknown OutputType=\"{outputType}\" in {path}, known ones are {string.Join(", ", NcBuild.OutputTypes)}");

        return new NcBuild(
            (string?)element.Attribute("AssemblyName") ?? string.Empty,
            (string?)element.Attribute("Configuration") ?? string.Empty,
            (string?)element.Attribute("Output") ?? string.Empty,
            (string?)element.Attribute("ExtraArgs") ?? string.Empty,
            ReadBool(element, "Nullable", true, path),
            ReadBool(element, "ImplicitUsings", true, path),
            (string?)element.Attribute("DefineConstants") ?? string.Empty,
            outputType,
            (string?)element.Attribute("RootNamespace") ?? string.Empty);
    }

    //Read each package dependency under <Packages>
    private static void ReadPackages(XElement parent, string path, List<NcPackage> packages)
    {
        foreach (var element in parent.Elements())
        {
            if (!Folded(element.Name.LocalName, "Package"))
            {
                Warn($"unknown element <{element.Name.LocalName}> under <Packages> in {path}, ignored");
                continue;
            }

            var id = (string?)element.Attribute("Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                Warn($"a <Package> in {path} has no Id, skipped");
                continue;
            }

            packages.Add(new NcPackage(id, (string?)element.Attribute("Version") ?? string.Empty));
        }
    }

    //Whether two element names differ only in case
    private static bool Folded(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    //Read each <Task> under <Tasks>
    private static void ReadTasks(XElement parent, bool globalOverride, string path, List<NcTask> tasks)
    {
        foreach (var element in parent.Elements())
        {
            if (!string.Equals(element.Name.LocalName, "Task", StringComparison.OrdinalIgnoreCase))
            {
                Warn($"unknown element <{element.Name.LocalName}> under <Tasks> in {path}, ignored");
                continue;
            }

            var name = (string?)element.Attribute("Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                Warn($"a <Task> in {path} has no Name, skipped");
                continue;
            }

            if (tasks.Any(task => string.Equals(task.Name, name, StringComparison.Ordinal)))
            {
                Warn($"task {name} is declared more than once in {path}, the later one is ignored");
                continue;
            }

            var description = (string?)element.Attribute("Description") ?? string.Empty;
            var depends = Split((string?)element.Attribute("Depends"));
            var overrides = ReadBool(element, "Override", globalOverride, path);
            tasks.Add(new NcTask(name, description, depends, overrides, ReadSteps(element, name, path)));
        }
    }

    //Read the steps of a task
    //Exec takes the element text while Copy and Zip take From and To, and unsupported kinds only produce a warning
    private static List<NcStep> ReadSteps(XElement task, string name, string path)
    {
        var steps = new List<NcStep>();
        foreach (var step in task.Elements())
        {
            var kind = step.Name.LocalName;
            if (string.Equals(kind, "Exec", StringComparison.OrdinalIgnoreCase))
            {
                var text = step.Value.Trim();
                if (text.Length > 0)
                    steps.Add(NcStep.Exec(text));
                continue;
            }

            if (!string.Equals(kind, "Copy", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(kind, "Zip", StringComparison.OrdinalIgnoreCase))
            {
                Warn($"step <{kind}> in task {name} is not supported yet, ignored");
                continue;
            }

            var from = (string?)step.Attribute("From") ?? string.Empty;
            var to = (string?)step.Attribute("To") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                Warn($"<{kind}> in task {name} needs both From and To, skipped");
                continue;
            }

            steps.Add(string.Equals(kind, "Copy", StringComparison.OrdinalIgnoreCase)
                ? NcStep.Copy(from, to)
                : NcStep.Zip(from, to));
        }

        return steps;
    }

    //Split a separator-delimited attribute value, dropping whitespace and empty entries
    //Depends uses commas while path lists use semicolons
    private static List<string> Split(string? value, char separator = ',')
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    //Read a boolean attribute, warning and using the fallback when the value is unrecognized
    private static bool ReadBool(XElement element, string name, bool fallback, string path)
    {
        var raw = (string?)element.Attribute(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (bool.TryParse(raw, out var value))
            return value;

        Warn($"{name}=\"{raw}\" in {path} is not a boolean, using {fallback.ToString().ToLowerInvariant()}");
        return fallback;
    }

    //Tolerable config problems are reported here in yellow without aborting
    private static void Warn(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"warning: {message}");
        Console.ForegroundColor = previous;
    }
}
