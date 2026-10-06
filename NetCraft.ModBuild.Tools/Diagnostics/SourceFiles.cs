namespace NetCraft.ModBuild.Diagnostics;

//SourceFiles 项目里的源码文件
//路径里任何一层只要叫 bin 或 obj 就跳过 那是构建产物 子项目里的同样算
internal static class SourceFiles
{
    //SkippedDirectories 构建产物目录名 按整段比 免得把 binary 这类名字误伤
    private static readonly string[] SkippedDirectories = { "bin", "obj" };

    //Enumerate 项目里的全部 .cs
    public static IEnumerable<string> Enumerate(string root) => Enumerate(root, "*.cs");

    //Enumerate 按后缀收文件 生成器要读的 axaml 之类从这条走
    public static IEnumerable<string> Enumerate(string root, string pattern)
    {
        foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            if (!IsGenerated(root, path))
                yield return path;
        }
    }

    //IsGenerated 相对 root 的路径里有没有落在构建产物目录下的那一段
    internal static bool IsGenerated(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
