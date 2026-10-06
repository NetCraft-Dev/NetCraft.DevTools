using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Compile;

//CompileOptions 一次编译用的设置
public sealed record CompileOptions(string AssemblyName, string LangVersion, bool Nullable, bool ImplicitUsings,
    string DefineConstants)
{
    //Default 只看不编时用的那套 与模板工程的默认值一致
    public static CompileOptions Default { get; } =
        new("ncm-project", NcCheck.DefaultLangVersion, true, true, string.Empty);

    //From 按项目配置来 fallbackName 是配置没写程序集名时的兜底
    public static CompileOptions From(NcProject project, string? fallbackName = null)
        => new(
            string.IsNullOrWhiteSpace(project.Build.AssemblyName)
                ? fallbackName ?? "ncm-project"
                : project.Build.AssemblyName,
            project.Check.LangVersion,
            project.Build.Nullable,
            project.Build.ImplicitUsings,
            project.Build.DefineConstants);
}

//ProjectCompilation 装配好的一次编译
public sealed class ProjectCompilation
{
    //GeneratedPath 补隐式 using 那棵虚拟树的路径 它自己的诊断不往外报
    public const string GeneratedPath = "ncm.g.cs";

    internal ProjectCompilation(Compilation compilation, CSharpParseOptions parseOptions,
        IReadOnlyList<string> sources, IReadOnlyList<string> references)
    {
        Compilation = compilation;
        ParseOptions = parseOptions;
        Sources = sources;
        References = references;
    }

    //Compilation 跑过源生成器之后的结果
    public Compilation Compilation { get; }

    //ParseOptions 解析源码用的选项 生成器那边也要同一份
    public CSharpParseOptions ParseOptions { get; }

    //Sources 参与编译的项目源码 不含那棵虚拟树
    public IReadOnlyList<string> Sources { get; }

    //References 这次编译用到的引用文件 增量判定也要盯它们
    public IReadOnlyList<string> References { get; }
}

//CompilationFactory 把项目源码 引用 隐式 using 与源生成器装成一次编译
//检查与真正的构建共用这一套 省得两边各解析一遍源码
public static class CompilationFactory
{
    //ImplicitUsings 工程开了隐式 using 时补一份等价的全局 using
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

    //Create 装配一次编译 装不出来返回 null 并把原因写进 error
    public static ProjectCompilation? Create(string root, CompileOptions options, out string error)
    {
        error = string.Empty;
        var files = SourceFiles.Enumerate(root).ToList();
        if (files.Count == 0)
        {
            error = "no C# source file found in this project";
            return null;
        }

        var parseOptions = ParseOptionsOf(options);
        var trees = new List<SyntaxTree>(files.Count + 1);

        if (options.ImplicitUsings)
        {
            trees.Add(CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions,
                path: ProjectCompilation.GeneratedPath, encoding: Encoding.UTF8));
        }

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

