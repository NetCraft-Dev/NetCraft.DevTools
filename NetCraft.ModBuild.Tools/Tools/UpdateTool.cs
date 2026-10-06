using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NetCraft.ModBuild.Core;
//Trace 与 System.Diagnostics.Trace 重名 这里指名道姓挑项目自己那个
using Trace = NetCraft.ModBuild.Core.Trace;

namespace NetCraft.ModBuild.Tools;

//UpdateTool 问一次 nuget 有没有新版本 有就先下包再放个脚本把 ncm 更新掉
//脚本落在用户目录的更新缓存里 用下载好的那一份当源 到点自己装
//直接覆盖正在运行的程序集在 Windows 上会失败 所以脚本头一行先等一秒
//ncm 不等脚本 放完就退出 把文件让出来给安装
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

    //Run 查版本 有新版本先下包 再放脚本并起它 没有就报一句已是最新
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

        //升级要换掉安装目录里的文件 还占着的实例会跟着出问题 先问一句
        if (!ConfirmUnlocked())
        {
            Console.WriteLine("Update cancelled");
            return 1;
        }

        //包先下到手里 脚本随后拿它当源装 不让 dotnet 再去网上拉一遍
        if (Download(latest) is null)
            return 1;

        var script = ReleaseScript(latest);
        Console.WriteLine($"Updating ncm {current} -> {latest}");
        Console.WriteLine($"Started {script}, this process exits now");
        Launch(script);
        return 0;
    }

    //ConfirmUnlocked 升级前看一眼 ncm 是不是还被别的进程占着
    //占着就问一句要不要硬来 回车算不继续 输入接不上也一样
    private static bool ConfirmUnlocked()
    {
        var executable = NcmExecutable();
        var lockers = Lockers();

        if (lockers.Count == 0 && !IsLocked(executable))
            return true;

        if (executable is not null)
            Console.WriteLine($"{executable} is in use" + (lockers.Count == 0
                ? string.Empty
                : $", held by process(es) {string.Join(", ", lockers)}"));

        return Prompt.Confirm(
            "Some processes are still locking the ncm tool, continuing the update may cause errors. Continue?",
            defaultYes: false,
            warn: true);
    }

    //NcmExecutable ncm 的可执行文件
    //dotnet tool 的 shim 落在用户主目录的 .dotnet/tools 下 名字就是命令名
    //shim 不在时退回当前进程自己的可执行文件
    private static string? NcmExecutable()
    {
        var shim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", OperatingSystem.IsWindows() ? "ncm.exe" : "ncm");
        return File.Exists(shim) ? shim : Environment.ProcessPath;
    }

    //Lockers 还活着的其他 ncm 进程 自己那个不算
    private static List<int> Lockers()
    {
        var self = Environment.ProcessId;
        var lockers = new List<int>();
        foreach (var process in Process.GetProcessesByName("ncm"))
        {
            if (process.Id != self)
                lockers.Add(process.Id);
            process.Dispose();
        }
        return lockers;
    }

    //IsLocked 可执行文件连独占打开都拿不到就是有谁占着
    //Linux 那边不做强制锁 这条路只补 Windows 上按进程名认不出来的情况
    private static bool IsLocked(string? executable)
    {
        if (executable is null || !File.Exists(executable))
            return false;

        try
        {
            using var stream = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            Trace.Log($"{executable} is locked");
            return true;
        }
    }

    //Download 把新版包下进更新缓存 下过了就直接用 返回包路径 下不动返回 null
    private static string? Download(string version)
    {
        Directory.CreateDirectory(CacheLayout.Update);
        var target = Path.Combine(CacheLayout.Update, $"{PackageId}.{version}.nupkg");
        if (File.Exists(target))
        {
            Trace.Log($"update package is already downloaded {target}");
            return target;
        }

        //扁平容器接口里包名段一律小写 版本段照 nuget 上的写法
        var name = PackageId.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{name}/{version.ToLowerInvariant()}/{name}.{version.ToLowerInvariant()}.nupkg";

        try
        {
            var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(target, bytes);
            Console.WriteLine($"Downloaded {Path.GetFileName(target)} ({bytes.Length / 1024} KB)");
            return target;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"error: cannot download {url}: {exception.Message}");
            return null;
        }
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
            Trace.Log($"failed to read the version list: {exception.Message}");
            return null;
        }
    }

    //ReleaseScript 在更新缓存里放一个更新脚本 平台不同命令不同
    //用 dotnet tool update 从下载好的那一份装 版本与源都钉死
    //头一行是延迟 等 ncm 退出后 dotnet 才动得了安装目录里的文件
    private static string ReleaseScript(string version)
    {
        var windows = OperatingSystem.IsWindows();
        Directory.CreateDirectory(CacheLayout.Update);
        var path = Path.Combine(CacheLayout.Update, windows ? "ncm-update.cmd" : "ncm-update.sh");

        var lines = new[]
        {
            //Windows 上用 ping 而不是 timeout 后者在输入被重定向时会直接退出 起不到延迟作用
            //这一秒是等 ncm 退出 把安装目录里的文件让出来
            windows ? "ping -n 2 127.0.0.1 >nul" : "sleep 1",
            $"dotnet tool update -g {PackageId} --version {version} --add-source \"{CacheLayout.Update}\"",
        };

        File.WriteAllLines(path, lines);
        if (!windows)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Trace.Log($"update script written to {path}");
        return path;
    }

    //Launch 起脚本 不等它结束 让 ncm 立刻退出
    //工作目录定在脚本那一处 只给文件名 用户目录带空格也不会被引号绊住
    private static void Launch(string script)
    {
        var windows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = windows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
        };
        //ncm 的输出统一走英文 子进程别跟着系统语言变
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        if (windows)
            startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(Path.GetFileName(script));

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
