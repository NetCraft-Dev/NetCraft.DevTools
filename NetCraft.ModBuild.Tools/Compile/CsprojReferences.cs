using System.Diagnostics;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NetCraft.ModBuild.Tools;

//System.Diagnostics 也带一个 Trace 这里点明用项目自己的那个
using Trace = NetCraft.ModBuild.Core.Trace;

namespace NetCraft.ModBuild.Compile;

//CsprojReferences 普通 csproj 项目的编译引用
//不自己解析那份 xml 让 msbuild 引擎跑一遍 ResolveReferences 取它算出来的那份
//程序集名解析 包还原 项目引用这三样都由 msbuild 与 nuget 负责 自己写一遍只会漏
//ncproj 项目不走这里 它的引用从项目配置与 Build 目录来
internal static class CsprojReferences
{
    //Cache 一个工程求一次就够 检查与构建两条链都会问同一份
    private static readonly Dictionary<string, IReadOnlyList<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    //Assemblies 目录里那个 csproj 编译要用到的引用文件
    //没有 csproj 或引擎不可用时返回空表 调用方那边自然退回只有框架引用
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

    //Resolve 求值一次 ResolveReferences 把 ReferencePath 那批取出来
    private static IReadOnlyList<string> Resolve(string project)
    {
        if (!MsBuildLibrary.Attach(out var engineError))
        {
            Console.WriteLine($"warning: {engineError}, the references of {Path.GetFileName(project)} are skipped");
            return [];
        }

        var directory = Path.GetDirectoryName(project)!;
        //没有资产文件就先还原 少了它 ResolvePackageAssets 那一步直接失败
        if (!File.Exists(Path.Combine(directory, "obj", "project.assets.json")) && !Restore(directory))
            return [];

        try
        {
            TargetRunner.Sdks();
            var collection = new ProjectCollection();
            var loaded = new Project(project, globalProperties: null, toolsVersion: null, projectCollection: collection);
            //ReferencePath 是目标跑起来才填的 求值那份取不到 得拿实例
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

    //Restore 交给 dotnet 还原一次 被引用工程的产物也在这条链上一起准备好
    private static bool Restore(string directory)
    {
        Console.WriteLine($"Restoring {new DirectoryInfo(directory).Name} before resolving its references");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
        };
        //ncm 的输出统一走英文 子进程别跟着系统语言变
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
