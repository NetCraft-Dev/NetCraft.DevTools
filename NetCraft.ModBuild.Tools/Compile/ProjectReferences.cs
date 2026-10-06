using System.Diagnostics;
using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Tools;

namespace NetCraft.ModBuild.Compile;

//ProjectReferences <References> 下 <Project> 那类引用的处理
//被引用工程没编过就先编一遍 引用方只拿它的产物当编译引用 不内嵌也不部署
//被引用工程自己还可能带着引用 一路递归下去 环由 Building 挡掉
internal static class ProjectReferences
{
    //CsprojConfiguration 被引用工程还是普通 csproj 时用的构建配置
    //那种工程没有 ncproj 的配置可读 只能取一个通用值
    private const string CsprojConfiguration = "Release";

    //Building 正在构建的工程目录 挡循环引用
    private static readonly HashSet<string> Building = new(StringComparer.OrdinalIgnoreCase);

    //Cache 工程目录到产物路径 一次运行里同一份引用只解析一遍
    //值为 null 表示之前解析失败过 失败也缓存 免得反复报同一件事
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    //Clear 每次构建开工前清一遍 别让上一次的结果串到这一次
    public static void Clear()
    {
        Building.Clear();
        Cache.Clear();
    }

    //Build 把配置里声明的工程引用都备好 返回是否成功
    public static bool Build(string root, NcProject project, out string error)
    {
        error = string.Empty;
        foreach (var include in project.ProjectReferences)
        {
            if (Resolve(root, include, out error) is null)
                return false;
        }

        return true;
    }

    //Products 这些引用的产物程序集 没解析出来的忽略
    public static IEnumerable<string> Products(string root, NcProject project)
    {
        foreach (var include in project.ProjectReferences)
        {
            var directory = DirectoryOf(root, include);
            if (directory is not null
                && Cache.TryGetValue(directory, out var product)
                && product is not null)
                yield return product;
        }
    }

    //Resolve 解析一条工程引用 必要时编一遍被引用的工程
    private static string? Resolve(string root, string include, out string error)
    {
        error = string.Empty;
        var directory = DirectoryOf(root, include);
        if (directory is null)
        {
            error = $"the project referenced as \"{include}\" does not exist";
            return null;
        }

        if (Cache.TryGetValue(directory, out var cached))
            return cached;

        if (!Building.Add(directory))
        {
            error = $"circular project reference through \"{include}\"";
            return null;
        }

        try
        {
            var product = Compile(directory, out error);
            Cache[directory] = product;
            return product;
        }
        finally
        {
            Building.Remove(directory);
        }
    }

    //DirectoryOf 一条引用落在哪个目录 可以指工程目录也可以指工程文件
    private static string? DirectoryOf(string root, string include)
    {
        var path = Path.GetFullPath(Path.Combine(root, include));
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    //Compile 编一个被引用的工程 返回它的产物程序集路径
    //有 ncproj 走直编 只有 csproj 就交给 dotnet
    private static string? Compile(string directory, out string error)
    {
        error = string.Empty;
        //TryFind 会往上找 找到父工程去了就当作这个目录里没有配置
        var config = NcProject.TryFind(directory, out var configError);
        if (config is not null && SamePath(config.Directory, directory))
            return CompileProject(directory, config, out error);

        if (!string.IsNullOrEmpty(configError))
        {
            error = configError;
            return null;
        }

        return CompileCsproj(directory, out error);
    }

    //CompileProject 走直编那条路
    //内核 包 它自己的引用三样按同一顺序备齐 与主工程那边一致
    private static string? CompileProject(string directory, NcProject config, out string error)
    {
        if (!KernelStore.Sync(directory, config))
        {
            error = $"cannot prepare the kernel of {directory}";
            return null;
        }

        if (!RestoreTool.Ensure(config, RestoreTool.DirectoryOf(config), out var restoreError))
        {
            error = restoreError;
            return null;
        }

        if (!Build(directory, config, out error))
            return null;

        var name = string.IsNullOrWhiteSpace(config.Build.AssemblyName)
            ? new DirectoryInfo(directory).Name
            : config.Build.AssemblyName;
        var target = Path.Combine(directory, config.Build.Output, name + ".dll");

        Console.WriteLine($"Building referenced project {new DirectoryInfo(directory).Name}");
        return Compiler.Compile(directory, config, name, target, out error) ? target : null;
    }

    //CompileCsproj 还没迁移的工程交给 dotnet 编
    //产物从 bin/<配置> 下按工程名找 不猜目标框架那一层叫什么
    private static string? CompileCsproj(string directory, out string error)
    {
        error = string.Empty;
        var project = Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault();
        if (project is null)
        {
            error = $"no {NcProject.Extension} or .csproj found in {directory}";
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(project);
        Console.WriteLine($"Building referenced project {name} with dotnet");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
        };
        //ncm 的输出统一走英文 子进程别跟着系统语言变
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(CsprojConfiguration);
        startInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            error = "could not start dotnet";
            return null;
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            error = $"dotnet build of {name} exited with code {process.ExitCode}";
            return null;
        }

        var bin = Path.Combine(directory, "bin", CsprojConfiguration);
        var product = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (product is null)
        {
            error = $"no {name}.dll under bin/{CsprojConfiguration} of {directory}";
            return null;
        }

        return product;
    }

    //SamePath 两个目录是不是同一个 引用工程不能认到父工程头上
    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
