using System.Runtime.InteropServices;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Compile;

//RefPack 目标框架的引用程序集
//编译期该用这一套而不是运行时那种实现程序集 拿实现程序集编出来的产物会绑上具体实现
//本机装了对应 SDK 就自带 packs 目录 探不到才需要联网去拉
internal static class RefPack
{
    //PackName 引用程序集所在的包名
    private const string PackName = "Microsoft.NETCore.App.Ref";

    //Cache 探一次就够 同一进程里反复编译不必再扫目录
    private static IReadOnlyList<string>? _cache;

    //Assemblies 当前目标框架的引用程序集 探不到返回空表
    public static IReadOnlyList<string> Assemblies()
    {
        if (_cache is not null)
            return _cache;

        _cache = Locate();
        return _cache;
    }

    //Locate 从本机 SDK 的 packs 目录里找引用程序集
    private static IReadOnlyList<string> Locate()
    {
        var root = DotnetRoot();
        if (root is null)
        {
            Trace.Log("cannot locate the dotnet root, reference assemblies are unavailable");
            return [];
        }

        var pack = Path.Combine(root, "packs", PackName);
        if (!Directory.Exists(pack))
        {
            Trace.Log($"no {pack}, the reference pack is not installed");
            return [];
        }

        var framework = TargetFramework();
        //版本高的排前面 但框架目录要真的存在 只装了 10.0.0 就不能挑 11.0.0
        var candidates = Directory.GetDirectories(pack)
            .OrderByDescending(path => ParseVersion(Path.GetFileName(path)));

        foreach (var candidate in candidates)
        {
            var reference = Path.Combine(candidate, "ref", framework);
            if (!Directory.Exists(reference))
                continue;

            var files = Directory.EnumerateFiles(reference, "*.dll").ToList();
            if (files.Count == 0)
                continue;

            Trace.Log($"reference pack {reference} ({files.Count} assemblies)");
            return files;
        }

        Trace.Log($"no ref/{framework} under {pack}");
        return [];
    }

    //DotnetRoot dotnet 的安装根目录
    //ncm 自己就跑在 dotnet 上 从运行时目录往上数三层就是
    private static string? DotnetRoot()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        if (string.IsNullOrEmpty(runtime))
            return null;

        //.../dotnet/shared/Microsoft.NETCore.App/<版本>/
        var directory = new DirectoryInfo(runtime);
        return directory.Parent?.Parent?.Parent?.FullName;
    }

    //TargetFramework 当前运行的目标框架名 形如 net10.0
    private static string TargetFramework()
    {
        const string marker = "Version=v";
        var name = AppContext.TargetFrameworkName ?? string.Empty;
        var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "net10.0" : "net" + name[(index + marker.Length)..];
    }

    //ParseVersion 目录名按版本比大小 认不出来当零
    private static Version ParseVersion(string name)
        => Version.TryParse(name, out var version) ? version : new Version(0, 0);
}
