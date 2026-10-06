using System.Xml;
using System.Xml.Linq;

namespace NetCraft.ModBuild.Core;

//NcCheck 语法检查的设置
public sealed class NcCheck
{
    //DefaultLangVersion 没写时按最新语言版本解析
    public const string DefaultLangVersion = "latest";

    public NcCheck(string? langVersion)
    {
        LangVersion = string.IsNullOrWhiteSpace(langVersion) ? DefaultLangVersion : langVersion;
    }

    //LangVersion 解析源码用的 C# 语言版本
    public string LangVersion { get; }
}

//NcBuild 编译相关的设置
public sealed class NcBuild
{
    //DefaultConfiguration 没写配置时按发布版编
    public const string DefaultConfiguration = "Release";

    //DefaultOutput 产物默认落在项目根的 Build/out 下
    public static readonly string DefaultOutput = ProjectLayout.Output;

    //DefaultOutputType 没写时编成类库 模组都是这种
    public const string DefaultOutputType = "Library";

    //ExeOutputType 编成控制台程序的那个名字
    public const string ExeOutputType = "Exe";

    //OutputTypes 认得的输出类型
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

    //AssemblyName 产物程序集名 空表示先看清单的 id 再看目录名
    public string AssemblyName { get; }

    //Configuration 构建配置名
    public string Configuration { get; }

    //Output 产物目录 相对项目根
    public string Output { get; }

    //ExtraArgs 追加给编译器的参数 原样传给 dotnet 那条旧路
    public string ExtraArgs { get; }

    //Nullable 可空引用类型检查
    public bool Nullable { get; }

    //ImplicitUsings 是否补隐式 using
    public bool ImplicitUsings { get; }

    //DefineConstants 编译期符号 分号分隔
    public string DefineConstants { get; }

    //OutputType 产物种类 见 OutputTypes
    public string OutputType { get; }

    //RootNamespace 根命名空间 空表示跟产物名走 只影响内嵌资源的默认名字
    public string RootNamespace { get; }

    //Normalized 输出类型大小写归一 认不出来退回类库
    private static string Normalized(string value)
        => OutputTypes.FirstOrDefault(type => string.Equals(type, value, StringComparison.OrdinalIgnoreCase))
            ?? DefaultOutputType;
}

//NcPackage 一条包依赖
public sealed record NcPackage(string Id, string Version);

//NcResource 一条要打进产物程序集的资源
//LogicalName 是资源名 不写就按根命名空间加相对路径算
public sealed record NcResource(string Include, string LogicalName);

//NcServer 开发期服务端的设置
//Cache 指定运行时文件缓存的落点 Args 是每次开服自动带上的参数 Debug 等于常开调试模式
public sealed class NcServer
{
    public NcServer(string cache, string args, bool debug)
    {
        Cache = cache;
        Args = args;
        Debug = debug;
    }

    //Cache 运行时缓存目录 空表示用 ncm 自带的那个
    public string Cache { get; }

    //Args 开服时自动追加的参数 空白分隔 双引号能裹住带空格的取值
    public string Args { get; }

    //Debug 常开调试模式 与命令行 --debug 同一件事
    public bool Debug { get; }
}

//NcClient 开发期客户端的设置
//开客户端那套还没做 这里先解析出来备用
public sealed class NcClient
{
    public NcClient(string args, string version, string jar)
    {
        Args = args;
        Version = version;
        Jar = jar;
    }

    //Args 启动客户端时自动追加的参数
    public string Args { get; }

    //Version 客户端 jar 的版本 空表示照内核版本走
    public string Version { get; }

    //Jar 客户端 jar 的直接下载地址 给了就不再查版本清单
    public string Jar { get; }
}

//NcSource 内核来源与解析规则
//Url 是基准地址 Index 是清单文件名 Format 决定清单怎么读 Pattern 只在 regex 下用
public sealed class NcSource
{
    //DefaultFormat 默认按 sha256sum 那种一行一条的格式读
    public const string DefaultFormat = "sha256-lines";

    //DefaultIndex 默认的清单文件名
    public const string DefaultIndex = "index.txt";

    //Formats 认得的解析格式
    public static readonly string[] Formats = [DefaultFormat, "plain", "regex"];

    public NcSource(string url, string index, string format, string pattern)
    {
        Url = url;
        Index = string.IsNullOrWhiteSpace(index) ? DefaultIndex : index;
        Format = string.IsNullOrWhiteSpace(format) ? DefaultFormat : format;
        Pattern = pattern;
    }

    //Url 内核文件的下载基准地址 空表示用内置那个
    public string Url { get; }

    //Index 清单文件名 相对基准地址
    public string Index { get; }

    //Format 清单的解析格式 见 Formats
    public string Format { get; }

    //Pattern regex 格式用的匹配式 两个捕获组依次是哈希与相对路径
    public string Pattern { get; }
}

//NcTemplate 模板目录的来源
//Url 是清单地址 Base 是示例文件的基准 两个都能带镜像前缀
//Base 不写就用清单里写的那个
public sealed class NcTemplate
{
    public NcTemplate(string url, string baseUrl)
    {
        Url = url;
        Base = baseUrl;
    }

