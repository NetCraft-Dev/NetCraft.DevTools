using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NetCraft.ModBuild.Core;
//Roslyn 的 Diagnostic 与本地同命名空间那个重名 起个别名区分
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace NetCraft.ModBuild.Diagnostics;

//CSharpAnalyzer 用 Roslyn 把项目源码整编一遍
//语法与语义错误都在这一层出 引用取运行时平台程序集加项目 libs 下的 NC 程序集
public static class CSharpAnalyzer
{
    //GeneratedPath 补隐式 using 的那棵虚拟树 它自己的诊断不往外报
    private const string GeneratedPath = "ncm.g.cs";

    //XamlName 界面文件里给控件起的名字 形如 x:Name="ModList"
    private static readonly Regex XamlName = new(
        @"(?:x:)?Name\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    //ImplicitUsings 模组工程开了隐式 using 这里补一份等价的全局 using
    //少了它整套源码会集体报找不到 System 里的类型
    private const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    //Analyze 解析并编译项目源码 把错误收进 bag
    //covered 是清单那侧已经报过的标识符 命中的不再重复报
    public static void Analyze(string root, DiagnosticBag bag, IReadOnlySet<string> covered)
    {
        var files = SourceFiles.Enumerate(root).ToList();
        if (files.Count == 0)
        {
            Trace.Log("no source file, skipping the Roslyn check");
            return;
        }

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions, path: GeneratedPath),
        };

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

            trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file));
        }

        var compilation = CSharpCompilation.Create(
            "ncm-diagnose",
            trees,
            BuildReferences(root),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        //界面那类项目有一批成员是生成器现产的 不先跑一遍整片源码都报找不到名字
        var generated = RunGenerators(root, compilation, parseOptions);

        var cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = new HashSet<string>(files, StringComparer.Ordinal);
        //界面文件里的命名控件与初始化方法是编译期补出来的 磁盘上没有对应源码
        var declared = XamlMembers(root);
        var reported = 0;

        foreach (var diagnostic in generated.GetDiagnostics())
        {
            //只看错误 警告交给真正的构建去刷 免得一屏都是建议
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                continue;

            var span = diagnostic.Location.GetLineSpan();
            //生成器产出的树不是项目源码 它自己报的错不往外抛
            if (string.IsNullOrEmpty(span.Path) || span.Path == GeneratedPath || !sourcePaths.Contains(span.Path))
                continue;

            var line = span.StartLinePosition.Line + 1;
            var sourceLine = LineOf(cache, span.Path, line);

            //同一个名字清单那侧已经报过一次了 这里再报一遍只是噪音
            //界面文件里点名过的名字同样放过 它们本来就由界面编译器补
            var identifier = IdentifierAt(sourceLine, span.StartLinePosition.Character);
            if (identifier is not null && (covered.Contains(identifier) || declared.Contains(identifier)))
                continue;

            //先试能直接照着改的建议 命中了就不再堆通用话术
            var suggestion = SuggestFor(generated, diagnostic);
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

    //BuildReferences 编译引用 运行时平台程序集加项目 libs 下的 NC 程序集再加 NuGet 包带来的
    private static List<MetadataReference> BuildReferences(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        //运行时自带的平台程序集 模组与工具同为 net10.0 可以直接借
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
        }

        //项目 libs 下的 NC 程序集
        var libs = Path.Combine(root, "libs");
        if (Directory.Exists(libs))
        {
            foreach (var path in Directory.EnumerateFiles(libs, "*.dll", SearchOption.AllDirectories))
                paths.Add(path);
        }

        //NuGet 包引来的程序集 面板那类界面库走的是 PackageReference 不带上整片类型都找不到
        foreach (var path in PackageAssemblies(root))
            paths.Add(path);

        var references = new List<MetadataReference>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception e)
            {
                Trace.Log($"failed to load reference {path}: {e.GetType().Name}");
            }
        }

        Trace.Log($"Roslyn loaded {references.Count} reference(s)");
        return references;
    }

    //PackageAssemblies 从 restore 落下的资产文件里取各个 NuGet 包的编译期程序集
    //路径交给资产文件算 自己按包名版本拼目录迟早对不上
    private static IEnumerable<string> PackageAssemblies(string root)
    {
        var assets = Path.Combine(root, "obj", "project.assets.json");
        if (!File.Exists(assets))
        {
            Trace.Log($"no {assets}, package references are not loaded");
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(assets));
        }
        catch (Exception e)
        {
            Trace.Log($"cannot read {assets}: {e.GetType().Name}");
            yield break;
        }

        using (document)
        {
            var project = document.RootElement;
            if (!project.TryGetProperty("packageFolders", out var folders) || folders.ValueKind != JsonValueKind.Object)
                yield break;

            //全局包目录就是包名与版本前面那一段
            var folder = folders.EnumerateObject().Select(property => property.Name).FirstOrDefault();
            if (string.IsNullOrEmpty(folder) || !project.TryGetProperty("targets", out var targets))
                yield break;

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var package in target.Value.EnumerateObject())
                {
                    if (!package.Value.TryGetProperty("compile", out var compile) || compile.ValueKind != JsonValueKind.Object)
                        continue;

                    //包目录键是 包名/版本 NuGet 那边按小写存
                    var segments = package.Name.Split('/');
                    if (segments.Length != 2)
                        continue;

                    var packageDirectory = Path.Combine(folder, segments[0].ToLowerInvariant(), segments[1]);
                    foreach (var item in compile.EnumerateObject())
                    {
                        if (!item.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var path = Path.Combine(packageDirectory, item.Name.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(path))
                            yield return path;
                    }
                }
            }
        }
    }

    //RunGenerators 把各包带的生成器跑一遍 生成出来的源码并进诊断编译
    //界面库的 InitializeComponent 与 x:Name 字段都是这一步才有的 磁盘上根本找不到
    private static Compilation RunGenerators(string root, Compilation compilation, CSharpParseOptions parseOptions)
    {
        var analyzers = PackageAnalyzers(root).ToList();
        if (analyzers.Count == 0)
            return compilation;

        var generators = new List<ISourceGenerator>();
        foreach (var path in analyzers)
            LoadGenerators(path, generators);

        if (generators.Count == 0)
        {
            Trace.Log($"loaded {analyzers.Count} analyzer assembly(ies), none carries a source generator");
            return compilation;
        }

        //模板文件是走附加文件喂给生成器的 axaml 全靠这一路读到
        var additional = SourceFiles.Enumerate(root, "*.axaml")
            .Select(path => (AdditionalText)new AdditionalFile(path))
            .ToList();

        var driver = CSharpGeneratorDriver.Create(generators, additional, parseOptions, new OptionsProvider());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var failures);
        Trace.Log($"ran {generators.Count} generator(s) over {additional.Count} additional file(s), {failures.Length} diagnostic(s)");
        return output;
    }

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

    //PackageAnalyzers 各 NuGet 包里带的生成器与分析器程序集
    private static IEnumerable<string> PackageAnalyzers(string root)
    {
        foreach (var directory in PackageDirectories(root))
        {
            var analyzers = Path.Combine(directory, "analyzers", "dotnet");
            if (!Directory.Exists(analyzers))
                continue;

            foreach (var path in Directory.EnumerateFiles(analyzers, "*.dll", SearchOption.AllDirectories))
                yield return path;
        }
    }

    //PackageDirectories 资产文件里记着的每个包的落盘目录
    private static List<string> PackageDirectories(string root)
    {
        var directories = new List<string>();
        var assets = Path.Combine(root, "obj", "project.assets.json");
        if (!File.Exists(assets))
            return directories;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(assets));
        }
        catch (Exception e)
        {
            Trace.Log($"cannot read {assets}: {e.GetType().Name}");
            return directories;
        }

        using (document)
        {
            var project = document.RootElement;
            if (!project.TryGetProperty("packageFolders", out var folders) || folders.ValueKind != JsonValueKind.Object)
                return directories;

            var folder = folders.EnumerateObject().Select(property => property.Name).FirstOrDefault();
            if (string.IsNullOrEmpty(folder) || !project.TryGetProperty("targets", out var targets))
                return directories;

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var package in target.Value.EnumerateObject())
                {
                    var segments = package.Name.Split('/');
                    if (segments.Length != 2)
                        continue;

                    var directory = Path.Combine(folder, segments[0].ToLowerInvariant(), segments[1]);
                    if (!directories.Contains(directory))
                        directories.Add(directory);
                }
            }
        }

        return directories;
    }

    //AdditionalFile 交给生成器读的附加文件
    private sealed class AdditionalFile(string path) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText? GetText(CancellationToken cancellationToken = default)
        {
            try
            {
                return SourceText.From(File.ReadAllText(Path));
            }
            catch (IOException e)
            {
                Trace.Log($"cannot read {Path}: {e.Message}");
                return null;
            }
        }
    }

    //LoadGenerators 从一份分析器程序集里挑出生成器
    //只认带无参构造的 Roslyn 就是这么实例化它们的
    private static void LoadGenerators(string path, List<ISourceGenerator> generators)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(path);
        }
        catch (Exception e)
        {
            Trace.Log($"cannot load analyzer {path}: {e.GetType().Name}");
            return;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            //依赖缺一个不该让整份程序集作废 能拿到的类型接着看
            types = e.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
                continue;

            try
            {
                if (typeof(IIncrementalGenerator).IsAssignableFrom(type))
                    generators.Add(((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator());
                else if (typeof(ISourceGenerator).IsAssignableFrom(type))
                    generators.Add((ISourceGenerator)Activator.CreateInstance(type)!);
            }
            catch (Exception e)
            {
                Trace.Log($"cannot instantiate generator {type.FullName}: {e.GetType().Name}");
            }
        }
    }

    //OptionsProvider 生成器读的那份配置
    //全局属性一律不给 让各选项走自己的默认值
    //附加文件要报出它属于 AvaloniaXaml 这一项 界面库的生成器靠这个筛输入 不给就一个都不生成
    private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions None = new EmptyOptions();

        private static readonly AnalyzerConfigOptions Xaml = new XamlOptions();

        public override AnalyzerConfigOptions GlobalOptions => None;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => None;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Xaml;
    }

    //EmptyOptions 什么都不给
    private sealed class EmptyOptions : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }
    }

    //XamlOptions 只认项目项那一项
    private sealed class XamlOptions : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (key == "build_metadata.AdditionalFiles.SourceItemGroup")
            {
                value = "AvaloniaXaml";
                return true;
            }

            value = string.Empty;
            return false;
        }
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
