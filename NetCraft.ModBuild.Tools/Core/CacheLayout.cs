namespace NetCraft.ModBuild.Core;

//CacheLayout ncm 在用户目录下占的那几处
//挂在用户目录下 升级与重装都不换位置 装过的几个项目共用一份
internal static class CacheLayout
{
    //Home 用户目录下的产品目录 拿不到用户目录时退回程序目录 免得下载没处落
    public static string Home { get; } = Resolve();

    //Root 下载缓存根
    public static string Root { get; } = Path.Combine(Home, "ncm");

    //Update 自更新下载下来的包与生成的脚本 与缓存分开放 免得被当成能清就清的缓存
    public static string Update { get; } = Path.Combine(Home, "Update");

    //Client 原版客户端 jar
    public static string Client => Path.Combine(Root, "Client");

    //Server 服务端运行时与内核
    public static string Server => Path.Combine(Root, "Server");

    //Template 模板清单与示例文件
    public static string Template => Path.Combine(Root, "Template");

    //Packages nuget 全局缓存里没命中的那些包
    public static string Packages => Path.Combine(Root, "packages");

    //Resolve 算产品目录 LocalApplicationData 个别环境是空的 空就退回程序目录
    private static string Resolve()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : Path.Combine(local, "NetCraft");
    }
}
