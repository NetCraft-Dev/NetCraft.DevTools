using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetCraft.ModBuild.Core;

//System.Diagnostics 也带一个 Trace 这里点明用项目自己的那个
using Trace = NetCraft.ModBuild.Core.Trace;

namespace NetCraft.ModBuild.Tools;

//TaskRunner 跑项目配置里定义的任务
//先把前置任务按依赖排好 再一个个跑 有一条命令失败就停
internal static class TaskRunner
{
    //ApprovalsDirectory 确认记录落在项目根的 obj 下 那是构建产物目录 清掉就重新问一次
    private const string ApprovalsDirectory = "obj";

    //ApprovalsFileName 确认记录的文件名
    private const string ApprovalsFileName = "task-approvals.json";

    //Variable 命令行里 $(Name) 与 $(env:Name) 的写法
    private static readonly Regex Variable = new(@"\$\((env:)?([A-Za-z_][A-Za-z0-9_]*)\)", RegexOptions.Compiled);

    //Run 跑一个任务 连它的前置一起 返回进程退出码
    public static int Run(NcProject project, NcTask task)
    {
        var order = Order(project, task, out var error);
        if (order is null)
        {
            Console.WriteLine($"error: {error}");
            return 1;
        }

        //带 Override 的任务能顶掉内置命令 也就等于克隆来的项目可以让你跑任意东西
        //所以每条要跑的命令都得先让人看过
        foreach (var current in order)
        {
            if (current.Overrides && !Approved(project, current))
                return 1;
        }

        var variables = Variables(project);
        foreach (var current in order)
        {
            if (current.Commands.Count == 0)
                continue;

            Console.WriteLine($"Running task {current.Name}");
            foreach (var command in current.Commands)
            {
                var line = Interpolate(command, variables);
                Console.WriteLine($"> {line}");
                if (!Execute(line, project.Directory))
                    return 1;
            }
        }
        return 0;
    }

    //Order 按 Depends 把要跑的任务排成一条链 有环或者前置不存在就报错
    private static List<NcTask>? Order(NcProject project, NcTask task, out string error)
    {
        var ordered = new List<NcTask>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var failure = string.Empty;

        if (Visit(task))
        {
            error = string.Empty;
            return ordered;
        }

        error = failure;
        return null;

        bool Visit(NcTask current)
        {
            if (done.Contains(current.Name))
                return true;

            if (!visiting.Add(current.Name))
            {
                failure = $"task dependency cycle at '{current.Name}'";
                return false;
            }

            foreach (var name in current.Depends)
            {
                var next = project.FindTask(name);
                if (next is null)
                {
                    failure = $"task '{current.Name}' depends on '{name}', which is not defined";
                    return false;
                }

                if (!Visit(next))
                    return false;
            }

            visiting.Remove(current.Name);
            done.Add(current.Name);
            ordered.Add(current);
            return true;
        }
    }

    //Variables 命令行里能插的值
    //mod 那几个来自数据清单 项目自己不该再定义一份 两边对不上只会更难查
    private static Dictionary<string, string> Variables(NcProject project)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProjectDir"] = project.Directory,
        };

        var manifest = ModProject.TryFind(project.Directory);
        if (manifest is not null)
        {
            values["ModId"] = manifest.Id;
            values["ModName"] = manifest.DisplayName;
            values["ModVersion"] = manifest.Version;
        }
        return values;
    }

    //Interpolate 把 $(Name) 换成实际值 认不出来的原样留着 免得悄悄少一段
    private static string Interpolate(string text, IReadOnlyDictionary<string, string> variables)
        => Variable.Replace(text, match =>
        {
            var name = match.Groups[2].Value;
            if (match.Groups[1].Success)
                return Environment.GetEnvironmentVariable(name) ?? string.Empty;

            if (variables.TryGetValue(name, out var value))
                return value;

            Trace.Log($"unknown variable {name} in \"{text}\", left as is");
            return match.Value;
        });

    //Approved 带 Override 的任务只放行一次确认过的内容
    //命令改过就重新问 免得确认完再被换掉
    private static bool Approved(NcProject project, NcTask task)
    {
        var fingerprint = Fingerprint(task);
        var approvals = ReadApprovals(project);
        if (approvals.TryGetValue(task.Name, out var known)
            && string.Equals(known, fingerprint, StringComparison.Ordinal))
            return true;

        //问不了就不能放行 陌生仓库里这么一条命令不该在没人看着的时候跑
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.WriteLine(
                $"error: task {task.Name} overrides a built-in tool, run it once in an interactive terminal to confirm");
            return false;
        }

        Console.WriteLine($"Task {task.Name} overrides the built-in {task.Name} tool:");
        foreach (var command in task.Commands)
            Console.WriteLine($"  {command}");

        Console.Write("Run it? [y/N] ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Aborted");
            return false;
        }

        approvals[task.Name] = fingerprint;
        WriteApprovals(project, approvals);
        return true;
    }

    //Fingerprint 任务内容的指纹 命令变了就不是同一个任务了
    private static string Fingerprint(NcTask task)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', task.Commands))));

    //ApprovalsPath 确认记录的位置
    private static string ApprovalsPath(NcProject project)
        => Path.Combine(project.Directory, ApprovalsDirectory, ApprovalsFileName);

    //ReadApprovals 读确认记录 读不出来当没记录
    private static Dictionary<string, string> ReadApprovals(NcProject project)
    {
        var path = ApprovalsPath(project);
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Trace.Log($"cannot read {path}: {e.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    //WriteApprovals 写回确认记录 写不进去也不该拦住任务
    private static void WriteApprovals(NcProject project, Dictionary<string, string> approvals)
    {
        var path = ApprovalsPath(project);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(approvals, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException e)
        {
            Trace.Log($"cannot write {path}: {e.Message}");
        }
    }

    //Execute 交给系统 shell 跑一条命令 工作目录固定项目根
    private static bool Execute(string command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        if (OperatingSystem.IsWindows())
        {
            //cmd 有自己的引号规则 交给 .NET 转义反而会坏掉 整串丢给它
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/c " + command;
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.WriteLine("error: could not start the shell");
            return false;
        }

        process.WaitForExit();
        if (process.ExitCode == 0)
            return true;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: command exited with code {process.ExitCode}");
        Console.ResetColor();
        return false;
    }
}
