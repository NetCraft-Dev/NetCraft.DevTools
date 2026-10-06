using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Diagnostics;

//DependencyReferences 找出项目里还在用某个依赖的源码文件
//判据是语义 用到的那个符号到底出自哪个程序集 按名字猜会与项目自己的东西撞车
//程序集名从 dll 里读 文件名不一定就是程序集名
internal static class DependencyReferences
{
    //Scan 扫出还在引用这批程序集的源码文件 返回相对项目根的路径
    //编译装不起来就没得判 返回空并把原因写进 error
    public static IReadOnlyList<string> Scan(string root, NcProject project, IReadOnlyList<string> assemblies,
        out string error)
    {
        error = string.Empty;
        var names = Names(assemblies);
        if (names.Count == 0)
        {
            error = "none of the assemblies could be read";
            return [];
        }

        var assembled = CompilationFactory.Create(root, CompileOptions.From(project), out error, generators: false);
        if (assembled is null)
            return [];

        var hits = new List<string>();
        foreach (var tree in assembled.Compilation.SyntaxTrees)
        {
            //虚拟树没有真实路径 构建产物目录下的那些也不算项目源码
            if (!Path.IsPathRooted(tree.FilePath) || SourceFiles.IsGenerated(root, tree.FilePath))
                continue;

            if (!Uses(assembled.Compilation, tree, names))
                continue;

            hits.Add(Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/'));
        }

        return hits;
    }

    //Uses 这份源码里有没有符号出自那批程序集
    private static bool Uses(Compilation compilation, SyntaxTree tree, HashSet<string> names)
    {
        var model = compilation.GetSemanticModel(tree);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            if (node is not (IdentifierNameSyntax or GenericNameSyntax or QualifiedNameSyntax))
                continue;

            var origin = model.GetSymbolInfo(node).Symbol?.ContainingAssembly?.Name;
            if (origin is not null && names.Contains(origin))
                return true;
        }

        return false;
    }

    //Names dll 各自声明的程序集名
    private static HashSet<string> Names(IReadOnlyList<string> assemblies)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in assemblies)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                if (reader.IsAssembly)
                    names.Add(reader.GetString(reader.GetAssemblyDefinition().Name));
            }
            catch (Exception e) when (e is IOException or BadImageFormatException)
            {
                Trace.Log($"cannot read {path}: {e.Message}");
            }
        }

        return names;
    }
}
