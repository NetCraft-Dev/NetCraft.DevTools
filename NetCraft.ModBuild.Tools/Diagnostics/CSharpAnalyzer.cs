using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;
//Roslyn's Diagnostic collides with the local one in this namespace, so alias it
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace NetCraft.ModBuild.Diagnostics;

//CSharpAnalyzer compiles the project sources through Roslyn
//syntax and semantic errors surface here, and assembly is left to CompilationFactory so the check shares the real build's setup
public static class CSharpAnalyzer
{
    //XamlName matches control names declared in UI files, as in x:Name="ModList"
    private static readonly Regex XamlName = new(
        @"(?:x:)?Name\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    //Analyze compiles the project sources and collects errors into bag, skipping identifiers the catalog pass already reported
    public static void Analyze(string root, DiagnosticBag bag, IReadOnlySet<string> covered)
        => Analyze(root, CompileOptions.Default, bag, covered);

    //Analyze overload taking caller-supplied compile options so the check matches the real build
    public static void Analyze(string root, CompileOptions options, DiagnosticBag bag, IReadOnlySet<string> covered)
    {
        var project = CompilationFactory.Create(root, options, out var failure);
        if (project is null)
        {
            Trace.Log($"skipping the Roslyn check: {failure}");
            return;
        }

        var sourcePaths = new HashSet<string>(project.Sources, StringComparer.Ordinal);
        //UI files declare named controls and an initialization method that the compiler synthesizes, so they have no source on disk
        var declared = XamlMembers(root);
        var cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var reported = 0;

        foreach (var diagnostic in project.Compilation.GetDiagnostics())
        {
            //only errors are reported; warnings belong to the real build, which would otherwise flood the screen with suggestions
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                continue;

            var span = diagnostic.Location.GetLineSpan();
            //trees from a generator are not project sources, so errors they report are dropped
            if (string.IsNullOrEmpty(span.Path)
                || span.Path == ProjectCompilation.GeneratedPath
                || !sourcePaths.Contains(span.Path))
                continue;

            var line = span.StartLinePosition.Line + 1;
            var sourceLine = LineOf(cache, span.Path, line);

            //a name the catalog pass already reported would only be noise here
            //names declared in UI files are skipped too since the UI compiler supplies them
            var identifier = IdentifierAt(sourceLine, span.StartLinePosition.Character);
            if (identifier is not null && (covered.Contains(identifier) || declared.Contains(identifier)))
                continue;

            //try a concrete fix first; when it applies, skip the generic advice
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

    //SuggestFor derives one actionable fix for a compile error, or null when it cannot
    //only the two misspelled-name cases are handled; the rest fall back to the static table in CompileAdvice
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

    //SuggestName picks the closest symbol visible at that point when a name is not found
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

    //SuggestMember picks the closest real member of the type when a member name does not match
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

    //MemberNames yields the member names of a type including its base types
    private static IEnumerable<string> MemberNames(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
                yield return member.Name;
        }
    }

    //TextOf reads the source text a diagnostic points at
    private static string TextOf(RoslynDiagnostic diagnostic, TextSpan span)
        => diagnostic.Location.SourceTree?.GetText().ToString(span) ?? string.Empty;

    //XamlMembers collects the names declared in UI files
    //the UI compiler generates a field per x:Name plus an InitializeComponent, none of which reach disk
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

        //only a project with UI files can call that initialization method, so add it only when such files exist
        if (names.Count > 0)
            names.Add("InitializeComponent");
        return names;
    }

    //LineOf returns the text of a line, caching the whole file per path
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

    //IdentifierAt returns the identifier containing a 0-based column
    //Roslyn sometimes points mid-identifier, so back up to the word start and take the whole word
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
