using System.Text.RegularExpressions;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Diagnostics;

//UsageState 项目里的一处 api 用法看起来对不对
public enum UsageState
{
    //Ok 类型与成员都在清单里
    Ok,
    //Warn 类型在清单里 但成员不在它的成员表里
    Warn,
    //Missing 名字像模组 api 却不在清单里
    Missing,
}

//UsageLocation 一处用法在源码里的落点
public sealed record UsageLocation(string File, int Line, string Text);

//ApiUsageItem 一处用法的完整信息 面板照它列清单 诊断照它出修复提示
public sealed class ApiUsageItem
{
    //Symbol 类型.成员
    public required string Symbol { get; init; }

    //State 定级
    public required UsageState State { get; init; }

    //Type 类型名
    public required string Type { get; init; }

    //Member 成员名
    public required string Member { get; init; }

    //Candidates 可能想要的 api 按接近程度排前几个
    //Warn 给同类型的成员 Missing 给清单里的类型
    public required List<string> Candidates { get; init; }

    //Locations 源码里的出现位置
    public required List<UsageLocation> Locations { get; init; }
}

//ApiUsage 扫项目源码得到的用法表
//清单是权威 api 名单 对照它给每处用法定级
public sealed class ApiUsage
{
    //MaxCandidates 修复提示里最多给几个候选
    private const int MaxCandidates = 6;

    //MaxLocations 一个符号最多记几处出现
    private const int MaxLocations = 3;

    private readonly Dictionary<string, ApiUsageItem> _items = new(StringComparer.Ordinal);

    //Items 每一处用法与它的信息
    public IReadOnlyCollection<ApiUsageItem> Items => _items.Values;

    //Scan 扫目录下的源码 按清单给每处用法定级
    public static ApiUsage Scan(string root, TemplateCatalog? catalog)
    {
        var usage = new ApiUsage();
        if (catalog is null)
        {
            Trace.Log("catalog is not loaded, skipping the source scan");
            return usage;
        }

        var pattern = BuildPattern(catalog.Grade);
        if (pattern is null)
        {
            Trace.Log("grade rule is empty, skipping the source scan");
            return usage;
        }

        //条目名到成员表 表为 null 表示这条不校验成员
        var members = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        foreach (var entry in catalog.Apis)
            members[Name(entry.Id)] = entry.Members.Count > 0
                ? new HashSet<string>(entry.Members, StringComparer.Ordinal)
                : null;

        var knownTypes = members.Keys.ToList();
        var files = SourceFiles.Enumerate(root).ToList();
        Trace.Log($"scanned {files.Count} source file(s) under {root}");

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException e)
            {
                Trace.Log($"cannot read {file}: {e.Message}");
                continue;
            }

            foreach (Match match in pattern.Matches(text))
            {
                var type = match.Groups[1].Value;
                var member = match.Groups[2].Value;
                var symbol = $"{type}.{member}";

                if (!usage._items.TryGetValue(symbol, out var item))
                {
                    var state = Judge(members, type, member);
                    item = new ApiUsageItem
                    {
                        Symbol = symbol,
                        State = state,
                        Type = type,
                        Member = member,
                        Candidates = Candidates(members, knownTypes, type, member, state),
                        Locations = new List<UsageLocation>(),
                    };
                    usage._items[symbol] = item;
                    Trace.Log($"{symbol} -> {state}");
                }

                if (item.Locations.Count < MaxLocations)
                    item.Locations.Add(new UsageLocation(
                        Path.GetRelativePath(root, file).Replace('\\', '/'),
                        LineOf(text, match.Index),
                        LineText(text, match.Index)));
            }
        }

        return usage;
    }

    //BuildPattern 按清单里的规则拼出识别 类型.成员 的正则
    //规则一条都没有时不扫 免得把项目里所有 类型.成员 都当模组 api
    private static Regex? BuildPattern(GradeRule rule)
    {
        var alternatives = new List<string>();
        foreach (var type in rule.Types)
        {
            if (!string.IsNullOrWhiteSpace(type))
                alternatives.Add(Regex.Escape(type));
        }
        foreach (var prefix in rule.Prefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
                alternatives.Add($"{Regex.Escape(prefix)}[A-Za-z0-9_]*");
        }

        if (alternatives.Count == 0)
            return null;

        return new Regex(
            $@"\b({string.Join("|", alternatives)})\.([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled);
    }

    //Candidates 修复提示里给的候选
    //Warn 在同类型的成员里找 Missing 在清单的类型名里找 都按编辑距离排序
    private static List<string> Candidates(
        Dictionary<string, HashSet<string>?> members,
        List<string> knownTypes,
        string type,
        string member,
        UsageState state)
    {
        if (state == UsageState.Warn)
        {
            if (!members.TryGetValue(type, out var table) || table is null)
                return new List<string>();

            //成员动辄几十个 差太远的列出来只会添乱
            var limit = Math.Max(2, member.Length / 2);
            return table
                .Select(name => (Name: name, Distance: Similarity.Distance(member, name)))
                .Where(item => item.Distance <= limit)
                .OrderBy(item => item.Distance)
                .Take(MaxCandidates)
                .Select(item => $"{type}.{item.Name}")
                .ToList();
        }

        if (state == UsageState.Missing)
        {
            //类型总共没几条 全列出来 最像的排最前 用户扫一眼就知道能换成什么
            return knownTypes
                .OrderBy(name => Similarity.Distance(type, name))
                .Take(MaxCandidates)
                .ToList();
        }

        return new List<string>();
    }

    //LineOf 字符下标落在第几行 行号从 1 起
    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }

    //LineText 字符下标所在行的文本 缩进保留 位置箭头才对得上
    private static string LineText(string text, int index)
    {
        var start = index;
        while (start > 0 && text[start - 1] != '\n')
            start--;

        var end = text.IndexOf('\n', index);
        if (end < 0)
            end = text.Length;

        return text[start..end].TrimEnd('\r');
    }

    //Name 取条目标识的最后一段 它就是代码里写的那个类型名
    private static string Name(string id) => id.Split('.')[^1];

    //Judge 一处用法该算哪一级
    private static UsageState Judge(Dictionary<string, HashSet<string>?> members, string type, string member)
    {
        if (!members.TryGetValue(type, out var table))
            return UsageState.Missing;
        if (table is null)
            return UsageState.Ok;
        return table.Contains(member) ? UsageState.Ok : UsageState.Warn;
    }
}
