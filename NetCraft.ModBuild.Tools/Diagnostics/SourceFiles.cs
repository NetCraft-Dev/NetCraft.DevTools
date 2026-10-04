namespace NetCraft.ModBuild.Diagnostics;

//SourceFiles 项目里的源码文件
//只跳过项目根下的 bin 与 obj 子树 项目本身装在别处的 bin 下时照样扫得到
internal static class SourceFiles
{
    //Enumerate 项目里的全部 .cs
    public static IEnumerable<string> Enumerate(string root)
    {
        var skipped = new[]
        {
            Path.Combine(root, "bin") + Path.DirectorySeparatorChar,
            Path.Combine(root, "obj") + Path.DirectorySeparatorChar,
        };

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !skipped.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }
}
