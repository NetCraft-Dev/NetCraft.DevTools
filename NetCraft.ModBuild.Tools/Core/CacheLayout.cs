namespace NetCraft.ModBuild.Core;

//The spots ncm occupies in the user directory; living there keeps them stable across upgrades and reinstalls and shared between installed projects
internal static class CacheLayout
{
    //Product directory under the user folder, falling back to the program root when unavailable so downloads still have a home
    public static string Home { get; } = Resolve();

    //Download cache root
    public static string Root { get; } = Path.Combine(Home, "ncm");

    //Packages and scripts from self-update, kept apart from the cache so they are not cleared like disposable cache
    public static string Update { get; } = Path.Combine(Home, "Update");

    //Vanilla client jar
    public static string Client => Path.Combine(Root, "Client");

    //Server runtime and kernel
    public static string Server => Path.Combine(Root, "Server");

    //Template catalog and example files
    public static string Template => Path.Combine(Root, "Template");

    //Packages that miss the global nuget cache
    public static string Packages => Path.Combine(Root, "packages");

    //Compute the product directory; LocalApplicationData is empty in some environments, so fall back to the program root then
    private static string Resolve()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : Path.Combine(local, "NetCraft");
    }
}
