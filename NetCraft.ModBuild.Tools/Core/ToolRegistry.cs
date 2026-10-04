namespace NetCraft.ModBuild.Core;

//ToolEntry 一个已登记的工具
//Description 是一句话说明 帮助里与名字并排显示
public sealed record ToolEntry(string Name, string Description, Func<string[], int> Run);

//ToolRegistry 工具注册表 主程序按命令行第一个参数在这里找工具
public static class ToolRegistry
{
    private static readonly List<ToolEntry> Entries = new();

    //All 已登记的工具 按登记顺序
    public static IReadOnlyList<ToolEntry> All => Entries;

    //Register 登记一个工具 由各工具自己的 Register 调用
    public static void Register(string name, string description, Func<string[], int> run)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(description);
        ArgumentNullException.ThrowIfNull(run);
        Entries.Add(new ToolEntry(name, description, run));
    }

    //Find 按名字找工具 没有返回 null
    public static ToolEntry? Find(string name)
        => Entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
}
