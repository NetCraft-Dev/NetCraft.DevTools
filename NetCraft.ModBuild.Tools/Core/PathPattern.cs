using System.Text;
using System.Text.RegularExpressions;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Core;

//PathPattern 相对项目根的模式匹配
//* 与 ? 只在同一段里生效 ** 能跨目录
//通配符之前那一段当基准 免得把整棵树扫一遍
internal static class PathPattern
{
    //Match 展开一个模式 命中的是一批文件路径
    //构建产物目录下的文件一律不算 那是上一次编出来的
    public static IEnumerable<string> Match(string root, string pattern)
    {
        var normalized = pattern.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        if (normalized.Length == 0)
            yield break;

        var wildcard = normalized.IndexOfAny(['*', '?']);
        if (wildcard < 0)
        {
            //没有通配符时它可能直接指着某个文件
            var direct = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(direct))
            {
                if (!SourceFiles.IsGenerated(root, direct))
                    yield return direct;
                yield break;
            }
        }

        var expression = wildcard < 0 ? null : new Regex(Glob(normalized), RegexOptions.IgnoreCase);
        var slash = wildcard < 0 ? normalized.LastIndexOf('/') : normalized.LastIndexOf('/', wildcard);
        var baseDirectory = slash <= 0
            ? root
            : Path.Combine(root, normalized[..slash].Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(baseDirectory))
            yield break;

        foreach (var path in Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories))
        {
            if (SourceFiles.IsGenerated(root, path))
                continue;

            //没有通配符又指着目录时整棵子树都算
            if (expression is null || expression.IsMatch(Relative(root, path)))
                yield return path;
        }
    }

    //Glob 通配模式换成正则 ** 跨目录 * 与 ? 只在同一段里生效
    private static string Glob(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                builder.Append(".*");
                index++;
            }
            else if (character == '*')
                builder.Append("[^/]*");
            else if (character == '?')
                builder.Append("[^/]");
            else
                builder.Append(Regex.Escape(character.ToString()));
        }

        return builder.Append('$').ToString();
    }

    //Relative 相对项目根的路径 统一用斜杠
    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}
