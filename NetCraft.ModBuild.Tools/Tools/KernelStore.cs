using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//KernelStore 项目里的内核引用程序集
//来源与服务端那份是同一处 复用同一套缓存与下载 只是落到项目里
internal static class KernelStore
{
    //Sync 保证内核引用齐备 返回是否可用
    //内核齐了直编才有得引 少一个整片源码都会说找不到类型
    public static bool Sync(string root, NcProject? project)
    {
        var destination = Path.Combine(root, ProjectLayout.Kernel);
        if (HasAssemblies(destination))
        {
            Trace.Log($"kernel of {root} is ready in {destination}");
            return true;
        }

        ServerStore.Configure(project);
        if (!ServerStore.Ensure())
            return false;

        //模组接口在加载器那个程序集里 编译引用要带上它
        ServerLauncher.SyncKernel(ServerStore.Root, destination, includeModLoader: true);
        if (HasAssemblies(destination))
            return true;

        Console.WriteLine($"error: no kernel assembly found in {ServerStore.Root}");
        return false;
    }

    //HasAssemblies 目录里的内核程序集齐不齐
    //根程序集 NetCraft.dll 单独算一样 设置与启动参数那批类型都在它里面
    //只有带点的那些子程序集算不齐 老目录会被认出来重新同步一遍
    private static bool HasAssemblies(string directory)
        => Directory.Exists(directory)
            && File.Exists(Path.Combine(directory, "NetCraft.dll"))
            && Directory.EnumerateFiles(directory, "NetCraft.*.dll").Any();
}