    //Url 清单地址 空表示用内置那个
    public string Url { get; }

    //Base 示例文件基准 空表示照清单里写的来
    public string Base { get; }
}

//NcStepKind 任务步骤的种类
public enum NcStepKind
{
    //Exec 跑一条命令行
    Exec,

    //Copy 把匹配到的文件复制过去
    Copy,

    //Zip 把一个目录压成 zip
    Zip,
}

//NcStep 任务里的一个步骤
//Exec 用 Command 另两种用 From 与 To 用不到的字段留空
public sealed class NcStep
{
    private NcStep(NcStepKind kind, string command, string from, string to)
    {
        Kind = kind;
        Command = command;
        From = from;
        To = to;
    }

    //Exec 一条命令行
    public static NcStep Exec(string command) => new(NcStepKind.Exec, command, string.Empty, string.Empty);

    //Copy 一组文件复制到目标
    public static NcStep Copy(string from, string to) => new(NcStepKind.Copy, string.Empty, from, to);

    //Zip 一个目录压成 zip
    public static NcStep Zip(string from, string to) => new(NcStepKind.Zip, string.Empty, from, to);

    //Kind 步骤种类
    public NcStepKind Kind { get; }

    //Command Exec 的命令行
    public string Command { get; }

    //From 源 相对项目根
    public string From { get; }

    //To 目标 相对项目根
    public string To { get; }

    //Text 展示与指纹都用它
    public string Text => Kind switch
    {
        NcStepKind.Copy => $"copy {From} -> {To}",
        NcStepKind.Zip => $"zip {From} -> {To}",
        _ => Command,
    };
}

//NcTask 项目配置里的一个任务
//步骤按顺序跑 有前置就先把前置跑完
public sealed class NcTask
{
    //DefaultDescription 任务没写描述时帮助与列表里显示的占位文案
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

    //Name 任务名 命令行里直接拿它调用
    public string Name { get; }

    //Description 配置里写的一句话说明 没写就是空的
    public string Description { get; }

    //Depends 要先跑完的任务名
    public IReadOnlyList<string> Depends { get; }

    //Overrides 是否允许顶掉同名的内置工具
    public bool Overrides { get; }

    //Steps 按顺序执行的步骤
    public IReadOnlyList<NcStep> Steps { get; }

    //Title 展示用的说明 配置没写就退回默认文案
    public string Title => string.IsNullOrWhiteSpace(Description) ? DefaultDescription : Description;
}

//NcProject 项目里的 .ncproj 通用配置
//装的是开发期的东西 任务 运行参数 来源之类 不随模组分发
//与 ncmod.json 分得很清 那个是 mod 数据清单 要内嵌进 dll 跟模组走
public sealed class NcProject
{
    //Extension 配置文件后缀
    public const string Extension = ".ncproj";

    //SupportedVersion 当前认得的配置版本
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

    //Path 配置文件的完整路径
    public string Path { get; }

    //Directory 配置文件所在目录 也就是项目根
    public string Directory { get; }

    //Overrides 全局默认 任务自己没写 Override 时照它算
    public bool Overrides { get; }

    //Check 语法检查的设置
    public NcCheck Check { get; }

    //Build 编译相关的设置
    public NcBuild Build { get; }

    //Packages 声明的包依赖
    public IReadOnlyList<NcPackage> Packages { get; }

    //Tasks 配置里定义的全部任务 含被内置挡掉的那些
    public IReadOnlyList<NcTask> Tasks { get; }

    //Server 开发期服务端的设置
    public NcServer Server { get; }

    //Client 开发期客户端的设置
    public NcClient Client { get; }

    //Sources 内核来源 没配就是 null 用内置那个
    public NcSource? Sources { get; }

    //Template 模板目录来源 没配就是 null 用内置那个
    public NcTemplate? Template { get; }

    //InternalsVisibleTo 允许访问内部成员的程序集名
    public IReadOnlyList<string> InternalsVisibleTo { get; }

    //DeployTargets 构建后产物要复制过去的目录 相对项目根或绝对路径
    public IReadOnlyList<string> DeployTargets { get; }

    //AvaloniaResources 要打进 !AvaloniaResources 的资源模式 相对项目根
    public IReadOnlyList<string> AvaloniaResources { get; }

    //EmbeddedResources 要打进产物程序集的普通资源
    public IReadOnlyList<NcResource> EmbeddedResources { get; }

    //FileReferences 直接当编译引用的 dll 模式 相对项目根 支持通配
    public IReadOnlyList<string> FileReferences { get; }

    //ProjectReferences 要引用其产物的其他工程 相对项目根 可指目录也可指工程文件
    public IReadOnlyList<string> ProjectReferences { get; }

    //TryFind 从指定目录起逐级向上找配置文件 找不到返回 null
    //error 非空表示找到了但读不动 与压根没有是两回事
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

