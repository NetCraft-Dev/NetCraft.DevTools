using System.Diagnostics;
using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Tools;

//BuildTool 先给项目做一轮诊断 通过了再交给 dotnet build 最后把产物收进 Build/
internal static class BuildTool
{
    //DefaultConfiguration 不带参数时用的构建配置
    private const string DefaultConfiguration = "Release";

    //OutputDirectoryName 产物收拢目录 自动开服与自动开客户端那两条链路都从这里取
    private const string OutputDirectoryName = "Build";

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("build", "Diagnose and build the mod project in the current directory", Run,
        [
            new("-c, --configuration <name>", "Build configuration, defaults to Release"),
            new("--no-check", "Skip the api and syntax checks and run dotnet build directly"),
            new("--check-only", "Run the api and syntax checks only, build nothing"),
        ]);

    //Run 解析参数 诊断 构建 收产物
    private static int Run(string[] args)
    {
        var configuration = DefaultConfiguration;
        var check = true;
        var checkOnly = false;

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
                default:
                    Console.WriteLine($"error: unknown build option {args[index]}");
                    Console.WriteLine("Usage: ncm build [-c|--configuration <name>] [--no-check] [--check-only]");
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
        if (project is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: no {ModProject.ManifestName} in this directory or any parent");
            Console.ResetColor();
            return 1;
        }

        var root = Path.GetDirectoryName(project.ManifestPath)!;

        var action = checkOnly ? "Checking" : "Building";
        Console.WriteLine($"{action} {project.DisplayName} ({configuration})");
        Console.WriteLine();

        if (check && !RunChecks(root))
            return 1;

        //只要检查结果 到这儿就收工 不落构建产物
        if (checkOnly)
            return 0;

        if (!RunDotnetBuild(root, configuration))
            return 1;

        return CollectOutput(root, configuration);
    }

    //RunChecks 构建前把 api 用法与 C# 语法语义都过一遍
    private static bool RunChecks(string root)
    {
        var bag = new DiagnosticBag();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
            Console.WriteLine("warning: template catalog is unavailable, the mod api check is skipped");
        else
            covered = ModApiAnalyzer.Analyze(root, catalog, bag);

        CSharpAnalyzer.Analyze(root, bag, covered);

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

        var target = Path.Combine(root, OutputDirectoryName);
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
