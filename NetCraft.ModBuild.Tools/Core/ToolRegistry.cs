namespace NetCraft.ModBuild.Core;

//ToolParameter 一个命令行参数 帮助里按用法与说明两列排
public sealed record ToolParameter(string Syntax, string Description);

//ToolEntry 一个已登记的工具
//Description 是一句话说明 帮助里与名字并排显示
//Parameters 是这个工具接受的参数 空表示不带参数
public sealed record ToolEntry(
    string Name,
    string Description,
    IReadOnlyList<ToolParameter> Parameters,
    Func<string[], int> Run);

//ToolRegistry 工具注册表 主程序按命令行第一个参数在这里找工具
public static class ToolRegistry
{
    private static readonly List<ToolEntry> Entries = new();

    //All 已登记的工具 按登记顺序
    public static IReadOnlyList<ToolEntry> All => Entries;

    //Register 登记一个工具 由各工具自己的 Register 调用
    //parameters 只用来出帮助 参数具体怎么解析还是各工具自己的事
    public static void Register(string name, string description, Func<string[], int> run,
        params ToolParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(description);
        ArgumentNullException.ThrowIfNull(run);
        Entries.Add(new ToolEntry(name, description, parameters, run));
    }

    //Find 按名字找工具 没有返回 null
    public static ToolEntry? Find(string name)
        => Entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    //SubCommandsOf 参数表里那些子命令 view example 这种
    //尖括号方括号开头的是位置参数 横杠开头的是选项 两种都不算子命令
    public static List<string> SubCommandsOf(ToolEntry tool)
        => tool.Parameters
            .Select(parameter => parameter.Syntax.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(word => !string.IsNullOrEmpty(word) && char.IsLetter(word[0]))
            .Select(word => word!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