    //FindIn 在目录里找一个配置文件 同名多个时优先与目录同名的那个
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

    //AddPackage 往配置里加一条包依赖 同名的已经有了就换版本 返回是否成功
    //只碰 <Packages> 那一块 其余节点原样保留
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
        //没给版本就是不动已有的那个 与配置里别处留空不碰的规矩一致
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

    //AddReference 往配置里加一条引用 同类同路径的已经有了就不重复加 返回是否成功
    //isProject 为真写进 <Project> 否则写进 <File>
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

    //ToRelative 路径折成相对项目根的写法
    //带通配符的不做绝对化 那串东西本来就不是一个能解析出来的路径
    private string ToRelative(string include)
    {
        var normalized = include.Trim().Replace('\\', '/');
        if (normalized.IndexOfAny(['*', '?']) >= 0)
            return normalized.Replace('/', System.IO.Path.DirectorySeparatorChar);

        var absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, normalized));
        return System.IO.Path.GetRelativePath(Directory, absolute);
    }

    //FindTask 按名字精确找一个能用的任务 大小写敏感 没有返回 null
    //被内置工具挡掉的任务不在这里 调用方拿不到也就执行不了
    public NcTask? FindTask(string name)
        => Tasks.FirstOrDefault(task => !IsShadowed(task)
            && string.Equals(task.Name, name, StringComparison.Ordinal));

    //Runnable 归属本项目的可用任务 被内置挡掉的除外
    public IReadOnlyList<NcTask> Runnable()
        => Tasks.Where(task => !IsShadowed(task)).ToList();

    //Shadowed 名字撞上内置工具因而被忽略的任务
    public IReadOnlyList<NcTask> Shadowed()
        => Tasks.Where(IsShadowed).ToList();

    //IsShadowed 这个任务是否被同名的内置工具挡掉
    //没开 Override 时忽略 开了就让它顶掉内置
    public bool IsShadowed(NcTask task)
        => !task.Overrides && HasBuiltIn(task.Name);

    //HasBuiltIn 内置工具里有没有这个名字 大小写敏感
    private static bool HasBuiltIn(string name)
        => ToolRegistry.All.Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

    //Load 读一份配置 读不动时把原因写进 error
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

    //ReadEmbeddedResources 读 <EmbeddedResources> 下每条要内嵌的资源
    //LogicalName 可省 省了按根命名空间加相对路径算资源名
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

    //ReadReferences 读 <References> 下两类引用
    //File 是直接给的 dll 或模式 Project 是另一个工程 两种都相对项目根
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

    //ReadFriends 读 <InternalsVisibleTo> 下每个要开放内部成员的程序集
    private static void ReadFriends(XElement parent, string path, List<string> friends)
    {
        foreach (var element in parent.Elements())
        {
            //Name 是这边习惯的写法 Include 是照 csproj 搬过来的 两种都认
            var name = (string?)element.Attribute("Name") ?? (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                Warn($"an element under <InternalsVisibleTo> in {path} has no Name, skipped");
                continue;
            }

            friends.Add(name);
        }
    }

    //ReadResources 读 <AvaloniaResources> 下每个要打进资源包的模式
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

    //ReadServer 读 <Server> 下的开发期服务端设置
    private static NcServer ReadServer(XElement element, string path)
        => new(
            (string?)element.Attribute("Cache") ?? string.Empty,
            (string?)element.Attribute("Args") ?? string.Empty,
            ReadBool(element, "Debug", false, path));

    //ReadClient 读 <Client> 下的开发期客户端设置
    private static NcClient ReadClient(XElement element)
        => new(
            (string?)element.Attribute("Args") ?? string.Empty,
            (string?)element.Attribute("Version") ?? string.Empty,
            (string?)element.Attribute("Jar") ?? string.Empty);

    //ReadSources 读 <Sources> 内核来源 地址没写就当没配 用它不如用内置那个
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

    //ReadTemplate 读 <Template> 模板目录来源 两项都没写就当没配 用它不如用内置那个
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

    //ReadCheck 读 <Check> 下的检查设置
    private static NcCheck ReadCheck(XElement element)
        => new((string?)element.Attribute("LangVersion"));

    //ReadBuild 读 <Build> 下的编译设置
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

    //ReadPackages 读 <Packages> 下每一条包依赖
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

    //Folded 两个元素名是否只是大小写不同
    private static bool Folded(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    //ReadTasks 读 <Tasks> 下的每一个 <Task>
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

    //ReadSteps 读一个任务里的步骤
    //Exec 取元素文本 Copy 与 Zip 各取 From 与 To 别的类型还没做 见到只提醒一句
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

    //Split 拆分隔符隔开的属性值 空白与空项都丢掉
    //Depends 用逗号 路径列表用分号
    private static List<string> Split(string? value, char separator = ',')
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    //ReadBool 读一个真假属性 写得不认识就用兜底值并提醒
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

    //Warn 配置里能容忍的问题都走这里 黄色一行 不打断流程
    private static void Warn(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"warning: {message}");
        Console.ForegroundColor = previous;
    }
}
