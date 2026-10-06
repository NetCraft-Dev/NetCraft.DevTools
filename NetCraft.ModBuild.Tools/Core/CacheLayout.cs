namespace NetCraft.ModBuild.Core;

//CacheLayout ncm 自己的下载缓存根
//挂在用户目录下 升级与重装都不换位置 装过的几个项目共用一份
internal static class CacheLayout
{
    //Root 缓存根 拿不到用户目录时退回程序目录 免得下载没处落
    public static string Root { get; } = Resolve();

    //Client 原版客户端 jar
    public static string Client => Path.Combine(Root, "Client");

    //Server 服务端运行时与内核
    public static string Server => Path.Combine(Root, "Server");

    //Template 模板清单与示例文件
    public static string Template => Path.Combine(Root, "Template");

    //Packages nuget 全局缓存里没命中的那些包
    public static string Packages => Path.Combine(Root, "packages");

    //Resolve 算缓存根 LocalApplicationData 个别环境是空的 空就退回程序目录
    private static string Resolve()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local)
            ? Path.Combine(AppContext.BaseDirectory, "cache")
            : Path.Combine(local, "NetCraft", "ncm");
    }
}
