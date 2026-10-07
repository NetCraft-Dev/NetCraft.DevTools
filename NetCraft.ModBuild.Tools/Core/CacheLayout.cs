namespace NetCraft.ModBuild.Core;

//CacheLayout ncm's own directories under the user folder
//living there keeps them stable across upgrades and reinstalls and shared between installed projects
internal static class CacheLayout
{
    //Product folder under the user folder, falling back to the program root when unavailable so downloads still have a home
    private static string Home { get; } = Resolve();

    //Root of everything ncm keeps for itself
    public static string Root { get; } = Path.Combine(Home, "ncm");

    //Packages and scripts from self update, kept apart from the disposable caches
    public static string Update { get; } = Path.Combine(Root, "Update");

    //Installed plugins, one folder per plugin holding its dlls
    public static string Tool { get; } = Path.Combine(Root, "Tool");

    //Vanilla client jar
    public static string Client => Path.Combine(Root, "Client");

    //Server runtime and kernel
    public static string Server => Path.Combine(Root, "Server");

    //Template catalog and example files
    public static string Template => Path.Combine(Root, "Template");

    //Packages that miss the global nuget cache
    public static string Packages => Path.Combine(Root, "packages");

    //Compute the product folder; LocalApplicationData is empty in some environments, so fall back to the program root then
    private static string Resolve()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : Path.Combine(local, "NetCraft");
    }
}
