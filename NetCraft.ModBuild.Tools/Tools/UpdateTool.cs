using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NetCraft.ModBuild.Core;
//Trace 与 System.Diagnostics.Trace 重名 这里指名道姓挑项目自己那个
using Trace = NetCraft.ModBuild.Core.Trace;

namespace NetCraft.ModBuild.Tools;

//UpdateTool 问一次 nuget 有没有新版本 有就在当前目录放个脚本把 ncm 卸了再装
//直接覆盖正在运行的程序集在 Windows 上会失败 所以脚本头一行先等一秒
//ncm 不等脚本 放完就退出 把文件让出来给卸载与安装
internal static class UpdateTool
{
    //PackageId nuget 上的包名
    private const string PackageId = "NetCraft.ModBuild.Tools";

    //VersionIndex 扁平容器接口给出的版本清单
    private static readonly string VersionIndex =
        $"https://api.nuget.org/v3-flatcontainer/{PackageId.ToLowerInvariant()}/index.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    //Register 把本工具登记进注册表
    public static void Register()
        => ToolRegistry.Register("update", "Check nuget.org and update ncm to the latest version", Run);

    //Run 查版本 有新版本就放脚本并起它 没有就报一句已是最新
    private static int Run(string[] args)
    {
        var current = CurrentVersion();
        var latest = LatestVersion();

        if (latest is null)
        {
            Console.WriteLine("Could not read the version list from nuget.org");
            return 1;
        }

        if (Compare(latest, current) <= 0)
        {
            Console.WriteLine($"ncm {current} is already the latest");
            return 0;
        }

        var script = ReleaseScript(latest);
        Console.WriteLine($"Updating ncm {current} -> {latest}");
        Console.WriteLine($"Started {script}, this process exits now");
        Launch(script);
        return 0;
    }

    //CurrentVersion 当前程序集的版本 去掉 SourceLink 挂在 + 后面的提交号
    private static string CurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }

    //LatestVersion 取 nuget 上最新的稳定版 拿不到返回 null
    //只看不带预发布标记的 与 dotnet tool install 不加 --prerelease 的口径一致
    private static string? LatestVersion()
    {
        try
        {
            var json = Http.GetStringAsync(VersionIndex).GetAwaiter().GetResult();
            var versions = JsonDocument.Parse(json).RootElement.GetProperty("versions");

            string? latest = null;
            foreach (var item in versions.EnumerateArray())
            {
                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Contains('-'))
                    continue;
                if (latest is null || Compare(value, latest) > 0)
                    latest = value;
            }
            return latest;
        }
        catch (Exception exception)
        {
            Trace.Log($"读版本清单失败 {exception.Message}");
            return null;
        }
    }

    //ReleaseScript 在当前目录放一个更新脚本 平台不同命令不同
    //头一行是延迟 等 ncm 退出后 dotnet 才动得了安装目录里的文件
    private static string ReleaseScript(string version)
    {
        var windows = OperatingSystem.IsWindows();
        var path = Path.Combine(Environment.CurrentDirectory, windows ? "ncm-update.cmd" : "ncm-update.sh");

        var lines = new[]
        {
            //Windows 上用 ping 而不是 timeout 后者在输入被重定向时会直接退出 起不到延迟作用
            //这一秒是等 ncm 退出 把安装目录里的文件让出来
            windows ? "ping -n 2 127.0.0.1 >nul" : "sleep 1",
            $"dotnet tool uninstall -g {PackageId}",
            $"dotnet tool install -g {PackageId} --version {version}",
        };

        File.WriteAllLines(path, lines);
        if (!windows)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Trace.Log($"更新脚本写到 {path}");
        return path;
    }

    //Launch 起脚本 不等它结束 让 ncm 立刻退出
    private static void Launch(string script)
    {
        var windows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = windows ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
        };

        if (windows)
            startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(script);

        Process.Start(startInfo);
    }

    //Compare 版本号比较 返回 -1 / 0 / 1
    //先逐段比数字 缺的段按 0 补 数字一样时带预发布标记的算小
    private static int Compare(string left, string right)
    {
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);

        var core = CompareParts(leftCore, rightCore);
        if (core != 0)
            return core;

        if (leftPre.Length == 0 && rightPre.Length == 0)
            return 0;
        if (leftPre.Length == 0)
            return 1;
        if (rightPre.Length == 0)
            return -1;
        return string.CompareOrdinal(leftPre, rightPre);
    }

    //Split 把版本号切成数字段与预发布标记两截 + 之后的内容丢掉
    private static (string Core, string Pre) Split(string version)
    {
        var text = version;
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        var dash = text.IndexOf('-');
        return dash >= 0 ? (text[..dash], text[(dash + 1)..]) : (text, string.Empty);
    }

    //CompareParts 逐段比数字
    private static int CompareParts(string left, string right)
    {
        var a = ParseParts(left);
        var b = ParseParts(right);
        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y)
                return x < y ? -1 : 1;
        }
        return 0;
    }

    //ParseParts 取各段的数字 遇到不是数字的段就截断
    private static List<int> ParseParts(string text)
    {
        var parts = new List<int>();
        foreach (var segment in text.Split('.'))
        {
            if (!int.TryParse(segment, out var value))
                break;
            parts.Add(value);
        }
        return parts;
    }
}
