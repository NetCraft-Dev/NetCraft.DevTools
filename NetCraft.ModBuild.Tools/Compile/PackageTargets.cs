using System.Text.RegularExpressions;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Compile;

//PackageTargets 把包带来的 props 与 targets 收进项目
//收进来项目就自持了 清掉包缓存或者换台机器照样能构建
//targets 里用 MSBuildThisFileDirectory 指到的文件也一并带上 不然任务程序集找不到
internal static class PackageTargets
{
    //DirectoryOf 收集落点 项目 Build 下的 targets 目录
    private static string DirectoryOf(string root) => Path.Combine(root, ProjectLayout.Targets);

    //Folders nuget 约定直接引用看 build 传递依赖看 buildTransitive 两边都收会重复导入同一批目标
    private static readonly string[] DirectFolders = ["build", "buildMultiTargeting"];
    private static readonly string[] TransitiveFolders = ["buildTransitive"];

    //Collect 收一个包 返回收进来几个文件
    public static int Collect(string root, string source, string id, bool direct)
    {
        var destination = Path.Combine(DirectoryOf(root), id);
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in direct ? DirectFolders : TransitiveFolders)
        {
            var directory = Path.Combine(source, folder);
            if (!Directory.Exists(directory))
                continue;

            var files = Directory.EnumerateFiles(directory, "*.props", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(directory, "*.targets", SearchOption.AllDirectories))
                .ToList();

            foreach (var file in files)
            {
                Copy(file, source, destination, copied);
                foreach (var reference in References(file))
                    Copy(reference, source, destination, copied);
            }
        }

        return copied.Count;
    }

    //Imports 项目里收好的那份清单 props 一律排在 targets 前面
    //顺序按落点目录的第一个子目录名定 也就是包名 同一个包内按路径比
    public static IEnumerable<string> Imports(string root)
    {
        var directory = DirectoryOf(root);
        if (!Directory.Exists(directory))
            yield break;

        foreach (var pattern in new[] { "*.props", "*.targets" })
        {
            var files = Directory.EnumerateDirectories(directory)
                .Where(path => !LooksLikeIntermediate(path))
                .SelectMany(path => Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
                yield return file;
        }
    }

    //LooksLikeIntermediate 中间目录不是包 别把里面的东西当成导入项
    private static bool LooksLikeIntermediate(string path)
        => Path.GetFileName(path).Equals("obj", StringComparison.OrdinalIgnoreCase);

    //Copy 按包内相对结构复制过去 结构不能变 targets 之间都是靠相对路径互相找的
    private static void Copy(string path, string source, string destination, HashSet<string> copied)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            return;

        var target = Path.Combine(destination, Path.GetRelativePath(source, full));
        if (!copied.Add(target))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(full, target, overwrite: true);
        }
        catch (IOException e)
        {
            Trace.Log($"cannot collect {full}: {e.Message}");
        }
    }

    //References 从文本里挑出 MSBuildThisFileDirectory 指到的路径
    //带属性的拼不出来 直接跳过 剩下的包自己会抱怨
    private static IEnumerable<string> References(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            yield break;
        }

        var directory = Path.GetDirectoryName(path)!;
        foreach (Match match in Regex.Matches(text, @"\$\(MSBuildThisFileDirectory\)([^""'<>;\s]*)", RegexOptions.IgnoreCase))
        {
            //tail 常以分隔符开头 直接 combine 会把前半段丢掉
            var tail = match.Groups[1].Value.TrimStart('\\', '/');
            if (tail.Length == 0 || tail.Contains("$(") || tail.IndexOfAny(['*', '?']) >= 0)
                continue;

            var candidate = Path.GetFullPath(Path.Combine(directory, tail));
            if (File.Exists(candidate))
            {
                yield return candidate;
                continue;
            }

            if (!Directory.Exists(candidate))
                continue;

            foreach (var file in Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories))
                yield return file;
        }
    }
}
