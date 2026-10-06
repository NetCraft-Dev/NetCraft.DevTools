using NuGet.Frameworks;

namespace NetCraft.ModBuild.Compile;

//TargetFramework 当前运行的目标框架
//模组得跟内核同一个运行时 所以框架名直接从 ncm 自己的运行时读 不另配
internal static class TargetFramework
{
    //Name 短名 形如 net10.0
    public static string Name { get; } = Resolve();

    //NuGet NuGet 那边的那套表示 挑包里的框架目录要用它
    public static NuGetFramework NuGet { get; } = NuGetFramework.Parse(Name);

    //Resolve 从运行时的目标框架名里取 形如 .NETCoreApp,Version=v10.0
    private static string Resolve()
    {
        const string marker = "Version=v";
        var name = AppContext.TargetFrameworkName ?? string.Empty;
        var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "net10.0" : "net" + name[(index + marker.Length)..];
    }
}
