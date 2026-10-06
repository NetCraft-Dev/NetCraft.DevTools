using System.Diagnostics;
using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Tools;

//BuildTool 先给项目做一轮诊断 通过了再交给 dotnet build 最后把产物收进 Build/
internal static class BuildTool
{
    //DefaultConfiguration 不带参数时用的构建配置
    private const string DefaultConfiguration = "Release";

    //OutputDirectoryName 产物收拢目录 自动开服与自动开客户端那两条链路都从这里取
    private static readonly string OutputDirectoryName = ProjectLayout.Output;

    //OutputPath 项目根下的产物目录 自动开服那条链路按它搬产物
    //项目自己配了输出目录就照它的来
    internal static string OutputPath(string root)
    {
        var config = NcProject.TryFind(root, out _);
        return Path.Combine(root, config?.Build.Output ?? OutputDirectoryName);
    }

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("build", "Diagnose and build the mod project in the current directory", Run,
        [
            new("-c, --configuration <name>", "Build configuration, defaults to Release"),
            new("--no-check", "Skip the api and syntax checks and run dotnet build directly"),
            new("--check-only", "Run the api and syntax checks only, build nothing"),
            new("--no-manifest", "Skip the ncmod.json lookup, treat the current directory as a plain C# project (must contain a csproj)"),
        ]);

    //Run 解析参数 诊断 构建 收产物
    //命令行走这里 自动开服那条链路直接调它 参数为空即默认配置加全量检查
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

        //两个开关凑一起就没东西可做 直接挡掉
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

        //没有清单时把当前目录当一个普通 C# 项目 它得真有 csproj
        //否则一路递归下去会把子项目与模板里的示例源码都当成这个项目的源码
        if (project is null && !HasCsproj(Environment.CurrentDirectory))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("error: no .csproj in this directory, this is not a valid C# project");
            Console.ResetColor();
            return 1;
        }

        //没有清单时 root 取当前目录 名字取目录名
        var root = project is null
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(project.ManifestPath)!;
        var display = project?.DisplayName ?? new DirectoryInfo(root).Name;

        var action = checkOnly ? "Checking" : "Building";
        Console.WriteLine($"{action} {display} ({configuration})");
        Console.WriteLine();

        //配置读不动要说清楚 别让人以为是在走默认设置
        var config = NcProject.TryFind(root, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {configError}");
            Console.ResetColor();
            return 1;
        }

        //直编要看引用 诊断也要看 内核程序集与包先备齐
        if (config is not null && !Prepare(root, config))
            return 1;

        if (check && !RunChecks(root, config))
            return 1;

        //只要检查结果 到这儿就收工 不落构建产物
        if (checkOnly)
            return 0;

        //带了 ncproj 的项目自己编 没带的仍旧交给 dotnet
        if (config is not null)
            return RunRoslynBuild(root, config);

        if (!RunDotnetBuild(root, configuration))
            return 1;

        return CollectOutput(root, configuration);
    }

    //Prepare 备齐直编要用的两样东西 内核程序集与声明的包
    //两样都按需补 已经就位的直接跳过 不联网也不重下
    private static bool Prepare(string root, NcProject config)
    {
        if (!KernelStore.Sync(root, config))
            return false;

        if (RestoreTool.Ensure(config, RestoreTool.DirectoryOf(config), out var error))
            return true;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: {error}");
        Console.ResetColor();
        return false;
    }

    //RunRoslynBuild 用 Roslyn 直接编出模组程序集 不经过 dotnet 与 MSBuild 工程
    //编不编由编译器那边按时间戳自己判 这里只负责把结果报出来
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

        //包带的 targets 在编译之后干活 界面库那类模板编译就走这一步
        if (!TargetRunner.Run(root, config, target, out var targetError))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {targetError}");
            Console.ResetColor();
            return 1;
        }

        return Deploy(root, config, target) ? 0 : 1;
    }

    //Deploy 按配置把产物复制到各个宿主目录
    //原先那条 DeployModToHosts 目标干的就是这一件事 没配宿主就什么都不做
    private static bool Deploy(string root, NcProject config, string target)
    {
        var ok = true;
        foreach (var entry in config.DeployTargets)
        {
            var directory = Path.GetFullPath(Path.Combine(root, entry));
            var destination = Path.Combine(directory, Path.GetFileName(target));

            try
            {
                //内容一样就别动它 时间戳一改宿主那边又要重编一轮
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

    //AssemblyNameOf 产物名 配置里写了就用它 没写按目录名来
    //目录名正好是模板生成工程时用的那个名字 与原先的产物对得上
    private static string AssemblyNameOf(string root, NcProject config)
        => string.IsNullOrWhiteSpace(config.Build.AssemblyName)
            ? new DirectoryInfo(root).Name
            : config.Build.AssemblyName;

    //HasCsproj 目录下有没有工程文件 有才把这里当成一个普通 C# 项目
    private static bool HasCsproj(string directory)
        => Directory.EnumerateFiles(directory, "*.csproj").Any();

    //RunChecks 构建前把 api 用法与 C# 语法语义都过一遍
    //编译设置跟着项目配置走 免得检查过了编译又因为语言版本不同翻车
    private static bool RunChecks(string root, NcProject? config)
    {
        var bag = new DiagnosticBag();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        //模板目录的来源跟着项目配置走 镜像环境要用得上
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

    //RunDotnetBuild 交给真正的构建 输出直接走控制台
    //编译错误已经被前置检查挡在前面 走到这里失败多半是环境问题 原样交出输出更清楚
    private static bool RunDotnetBuild(string root, string configuration)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
        };
        //ncm 的输出统一走英文 子进程别跟着系统语言变
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

    //CollectOutput 把构建产物收进项目根下的 Build/
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

    //FindOutputDirectory bin/<配置> 下装着 dll 的那一层
    //目标框架目录名随 TFM 变 不写死
    private static string? FindOutputDirectory(string root, string configuration)
    {
        var directory = Path.Combine(root, "bin", configuration);
        if (!Directory.Exists(directory))
            return null;

        return Directory.GetDirectories(directory)
            .FirstOrDefault(path => Directory.EnumerateFiles(path, "*.dll").Any());
    }
}
