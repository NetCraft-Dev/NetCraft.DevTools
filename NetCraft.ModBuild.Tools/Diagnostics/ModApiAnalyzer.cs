using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Diagnostics;

//ModApiAnalyzer 拿清单给项目里的 api 用法定级 再把问题转成诊断
//清单里没有的类型算 error 拦构建 成员对不上只算 warning 清单可能只是没登记全
public static class ModApiAnalyzer
{
    //UnknownTypeCode 类型不在清单里
    public const string UnknownTypeCode = "NC0001";

    //UnknownMemberCode 类型在清单里 成员不在
    public const string UnknownMemberCode = "NC0002";

    //Analyze 扫项目源码 把对不上清单的用法收进 bag
    //返回已经报过的标识符 给 Roslyn 那一步去重 同一处错不该报两遍
    public static HashSet<string> Analyze(string root, TemplateCatalog catalog, DiagnosticBag bag)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in ApiUsage.Scan(root, catalog).Items)
        {
            if (item.State == UsageState.Ok)
                continue;

            var location = item.Locations.Count > 0 ? item.Locations[0] : null;
            var column = location is null ? 1 : location.Text.IndexOf(item.Symbol, StringComparison.Ordinal) + 1;
            var unknown = item.State == UsageState.Missing;

            covered.Add(item.Type);
            covered.Add(item.Member);

            var fixes = new List<Suggestion>();
            if (location is not null)
            {
                //最近的一个候选做成能直接照着改的建议
                //类型未知就换类型那段 成员未知就换成员那段
                var first = item.Candidates.FirstOrDefault();
                if (first is not null)
                {
                    var replacement = unknown ? first : LastSegment(first);
                    var start = Math.Max(1, column) + (unknown ? 0 : item.Type.Length + 1);
                    var length = unknown ? item.Type.Length : item.Member.Length;

                    fixes.Add(Suggestion.Replace(
                        $"did you mean `{replacement}`?",
                        location.Line,
                        start,
                        length,
                        replacement,
                        Applicability.MaybeIncorrect));
                }

                //其余候选合成一条 没有标题之后得自带说明
                var rest = item.Candidates.Skip(1).ToList();
                if (rest.Count > 0)
                    fixes.Add(Suggestion.Text($"other candidates: {string.Join(", ", rest)}"));
            }

            bag.Add(new Diagnostic(
                unknown ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                unknown ? UnknownTypeCode : UnknownMemberCode,
                unknown
                    ? $"the catalog does not declare type `{item.Type}`"
                    : $"type `{item.Type}` has no member `{item.Member}`",
                location?.File ?? "?",
                location?.Line ?? 0,
                Math.Max(1, column),
                item.Symbol.Length,
                location?.Text ?? string.Empty,
                unknown
                    ? "the name may be misspelled; the catalog is the authoritative api list"
                    : $"`{item.Member}` is not a member of {item.Type}",
                string.Empty,
                fixes));
        }

        return covered;
    }

    //LastSegment 候选是 类型.成员 形式 取最后那段成员名
    private static string LastSegment(string symbol)
    {
        var dot = symbol.LastIndexOf('.');
        return dot >= 0 ? symbol[(dot + 1)..] : symbol;
    }
}
