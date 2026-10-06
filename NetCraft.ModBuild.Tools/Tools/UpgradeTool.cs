using System.Xml;
using System.Xml.Linq;
using NetCraft.ModBuild.Core;
using TargetFramework = NetCraft.ModBuild.Compile.TargetFramework;

namespace NetCraft.ModBuild.Tools;

//UpgradeTool 把 csproj 那种模组项目迁成 .ncproj
//原 csproj 改名备份 ncm 认不出的属性逐条列出来让人自己处理
internal static class UpgradeTool
{
    //BackupExtension 原文件改名后的后缀
    private const string BackupExtension = ".bak";

    //DependencyTargetName 模板里负责内嵌依赖的那个目标 ncm 已经接手 见到不必提示处理
    private const string DependencyTargetName = "EmbedDependencies";

    //DeployTargetName 模板里负责把产物送到宿主目录的那个目标
    private const string DeployTargetName = "DeployModToHosts";

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("upgrade", "Convert a csproj based mod project to a .ncproj one", Run,
        [
            new("[file]", "The csproj to convert, defaults to the only csproj in the current directory"),
        ]);

    //Run 找源文件 读一遍 写出 .ncproj 再把原件改名
    private static int Run(string[] args)
    {
        if (args.Length > 1)
        {
            Console.WriteLine("Usage: ncm upgrade [file]");
            return 1;
        }

        var source = args.Length == 1 ? Path.GetFullPath(args[0]) : SingleCsproj(Environment.CurrentDirectory);
        if (source is null)
            return 1;

        if (!File.Exists(source))
        {
            Console.WriteLine($"error: {source} does not exist");
            return 1;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(source);
        }
        catch (XmlException e)
        {
            Console.WriteLine($"error: {source} is not valid xml: {e.Message}");
            return 1;
        }

        if (document.Root is not { } root || !string.Equals(root.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: {source} has no <Project> root element");
            return 1;
        }

        //同名的那份已经在就停下 覆盖掉别人手写的配置比报个错糟糕得多
        var target = Path.ChangeExtension(source, NcProject.Extension);
        if (File.Exists(target))
        {
            Console.WriteLine($"error: {Path.GetFileName(target)} already exists, move it away first");
            return 1;
        }

        var migration = Read(root);
        Console.WriteLine($"Converting {Path.GetFileName(source)}");

        try
        {
            XmlFile.Save(target, Compose(migration));
            File.Move(source, source + BackupExtension, overwrite: true);
        }
        catch (IOException e)
        {
            Console.WriteLine($"error: {e.Message}");
            return 1;
        }

        Report(migration, source, target);
        return 0;
    }

    //SingleCsproj 目录里唯一那个 csproj 有多个或一个都没有就报错
    private static string? SingleCsproj(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.csproj").ToList();
        if (files.Count == 1)
            return files[0];

        Console.WriteLine(files.Count == 0
            ? "error: no .csproj in this directory, pass one explicitly"
            : "error: more than one .csproj in this directory, pass the one to convert");
        return null;
    }

    //Read 把工程里能对上的东西挑出来 对不上的分门别类记下
    private static Migration Read(XElement root)
    {
        var migration = new Migration();

        foreach (var group in root.Elements().Where(element => Folded(element, "PropertyGroup")))
        {
            foreach (var property in group.Elements())
                ReadProperty(property, migration);
        }

        foreach (var group in root.Elements().Where(element => Folded(element, "ItemGroup")))
        {
            foreach (var item in group.Elements())
                ReadItem(item, migration);
        }

        foreach (var target in root.Elements().Where(element => Folded(element, "Target")))
        {
            var name = (string?)target.Attribute("Name") ?? string.Empty;
            if (string.Equals(name, DependencyTargetName, StringComparison.Ordinal))
                migration.Handled.Add($"<Target Name=\"{name}\"> (ncm embeds the dependencies itself)");
            else if (string.Equals(name, DeployTargetName, StringComparison.Ordinal))
                ReadDeployTarget(target, migration);
            else
                migration.Unhandled.Add($"<Target Name=\"{name}\">");
        }

        return migration;
    }

    //ReadDeployTarget 把产物复制到宿主目录的那条目标
    //宿主写死在工程里就照搬 靠命令行传的那种取不到值 提示改用 <Deploy>
    private static void ReadDeployTarget(XElement target, Migration migration)
    {
        var hosts = target.Descendants()
            .Where(element => Folded(element, "ModHostDir"))
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .Where(value => value.Length > 0 && !value.Contains("$(", StringComparison.Ordinal))
            .ToList();

        if (hosts.Count == 0)
        {
            migration.Unhandled.Add(
                $"<Target Name=\"{DeployTargetName}\"> (ncm does the same, list the host mods directories in <Deploy To=\"...\">)");
            return;
        }

        migration.Deploy.AddRange(hosts);
        migration.Migrated.Add($"Deploy {string.Join(';', hosts)}");
    }

    //ReadProperty 认一个属性 认得就迁 认不得先记下来
    private static void ReadProperty(XElement property, Migration migration)
    {
        var name = property.Name.LocalName;
        var value = property.Value.Trim();

        switch (name)
        {
            case "AssemblyName":
                migration.AssemblyName = value;
                migration.Migrated.Add($"AssemblyName {value}");
                break;
            case "OutputPath":
                //ncproj 的插值只认自己那几个 带 msbuild 变量的照搬过去只会变成死字符串
                if (value.Contains("$(", StringComparison.Ordinal))
                    migration.Unhandled.Add($"<OutputPath>{value}</OutputPath> (ncproj does not expand msbuild properties)");
                else
                {
                    migration.Output = value;
                    migration.Migrated.Add($"Output {value}");
                }
                break;
            case "LangVersion":
                migration.LangVersion = value;
                migration.Migrated.Add($"LangVersion {value}");
                break;
            case "DefineConstants":
                migration.DefineConstants = value;
                migration.Migrated.Add($"DefineConstants {value}");
                break;
            case "Nullable":
                if (TryBool(value, out var nullable))
                {
                    migration.Nullable = nullable;
                    migration.Migrated.Add($"Nullable {value}");
                }
                else
                    migration.Unhandled.Add($"<Nullable>{value}</Nullable>");
                break;
            case "ImplicitUsings":
                if (TryBool(value, out var implicitUsings))
                {
                    migration.ImplicitUsings = implicitUsings;
                    migration.Migrated.Add($"ImplicitUsings {value}");
                }
                else
                    migration.Unhandled.Add($"<ImplicitUsings>{value}</ImplicitUsings>");
                break;
            case "TargetFramework":
                migration.Handled.Add($"<TargetFramework>{value}</TargetFramework> (ncm follows its own runtime)");
                if (!string.Equals(value, TargetFramework.Name, StringComparison.OrdinalIgnoreCase))
                {
                    migration.Warnings.Add(
                        $"the project targets {value} but ncm runs on {TargetFramework.Name}, the kernel must run on the same runtime");
                }
                break;
            case "EnableDefaultCompileItems":
            case "EnableDefaultItems":
            case "GenerateAssemblyInfo":
                migration.Handled.Add($"<{name}>{value}</{name}> (ncm always does this)");
                break;
            default:
                migration.Unhandled.Add($"<{name}>{value}</{name}>");
                break;
        }
    }

    //ReadItem 认一个项目项 只有包引用要迁 其余多半是 ncm 自己会做的事
    private static void ReadItem(XElement item, Migration migration)
    {
        var name = item.Name.LocalName;
        var include = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update") ?? string.Empty;

        switch (name)
        {
            case "PackageReference":
                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<PackageReference> without an id");
                    return;
                }

                var version = VersionOf(item);
                migration.Packages.Add(new NcPackage(include, version));
                migration.Migrated.Add($"Package {include} {(version.Length > 0 ? version : "(open)")}");
                return;
            case "Reference":
                if (include.Contains("libs", StringComparison.OrdinalIgnoreCase)
                    || include.Contains("kernel", StringComparison.OrdinalIgnoreCase))
                {
                    migration.Handled.Add(
                        $"<Reference Include=\"{include}\"> (ncm references Build\\kernel automatically, list anything else in that folder under <References>)");
                    return;
                }

                //带 HintPath 的按 HintPath 走 那才是 dll 真正的位置
                var file = HintPath(item);
                var path = file.Length > 0 ? file : include;
                if (string.IsNullOrWhiteSpace(path))
                {
                    migration.Unhandled.Add("<Reference> without a path or a HintPath");
                    return;
                }

                migration.Files.Add(path);
                migration.Migrated.Add($"File {path}");
                return;
            case "ProjectReference":
                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<ProjectReference> without a path");
                    return;
                }

                migration.Projects.Add(include);
                migration.Migrated.Add($"Project {include}");
                return;
            case "EmbeddedResource":
                if (IsManifestOrIcon(include) || IsManifestOrIcon(LogicalName(item)))
                    migration.Handled.Add($"<EmbeddedResource Include=\"{include}\"> (ncm embeds the manifest and the icon itself)");
                else
                    migration.Unhandled.Add($"<EmbeddedResource Include=\"{include}\">");
                return;
            case "Compile":
                migration.Handled.Add($"<Compile Include=\"{include}\"> (ncm scans the sources itself)");
                return;
            case "AvaloniaXaml":
                migration.Handled.Add($"<AvaloniaXaml Include=\"{include}\"> (ncm picks up every axaml itself)");
                return;
            case "AvaloniaResource":
                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<AvaloniaResource> without an Include");
                    return;
                }

