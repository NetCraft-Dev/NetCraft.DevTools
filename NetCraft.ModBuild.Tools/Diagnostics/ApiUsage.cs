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

    //TypeDeclaration 源码里定义类型的几种写法 用它把项目自己的类型摘出清单校验
    //清单管的是内核暴露给模组的 api 项目自己声明的类型不归它管 写错了编译期就知道
    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|struct|interface|enum|record)\s+(?:class\s+|struct\s+)?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

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

        //先把整份源码读进来 项目自己定义的类型要一次收齐
        //边读边扫的话 前面文件引用后面文件定义的类型会被当成清单里没有
        //匹配走盖掉注释与字符串的那一份 报位置仍旧拿原始那份 两者下标一一对应
        var sources = new List<(string File, string Text, string Masked)>(files.Count);
        var defined = new HashSet<string>(StringComparer.Ordinal);
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

            var masked = Mask(text);
            sources.Add((file, text, masked));
            foreach (Match declaration in TypeDeclaration.Matches(masked))
                defined.Add(declaration.Groups[1].Value);
        }
        Trace.Log($"found {defined.Count} type declaration(s) in the project itself");

        foreach (var (file, text, masked) in sources)
        {
            foreach (Match match in pattern.Matches(masked))
            {
                var type = match.Groups[1].Value;
                var member = match.Groups[2].Value;
                var symbol = $"{type}.{member}";

                if (!usage._items.TryGetValue(symbol, out var item))
                {
                    //项目自己声明的类型直接算过 清单里有没有同名的都不该拿来说事
                    var state = defined.Contains(type)
                        ? UsageState.Ok
                        : Judge(members, type, member);
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

    //Mask 把注释与字符串盖成空格 换行留着
    //匹配与报位置都按同一份文本的下标走 盖过之后行列照样对得上
    private static string Mask(string text)
    {
        var buffer = text.ToCharArray();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            //行注释 盖到行尾
            if (current == '/' && Looking(text, index + 1, '/'))
            {
                while (index < text.Length && text[index] != '\n')
                    buffer[index++] = ' ';
                continue;
            }

            //块注释 盖到收尾那一对 没闭合就盖到底
            if (current == '/' && Looking(text, index + 1, '*'))
            {
                buffer[index++] = ' ';
                buffer[index++] = ' ';
                while (index < text.Length)
                {
                    var ends = text[index] == '*' && Looking(text, index + 1, '/');
                    if (text[index] != '\n')
                        buffer[index] = ' ';
                    index++;
                    if (!ends)
                        continue;

                    if (index < text.Length)
                        buffer[index++] = ' ';
                    break;
                }
                continue;
            }

            //字符串与字符字面量 前面的 @ $ 一起盖进去
            if (current is '"' or '\'' or '@' or '$' && MaskLiteral(text, buffer, ref index))
                continue;

            index++;
        }

        return new string(buffer);
    }

    //MaskLiteral 盖掉一处字面量 认出来返回真
    //@ 与 $ 只是前缀 后头没跟引号就原样放过 标识符里的 @ 不能当字符串头
    private static bool MaskLiteral(string text, char[] buffer, ref int index)
    {
        var start = index;
        while (index < text.Length && text[index] is '@' or '$')
            index++;

        if (index >= text.Length || text[index] is not ('"' or '\''))
        {
            index = start;
            return false;
        }

        var quote = text[index];
        var verbatim = index > start;
        var raw = quote == '"' && Looking(text, index + 1, '"') && Looking(text, index + 2, '"');

        //起始引号 原始字符串是三连
        for (var step = 0; step < (raw ? 3 : 1) && index < text.Length; step++)
            buffer[index++] = ' ';

        while (index < text.Length)
        {
            //原始字符串三连引号收尾 里面的单双引号都不算数
            if (raw && Looking(text, index, quote) && Looking(text, index + 1, quote) && Looking(text, index + 2, quote))
            {
                for (var step = 0; step < 3; step++)
                    buffer[index++] = ' ';
                break;
            }

            //转义 反斜杠连着下一个字符一起盖 换行留着免得行列错位
            if (!raw && !verbatim && text[index] == '\\')
            {
                buffer[index++] = ' ';
                if (index < text.Length && text[index] != '\n')
                    buffer[index++] = ' ';
                continue;
            }

            //逐字字符串里成对的引号是一处转义 不是收尾
            if (!raw && verbatim && Looking(text, index, quote) && Looking(text, index + 1, quote))
            {
                buffer[index++] = ' ';
                buffer[index++] = ' ';
                continue;
            }

            var closing = text[index] == quote;
            if (text[index] != '\n')
                buffer[index] = ' ';
            index++;

            if (closing)
                break;
        }

        return true;
    }

    //Looking 这一格是不是那个字符 越界一律不算
    private static bool Looking(string text, int index, char value)
        => index >= 0 && index < text.Length && text[index] == value;

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
