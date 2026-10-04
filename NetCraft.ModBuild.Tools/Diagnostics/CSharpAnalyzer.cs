using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Diagnostics;

//CSharpAnalyzer 用 Roslyn 把项目源码整编一遍
//语法与语义错误都在这一层出 引用取运行时平台程序集加项目 libs 下的 NC 程序集
public static class CSharpAnalyzer
{
    //GeneratedPath 补隐式 using 的那棵虚拟树 它自己的诊断不往外报
    private const string GeneratedPath = "ncm.g.cs";

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
            Trace.Log("没有源码文件 跳过 Roslyn 检查");
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
                Trace.Log($"读不出 {file} {e.Message}");
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

        var cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var reported = 0;

        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            //只看错误 警告交给真正的构建去刷 免得一屏都是建议
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                continue;

            var span = diagnostic.Location.GetLineSpan();
            if (string.IsNullOrEmpty(span.Path) || span.Path == GeneratedPath)
                continue;

            var line = span.StartLinePosition.Line + 1;
            var sourceLine = LineOf(cache, span.Path, line);

            //同一个名字清单那侧已经报过一次了 这里再报一遍只是噪音
            var identifier = IdentifierAt(sourceLine, span.StartLinePosition.Character);
            if (identifier is not null && covered.Contains(identifier))
                continue;

            bag.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.CurrentCulture),
                Path.GetRelativePath(root, span.Path).Replace('\\', '/'),
                line,
                span.StartLinePosition.Character + 1,
                Math.Max(1, diagnostic.Location.SourceSpan.Length),
                sourceLine,
                string.Empty,
                string.Empty,
                Array.Empty<string>()));

            reported++;
        }

        Trace.Log($"Roslyn 报了 {reported} 条错误");
    }

    //BuildReferences 编译引用 运行时平台程序集加项目 libs 下的 NC 程序集
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

        var references = new List<MetadataReference>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception e)
            {
                Trace.Log($"引用加载失败 {path} {e.GetType().Name}");
            }
        }

        Trace.Log($"Roslyn 引用 {references.Count} 个程序集");
        return references;
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
