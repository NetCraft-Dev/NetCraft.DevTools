namespace NetCraft.ModBuild.Core;

//ProjectLayout 项目里那几个固定目录
//内核引用 还原的包 产物各占一层 都挂在 Build 之下 换名只改这里
internal static class ProjectLayout
{
    //Build 构建总目录名
    public const string Build = "Build";

    //Kernel 内核引用程序集的落点 模板同步来的那批 也是编译时的引用
    public static readonly string Kernel = Path.Combine(Build, "kernel");

    //Packages 还原出来的 NuGet 包落点 每个包在它下面占一个以包 id 命名的子目录
    public static readonly string Packages = Path.Combine(Build, "packages");

    //Targets 包带的 props 与 targets 的落点 每个包一格
    public static readonly string Targets = Path.Combine(Build, "targets");

    //Intermediate 交给目标执行的中间目录
    public static readonly string Intermediate = Path.Combine(Build, "obj");

    //Output 产物目录
    public static readonly string Output = Path.Combine(Build, "out");
}
