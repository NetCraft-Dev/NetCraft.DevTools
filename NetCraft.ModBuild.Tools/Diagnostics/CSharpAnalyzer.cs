using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;
//Roslyn 的 Diagnostic 与本地同命名空间那个重名 起个别名区分
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace NetCraft.ModBuild.Diagnostics;

//CSharpAnalyzer 用 Roslyn 把项目源码整编一遍
//语法与语义错误都在这一层出 装配那一步交给 CompilationFactory 与真正的构建共用一套
public static class CSharpAnalyzer
{
    //XamlName 界面文件里给控件起的名字 形如 x:Name="ModList"
    private static readonly Regex XamlName = new(
        @"(?:x:)?Name\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    //Analyze 解析并编译项目源码 把错误收进 bag
    //covered 是清单那侧已经报过的标识符 命中的不再重复报
    public static void Analyze(string root, DiagnosticBag bag, IReadOnlySet<string> covered)
        => Analyze(root, CompileOptions.Default, bag, covered);

    //Analyze 同上 编译设置由调用方给 好让检查与真正的构建用同一套
    public static void Analyze(string root, CompileOptions options, DiagnosticBag bag, IReadOnlySet<string> covered)
    {
        var project = CompilationFactory.Create(root, options, out var failure);
        if (project is null)
        {
            Trace.Log($"skipping the Roslyn check: {failure}");
            return;
        }

        var sourcePaths = new HashSet<string>(project.Sources, StringComparer.Ordinal);
        //界面文件里的命名控件与初始化方法是编译期补出来的 磁盘上没有对应源码
        var declared = XamlMembers(root);
        var cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var reported = 0;

        foreach (var diagnostic in project.Compilation.GetDiagnostics())
        {
            //只看错误 警告交给真正的构建去刷 免得一屏都是建议
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                continue;

            var span = diagnostic.Location.GetLineSpan();
            //生成器产出的树不是项目源码 它自己报的错不往外抛
            if (string.IsNullOrEmpty(span.Path)
                || span.Path == ProjectCompilation.GeneratedPath
                || !sourcePaths.Contains(span.Path))
                continue;

            var line = span.StartLinePosition.Line + 1;
            var sourceLine = LineOf(cache, span.Path, line);

            //同一个名字清单那侧已经报过一次了 这里再报一遍只是噪音
            //界面文件里点名过的名字同样放过 它们本来就由界面编译器补
            var identifier = IdentifierAt(sourceLine, span.StartLinePosition.Character);
            if (identifier is not null && (covered.Contains(identifier) || declared.Contains(identifier)))
                continue;

            //先试能直接照着改的建议 命中了就不再堆通用话术
            var suggestion = SuggestFor(project.Compilation, diagnostic);
            var fixes = suggestion is not null
                ? new List<Suggestion> { suggestion }
                : CompileAdvice.Text(diagnostic.Id).Select(Suggestion.Text).ToList();

            bag.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                Path.GetRelativePath(root, span.Path).Replace('\\', '/'),
                line,
                span.StartLinePosition.Character + 1,
                Math.Max(1, diagnostic.Location.SourceSpan.Length),
                sourceLine,
                string.Empty,
                diagnostic.Descriptor.HelpLinkUri,
                fixes));

            reported++;
        }

        Trace.Log($"Roslyn reported {reported} error(s)");
    }

    //SuggestFor 给一条编译错误算一条能照着改的建议 算不出返回 null
    //只处理名字写错这两类 其余交给 CompileAdvice 的静态表兜底
    private static Suggestion? SuggestFor(Compilation compilation, RoslynDiagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;
        if (tree is null)
            return null;

        var model = compilation.GetSemanticModel(tree);
        var span = diagnostic.Location.SourceSpan;

        return diagnostic.Id switch
        {
            "CS0103" or "CS0246" => SuggestName(model, diagnostic, span),
            "CS0117" or "CS1061" => SuggestMember(model, diagnostic, span),
            _ => null,
        };
    }

    //SuggestName 名字找不到时在该处可见的符号里挑最像的那个
    private static Suggestion? SuggestName(SemanticModel model, RoslynDiagnostic diagnostic, TextSpan span)
    {
        var written = TextOf(diagnostic, span);
        if (string.IsNullOrEmpty(written))
            return null;

        var names = model.LookupSymbols(span.Start).Select(symbol => symbol.Name);
        var candidate = Similarity.Closest(written, names);
        if (candidate is null)
            return null;

        var start = diagnostic.Location.GetLineSpan().StartLinePosition;
        return Suggestion.Replace(
            $"did you mean `{candidate}`?",
            start.Line + 1,
            start.Character + 1,
            span.Length,
            candidate,
            Applicability.MaybeIncorrect);
    }

    //SuggestMember 成员名对不上时从那个类型实际有的成员里挑最像的那个
    private static Suggestion? SuggestMember(SemanticModel model, RoslynDiagnostic diagnostic, TextSpan span)
    {
        var root = diagnostic.Location.SourceTree?.GetRoot();
        var node = root?.FindNode(span, getInnermostNodeForTie: true);
        var access = node?.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
        if (access?.Name is not IdentifierNameSyntax name)
            return null;

        var type = model.GetTypeInfo(access.Expression).Type;
        if (type is null)
            return null;

        var written = name.Identifier.Text;
        var candidate = Similarity.Closest(written, MemberNames(type));
        if (candidate is null)
            return null;

        var start = name.GetLocation().GetLineSpan().StartLinePosition;
        return Suggestion.Replace(
            $"did you mean `{candidate}`?",
            start.Line + 1,
            start.Character + 1,
            written.Length,
            candidate,
            Applicability.MaybeIncorrect);
    }

    //MemberNames 一个类型连基类算上全部成员的名字
    private static IEnumerable<string> MemberNames(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
                yield return member.Name;
        }
    }

    //TextOf 取诊断指向的那段源码原文
    private static string TextOf(RoslynDiagnostic diagnostic, TextSpan span)
        => diagnostic.Location.SourceTree?.GetText().ToString(span) ?? string.Empty;

    //XamlMembers 界面文件里点名出来的成员
    //界面编译器按 x:Name 生成同名字段 顺带补一个 InitializeComponent 这些都不落盘
    private static HashSet<string> XamlMembers(string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in SourceFiles.Enumerate(root, "*.axaml"))
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException e)
            {
                Trace.Log($"cannot read {path}: {e.Message}");
                continue;
            }

            foreach (Match match in XamlName.Matches(text))
                names.Add(match.Groups[1].Value);
        }

        //有界面文件才可能用到那个初始化方法 一个都没有就别往名单里塞
        if (names.Count > 0)
            names.Add("InitializeComponent");
        return names;
    }

    //LineOf 取文件里第 line 行的原文 按文件缓存整份内容
    private static string LineOf(Dictionary<string, string[]> cache, string path, int line)
    {
        if (!cache.TryGetValue(path, out var lines))
        {
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                lines = Array.Empty<string>();
            }
            cache[path] = lines;
        }

        var index = line - 1;
        return index >= 0 && index < lines.Length ? lines[index] : string.Empty;
    }

    //IdentifierAt 取某列所在的标识符 列号从 0 起
    //Roslyn 有时指向标识符中间 所以先退到词首再整词取出来
    private static string? IdentifierAt(string line, int column)
    {
        if (string.IsNullOrEmpty(line) || column < 0 || column >= line.Length)
            return null;

        var start = column;
        while (start > 0 && IsWordChar(line[start - 1]))
            start--;

        var end = start;
        while (end < line.Length && IsWordChar(line[end]))
            end++;

        return end > start ? line[start..end] : null;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
}