            //编码要报给 Roslyn 少了这一条调试信息就没法生成
            trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file, encoding: Encoding.UTF8));
        }

        var referencePaths = ReferencePaths(root);
        var compilation = CSharpCompilation.Create(
            options.AssemblyName,
            trees,
            LoadReferences(referencePaths),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: options.Nullable
                    ? NullableContextOptions.Enable
                    : NullableContextOptions.Disable,
                optimizationLevel: OptimizationLevel.Release,
                deterministic: true));

        //界面那类项目有一批成员是生成器现产的 不先跑一遍整片源码都报找不到名字
        var generated = RunGenerators(root, compilation, parseOptions);
        return new ProjectCompilation(generated, parseOptions, files, referencePaths);
    }

    //Inputs 会影响产物的那份文件清单 增量判定按它比时间戳
    //自己能想到的输入都算上 少列一个就可能编出过期的产物
    public static IReadOnlyList<string> Inputs(string root)
    {
        var paths = new List<string>(SourceFiles.Enumerate(root));
        paths.AddRange(ReferencePaths(root));
        return paths;
    }

    //ParseOptionsOf 语言版本与编译期符号
    private static CSharpParseOptions ParseOptionsOf(CompileOptions options)
    {
        var parse = new CSharpParseOptions(LanguageVersionOf(options.LangVersion));
        var symbols = Symbols(options.DefineConstants);
        return symbols.Count == 0 ? parse : parse.WithPreprocessorSymbols(symbols);
    }

    //LanguageVersionOf 配置里写的那种语言版本换成 Roslyn 的枚举
    private static LanguageVersion LanguageVersionOf(string value)
    {
        if (string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase))
            return LanguageVersion.Latest;

        if (string.Equals(value, "preview", StringComparison.OrdinalIgnoreCase))
            return LanguageVersion.Preview;

        //12.0 这种带小数点的写法对应 CSharp12
        var major = value.Split('.')[0];
        return Enum.TryParse<LanguageVersion>("CSharp" + major, ignoreCase: true, out var version)
            ? version
            : LanguageVersion.Latest;
    }

    //Symbols 分号或逗号分隔的编译期符号 形如 DEBUG 或 TRACE=1 都取等号前面那段
    private static List<string> Symbols(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(symbol => symbol.Split('=')[0].Trim())
                .Where(symbol => symbol.Length > 0)
                .ToList();

    //References 这次编译用到的引用文件 targets 那边也要这份清单
    internal static IReadOnlyList<string> References(string root) => ReferencePaths(root);

    //ReferencePaths 编译要用到的全部引用文件
    //引用程序集优先 那套只带签名 拿运行时实现程序集编出来的产物会绑上具体实现
    private static List<string> ReferencePaths(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in RefPack.Assemblies())
            paths.Add(path);

        if (paths.Count == 0 && AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
        }

        //项目 Build/kernel 下的 NC 程序集 模板同步来的那批
        //只盯这一层 还原出来的包另有清单指路 别让它把别人的东西也扫进来
        var kernel = Path.Combine(root, ProjectLayout.Kernel);
        if (Directory.Exists(kernel))
        {
            foreach (var path in Directory.EnumerateFiles(kernel, "*.dll", SearchOption.AllDirectories))
                paths.Add(path);
        }

        //还原出来的包程序集 直接扫落点 一个包一层
        foreach (var path in PackageResolver.Assemblies(root))
            paths.Add(path);

        //项目配置里声明的两类引用 直接给的 dll 与别的工程的产物
        //没配的项目走到这里就是空 与原先一样
        var config = NcProject.TryFind(root, out _);
        if (config is not null)
        {
            foreach (var pattern in config.FileReferences)
            {
                foreach (var file in PathPattern.Match(root, pattern))
                {
                    if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        paths.Add(file);
                }
            }

            foreach (var product in ProjectReferences.Products(root, config))
                paths.Add(product);
        }
        //没有 ncproj 就按普通 csproj 走 它那三类引用交给 msbuild 求值
        //--no-manifest 检查一个 csproj 项目走的就是这条路
        else
        {
            foreach (var path in CsprojReferences.Assemblies(root))
                paths.Add(path);
        }

        return paths.ToList();
    }

    //LoadReferences 把引用文件读成 Roslyn 的元数据引用 读不动的跳过
    private static List<MetadataReference> LoadReferences(IEnumerable<string> paths)
    {
        var references = new List<MetadataReference>();
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

    //RunGenerators 把各包带的生成器跑一遍 生成出来的源码并进编译
    //界面库的 InitializeComponent 与 x:Name 字段都是这一步才有的 磁盘上根本找不到
    private static Compilation RunGenerators(string root, Compilation compilation, CSharpParseOptions parseOptions)
    {
        var analyzers = PackageResolver.Analyzers(root).ToList();
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
        foreach (var failure in failures)
            Trace.Log($"generator diagnostic {failure.Id}: {failure.GetMessage()}");

        foreach (var tree in output.SyntaxTrees.Except(compilation.SyntaxTrees))
            Trace.Log($"generated tree {tree.FilePath}");
        return output;
    }

    //LoadGenerators 从一份分析器程序集里挑出生成器
    //只认带无参构造的 Roslyn 就是这么实例化它们的
    private static void LoadGenerators(string path, List<ISourceGenerator> generators)
    {
        System.Reflection.Assembly assembly;
        try
        {
            assembly = System.Reflection.Assembly.LoadFrom(path);
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
        catch (System.Reflection.ReflectionTypeLoadException e)
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
                else
                    continue;

                Trace.Log($"generator {type.FullName} from {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                Trace.Log($"cannot instantiate generator {type.FullName}: {e.GetType().Name}");
            }
        }
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
    //取不到时值要给 null 给空串的话生成器会当成属性就是空 那与没有这个属性是两回事
    private sealed class EmptyOptions : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            Trace.Log($"global option {key}");
            value = null!;
            return false;
        }
    }

    //XamlOptions 只认项目项那一项
    private sealed class XamlOptions : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            Trace.Log($"additional option {key}");
            if (key == "build_metadata.AdditionalFiles.SourceItemGroup")
            {
                value = "AvaloniaXaml";
                return true;
            }

            value = null!;
            return false;
        }
    }
}
