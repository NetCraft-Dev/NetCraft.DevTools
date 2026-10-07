namespace NetCraft.ModBuild.Diagnostics;

//SourceFiles enumerates the project's source files
//any path segment named bin or obj is skipped as build output, including those under nested projects
internal static class SourceFiles
{
    //SkippedDirectories holds build output directory names compared as whole segments so names like binary are not caught
    private static readonly string[] SkippedDirectories = { "bin", "obj" };

    //Enumerate lists all .cs files in the project
    public static IEnumerable<string> Enumerate(string root) => Enumerate(root, "*.cs");

    //Enumerate overload taking a pattern, used for files like the axaml the generator reads
    public static IEnumerable<string> Enumerate(string root, string pattern)
    {
        foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            if (!IsGenerated(root, path))
                yield return path;
        }
    }

    //IsGenerated checks whether the path relative to root runs through a build output directory
    internal static bool IsGenerated(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
