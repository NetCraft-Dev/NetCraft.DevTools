using System.Reflection;
using System.Runtime.Loader;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//MsBuildLibrary 把本机 sdk 自带的 msbuild 程序集挂到解析链上
//ncm 自己不分发这些程序集 引擎必须与 sdk 同一份 否则 sdk 的解析器与 targets 会因为接口对不上翻车
internal static class MsBuildLibrary
{
    //_directory 本机 sdk 目录 找不到就是 null
    private static string? _directory;
    private static bool _attached;

    //SdkDirectory sdk 目录 拿不到说明这台机器没装 sdk
    public static string? SdkDirectory => _directory ??= Locate();

    //Attach 定位 sdk 并把解析事件挂上 返回引擎是否可用
    //要赶在第一个 msbuild 类型被解析之前调用 不然加载就按默认规则走 找不到那份程序集
    public static bool Attach(out string error)
    {
        error = string.Empty;
        if (SdkDirectory is null)
        {
            error = "no dotnet sdk found, the msbuild engine is unavailable";
            return false;
        }

        if (_attached)
            return true;

        _attached = true;
        AssemblyLoadContext.Default.Resolving += Resolve;
        Trace.Log($"msbuild engine from {SdkDirectory}");
        return true;
    }

    //Resolve 缺哪个就从 sdk 目录取哪个 取不到交回默认逻辑
    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (SdkDirectory is null || string.IsNullOrEmpty(name.Name))
            return null;

        var path = Path.Combine(SdkDirectory, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    //Locate 取版本最高的那个 sdk 目录
    private static string? Locate()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(root))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(programFiles, "dotnet");
            if (Directory.Exists(candidate))
                root = candidate;
        }

        var directory = string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, "sdk");
        if (!Directory.Exists(directory))
            return null;

        //按版本号比 别按目录名比 否则 9.x 会排在 10.x 前面
        return Directory.GetDirectories(directory)
            .Where(path => File.Exists(Path.Combine(path, "MSBuild.dll")))
            .OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var value) ? value : new Version())
            .FirstOrDefault();
    }
}
