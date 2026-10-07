using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetCraft.ModBuild.Core;

//The mod project currently in scope; membership is decided solely by ncmod.json and the icon text uses the manifest name, falling back to id
public sealed class ModProject
{
    //Manifest file name; both the loader and the tools recognize only this one
    public const string ManifestName = "ncmod.json";

    //Conventional icon file name used when the manifest omits icon
    public const string DefaultIconName = "icon.png";

    private ModProject(string manifestPath, string displayName, string id, string version, string iconPath,
        string namespaceName)
    {
        ManifestPath = manifestPath;
        DisplayName = displayName;
        Id = id;
        Version = version;
        IconPath = iconPath;
        Namespace = namespaceName;
    }

    public string ManifestPath { get; }

    public string DisplayName { get; }

    //Mod id from the manifest, exposed as $(ModId) in task interpolation
    public string Id { get; }

    //Version from the manifest, exposed as $(ModVersion) in task interpolation
    public string Version { get; }

    //Full icon file path taken from the manifest's icon field
    public string IconPath { get; }

    //Namespace derived from the manifest entry and used to fill placeholders in template drafts; an entry like NetCraft.Test1.ModEntry drops its last segment and empty means it cannot be derived
    public string Namespace { get; }

    //Walk upward from the given directory looking for a mod project; running in a subdirectory still finds the root and only being fully outside counts as missing
    public static ModProject? TryFind(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var manifest = Path.Combine(current.FullName, ManifestName);
            if (File.Exists(manifest))
            {
                Trace.Log($"found mod manifest {manifest}");
                return Read(manifest, current.FullName);
            }
        }

        Trace.Log($"no {ManifestName} from {directory} upward, not inside a mod project");
        return null;
    }

    //Read name, id and icon from the manifest; a corrupt manifest or missing name counts as no project since exiting quietly beats dumping a stack
    private static ModProject? Read(string manifestPath, string directory)
    {
        string? name;
        string? id;
        string? version;
        string? icon;
        string? entry;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            name = GetString(root, "name");
            id = GetString(root, "id");
            version = GetString(root, "version");
            icon = GetString(root, "icon");
            entry = GetString(root, "entry");
        }
        catch (JsonException e)
        {
            Trace.Log($"manifest {manifestPath} is not valid json: {e.Message}");
            return null;
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? id : name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            Trace.Log($"manifest {manifestPath} has neither name nor id, treating it as no project");
            return null;
        }

        var iconName = string.IsNullOrWhiteSpace(icon) ? DefaultIconName : icon;
        return new ModProject(
            manifestPath,
            displayName,
            id ?? string.Empty,
            version ?? string.Empty,
            Path.GetFullPath(Path.Combine(directory, iconName)),
            NamespaceOf(entry));
    }

    //Derive the namespace from the entry, returning empty when only a class name is present
    private static string NamespaceOf(string? entry)
    {
        var lastDot = entry?.LastIndexOf('.') ?? -1;
        return lastDot > 0 ? entry![..lastDot] : string.Empty;
    }

    //Add an icon field when the manifest lacks one and report whether the file changed; an existing non-empty value is left alone
    public bool EnsureIcon(string iconFileName)
        => Edit(manifest =>
        {
            if (manifest.TryGetPropertyValue("icon", out var existing)
                && existing is JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
                return false;

            manifest["icon"] = iconFileName;
            return true;
        });

    //Load the manifest as an editable object and write it back only when the callback returns true; a corrupt manifest counts as unchanged since skipping quietly beats dumping a stack
    public bool Edit(Func<JsonObject, bool> change)
    {
        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(ManifestPath)) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (manifest is null || !change(manifest))
            return false;

        File.WriteAllText(ManifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return true;
    }

    //Read a string field, returning null when missing or of the wrong type
    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
