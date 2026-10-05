using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//ServerLauncher 动态加载服务端内核并把它跑起来
//流程与 NetCraft.ServerExe 一致 区别是内核不再编译期引用 而是按名字从 Server 目录取
internal static class ServerLauncher
{
    //KernelDirectoryName 内核程序集子目录名 与内核那边的约定对齐
    private const string KernelDirectoryName = "kernel";

    //RunDirectoryName 服务端运行目录名 存档日志配置都落在这里
    internal const string RunDirectoryName = "run";

    //RunDirectory 本次运行的目录 落在当前工作目录下 模组与存档都在里面
    internal static string RunDirectory => Path.Combine(Environment.CurrentDirectory, RunDirectoryName);

    //ModLoaderAssemblyName 模组加载器程序集名
    private const string ModLoaderAssemblyName = "NetCraft.ModLoader";

    //ServerAssemblyName 服务端程序集名
    private const string ServerAssemblyName = "NetCraft.Server";

    //Launch 备好运行目录 同步内核 再按服务端入口那套流程启动
    public static int Launch(string[] args)
    {
        var source = ServerStore.Root;
        var run = RunDirectory;
        Directory.CreateDirectory(run);

        //内核要长在程序根底下内核那边才找得到 程序根随后指向 run 内核就得跟着进来
        SyncKernel(source, Path.Combine(run, KernelDirectoryName));

        Directory.SetCurrentDirectory(run);

        //主库先起来 内核加载器与程序根都要在别的内核程序集被解析之前定好
        var main = Load(Path.Combine(source, "NetCraft.dll"));
        main.GetType("NetCraft.EmbeddedAssemblyLoader", throwOnError: true)!
            .GetMethod("Initialize")!.Invoke(null, null);
        main.GetType("NetCraft.AppPaths", throwOnError: true)!
            .GetMethod("SetOverride")!.Invoke(null, new object?[] { run });

        //内核程序集留给内核自己的加载器 它那边要过一次模组改写器 这里插手会让注入失效
        RegisterResolver(source);

        //日志出口要早于模组引导 注入与内核 Initialize 都在那之后 晚一步整段 debug 记录都会丢
        EnableDebug(args);

        //模组引导 必须早于服务端类型被解析
        var loader = Load(Path.Combine(source, ModLoaderAssemblyName + ".dll"));
        var bootstrap = loader.GetType("NetCraft.ModLoader.ModBootstrap", throwOnError: true)!;
        var environment = loader.GetType("NetCraft.ModLoader.ModEnvironment", throwOnError: true)!;
        bootstrap.GetMethod("Run")!.Invoke(null, new object?[] { Enum.Parse(environment, "Server"), null });

        //服务端启动流程 参数原样交给它
        var server = Load(Path.Combine(source, ServerAssemblyName + ".dll"));
        server.GetType("NetCraft.Game.ServerMain", throwOnError: true)!
            .GetMethod("Run")!.Invoke(null, new object?[] { args });
        return 0;
    }

    //EnableDebug 按 --debug 放开全局调试模式与日志级别
    //与 ServerExe 的 BootMods 同一件事 区别是这里拿不到编译期类型 先按名字把两个内嵌子库拉起来
    private static void EnableDebug(string[] args)
    {
        if (Array.IndexOf(args, "--debug") < 0)
            return;

        var config = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("NetCraft.Config"));
        var util = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("NetCraft.Util"));

        config.GetType("NetCraft.Config.DebugMode", throwOnError: true)!
            .GetProperty("IsEnabled")!.SetValue(null, true);

        var log = util.GetType("NetCraft.Logging.Log", throwOnError: true)!;
        var level = Enum.Parse(util.GetType("NetCraft.Logging.LogLevel", throwOnError: true)!, "Debug");
        log.GetMethod("SetConsoleLevel")!.Invoke(null, new[] { level });
        log.GetMethod("SetFileLevel")!.Invoke(null, new[] { level });
        log.GetMethod("SetVerbose")!.Invoke(null, new object?[] { true });
    }

    //SyncKernel 把 Server 里的内核程序集同步进运行目录
    //同名同大小就跳过 之后每次开服这一趟几乎不花时间
    //加载器不进内核目录 它跟主库一样是常驻的那两个 服务端发布版也把它留在根上
    private static void SyncKernel(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        var copied = 0;
        foreach (var path in Directory.EnumerateFiles(source, "NetCraft.*.dll"))
        {
            if (Path.GetFileNameWithoutExtension(path) == ModLoaderAssemblyName)
                continue;

            var target = Path.Combine(destination, Path.GetFileName(path));
            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == new FileInfo(path).Length)
                continue;

            File.Copy(path, target, overwrite: true);
            copied++;
        }
        Trace.Log($"kernel synced {copied} file(s) to {destination}");
    }

    //RegisterResolver 兜住非内核程序集 内核那几个走内核自己的解析
    //内核程序集要过模组改写器 这里先加载会让那批注入规则判为太晚
    private static void RegisterResolver(string source)
    {
        var resolver = new AssemblyDependencyResolver(Path.Combine(source, ServerAssemblyName + ".dll"));

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is null || name.Name.StartsWith("NetCraft", StringComparison.Ordinal))
                return null;

            var path = resolver.ResolveAssemblyToPath(name);
            if (path is null)
            {
                //deps 没登记的按目录再兜一下
                var candidate = Path.Combine(source, name.Name + ".dll");
                path = File.Exists(candidate) ? candidate : null;
            }
            return path is null ? null : context.LoadFromAssemblyPath(path);
        };

        //原生库同理 登记过的按登记位置找 找不到交给系统
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
        };
    }

    //Load 按路径加载一个程序集 这里没有可退的地方 失败直接抛出
    private static Assembly Load(string path)
    {
        Trace.Log($"loading assembly {path}");
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
}