                migration.AvaloniaResources.Add(include);
                migration.Migrated.Add($"AvaloniaResource {include}");
                return;
            case "InternalsVisibleTo":
                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<InternalsVisibleTo> without an assembly name");
                    return;
                }

                migration.Friends.Add(include);
                migration.Migrated.Add($"InternalsVisibleTo {include}");
                return;
            default:
                migration.Unhandled.Add($"<{name} Include=\"{include}\">");
                return;
        }
    }

    //Compose 把迁过来的东西拼成一份 .ncproj
    //默认值不写 让配置短一些 读的时候自然落到默认上
    private static XDocument Compose(Migration migration)
    {
        var root = new XElement("ncproj", new XAttribute("version", "1"));

        if (migration.LangVersion.Length > 0)
            root.Add(new XElement("Check", new XAttribute("LangVersion", migration.LangVersion)));

        var build = new XElement("Build");
        SetAttribute(build, "AssemblyName", migration.AssemblyName);
        SetAttribute(build, "Output", migration.Output);
        SetAttribute(build, "DefineConstants", migration.DefineConstants);
        if (migration.Nullable is { } nullable)
            build.SetAttributeValue("Nullable", nullable.ToString().ToLowerInvariant());
        if (migration.ImplicitUsings is { } implicitUsings)
            build.SetAttributeValue("ImplicitUsings", implicitUsings.ToString().ToLowerInvariant());
        if (build.HasAttributes)
            root.Add(build);

        if (migration.Packages.Count > 0)
        {
            var packages = new XElement("Packages");
            foreach (var package in migration.Packages)
            {
                var item = new XElement("Package", new XAttribute("Id", package.Id));
                SetAttribute(item, "Version", package.Version);
                packages.Add(item);
            }
            root.Add(packages);
        }

        if (migration.Files.Count > 0 || migration.Projects.Count > 0)
        {
            var references = new XElement("References");
            foreach (var include in migration.Files)
                references.Add(new XElement("File", new XAttribute("Include", include)));
            foreach (var include in migration.Projects)
                references.Add(new XElement("Project", new XAttribute("Include", include)));
            root.Add(references);
        }

        if (migration.Friends.Count > 0)
        {
            var friends = new XElement("InternalsVisibleTo");
            foreach (var name in migration.Friends)
                friends.Add(new XElement("Assembly", new XAttribute("Name", name)));
            root.Add(friends);
        }

        if (migration.Deploy.Count > 0)
            root.Add(new XElement("Deploy", new XAttribute("To", string.Join(';', migration.Deploy))));

        if (migration.AvaloniaResources.Count > 0)
        {
            var resources = new XElement("AvaloniaResources");
            foreach (var include in migration.AvaloniaResources)
                resources.Add(new XElement("Resource", new XAttribute("Include", include)));
            root.Add(resources);
        }

        return new XDocument(root);
    }

    //Report 把这次迁移干了什么 哪些被接手 哪些没迁都报出来
    private static void Report(Migration migration, string source, string target)
    {
        Console.WriteLine($"Written {Path.GetFileName(target)}");
        Write("Migrated:", migration.Migrated, ConsoleColor.Green);
        Write("Handled by ncm, dropped:", migration.Handled, ConsoleColor.DarkGray);
        Write("Not migrated, check them yourself:", migration.Unhandled, ConsoleColor.Yellow);
        Write("Warnings:", migration.Warnings, ConsoleColor.Yellow);
        Console.WriteLine($"Backed up the original to {Path.GetFileName(source + BackupExtension)}");
    }

    //Write 有小标题的一段 空表就整段不打
    private static void Write(string title, List<string> lines, ConsoleColor color)
    {
        if (lines.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine(title);
        Console.ForegroundColor = color;
        foreach (var line in lines)
            Console.WriteLine($"  {line}");
        Console.ResetColor();
    }

    //VersionOf 包版本写在属性上或子元素里 两种都认
    private static string VersionOf(XElement item)
        => (string?)item.Attribute("Version")
            ?? item.Elements().FirstOrDefault(element => Folded(element, "Version"))?.Value.Trim()
            ?? string.Empty;

    //LogicalName 内嵌资源写死的资源名
    private static string LogicalName(XElement item)
        => (string?)item.Attribute("LogicalName")
            ?? item.Elements().FirstOrDefault(element => Folded(element, "LogicalName"))?.Value.Trim()
            ?? string.Empty;

    //HintPath 程序集引用写明的实际位置 没写返回空
    private static string HintPath(XElement item)
        => item.Elements().FirstOrDefault(element => Folded(element, "HintPath"))?.Value.Trim() ?? string.Empty;

    //IsManifestOrIcon 这条资源是不是清单或图标 那两样 ncm 自己会嵌
    private static bool IsManifestOrIcon(string value)
        => value.Contains(ModProject.ManifestName, StringComparison.OrdinalIgnoreCase)
            || value.Contains(ModProject.DefaultIconName, StringComparison.OrdinalIgnoreCase);

    //SetAttribute 有值才写这个属性
    private static void SetAttribute(XElement element, string name, string value)
    {
        if (value.Length > 0)
            element.SetAttributeValue(name, value);
    }

    //TryBool csproj 里真假两种写法都认
    private static bool TryBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (string.Equals(value, "enable", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    //Folded 两个元素名是否只是大小写不同
    private static bool Folded(XElement element, string name)
        => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    //Migration 这一次迁移的收获
    private sealed class Migration
    {
        public string AssemblyName { get; set; } = string.Empty;

        public string Output { get; set; } = string.Empty;

        public string LangVersion { get; set; } = string.Empty;

        public string DefineConstants { get; set; } = string.Empty;

        //null 表示工程里压根没写 那就别写进 ncproj 走默认
        public bool? Nullable { get; set; }

        public bool? ImplicitUsings { get; set; }

        public List<NcPackage> Packages { get; } = [];

        //Friends 要开放内部成员的程序集名
        public List<string> Friends { get; } = [];

        //Deploy 构建后产物要复制过去的目录
        public List<string> Deploy { get; } = [];

        //AvaloniaResources 要打进资源包的模式
        public List<string> AvaloniaResources { get; } = [];

        //Files 要直接当编译引用的 dll
        public List<string> Files { get; } = [];

        //Projects 要引用其产物的其他工程
        public List<string> Projects { get; } = [];

        //Migrated 真迁过去的
        public List<string> Migrated { get; } = [];

        //Handled ncm 自己会做 丢掉不影响
        public List<string> Handled { get; } = [];

        //Unhandled 没人管的 要人自己看一眼
        public List<string> Unhandled { get; } = [];

        //Warnings 迁过去了但值得提醒的
        public List<string> Warnings { get; } = [];
    }
}
