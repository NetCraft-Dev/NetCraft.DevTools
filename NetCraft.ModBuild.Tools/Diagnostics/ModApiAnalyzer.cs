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
                unknown ? "Closest names in the catalog" : "Members this type actually has",
                item.Candidates));
        }

        return covered;
    }
}
