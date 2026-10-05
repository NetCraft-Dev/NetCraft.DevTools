using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetCraft.ModBuild.Core;

//ModProject 当前所在的模组项目
//判定只看有没有 ncmod.json 清单 图标文本取清单的 name 空则退回 id
public sealed class ModProject
{
    //ManifestName 清单文件名 加载器与工具都只认这一个名字
    public const string ManifestName = "ncmod.json";

    //DefaultIconName 清单没写 icon 时约定的图标文件名
    public const string DefaultIconName = "icon.png";

    private ModProject(string manifestPath, string displayName, string iconPath, string namespaceName)
    {
        ManifestPath = manifestPath;
        DisplayName = displayName;
        IconPath = iconPath;
        Namespace = namespaceName;
    }

    //ManifestPath 清单文件的完整路径
    public string ManifestPath { get; }

    //DisplayName 图标上写的名字
    public string DisplayName { get; }

    //IconPath 图标文件的完整路径 取自清单的 icon 字段
    public string IconPath { get; }

    //Namespace 清单 entry 推出来的命名空间 模板底稿里的占位标识照它填
    //entry 形如 NetCraft.Test1.ModEntry 去掉最后一段就是命名空间 推不出时为空
    public string Namespace { get; }

    //TryFind 从指定目录起逐级向上找模组项目 找不到返回 null
    //在子目录里执行也能落到项目根 完全不在项目里才算没找到
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

    //Read 读清单里的 name id 与 icon
    //清单损坏或读不出名字按没有项目处理 静默退出比抛一堆栈更合适
    private static ModProject? Read(string manifestPath, string directory)
    {
        string? name;
        string? id;
        string? icon;
        string? entry;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            name = GetString(root, "name");
            id = GetString(root, "id");
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
            Path.GetFullPath(Path.Combine(directory, iconName)),
            NamespaceOf(entry));
    }

    //NamespaceOf 从 entry 里推出命名空间 只有类名没有命名空间时返回空
    private static string NamespaceOf(string? entry)
    {
        var lastDot = entry?.LastIndexOf('.') ?? -1;
        return lastDot > 0 ? entry![..lastDot] : string.Empty;
    }

    //EnsureIcon 清单缺 icon 字段时补上 返回是否真的改过文件
    //已有非空取值就原样不动 生成位置本来就是照它来的
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

    //Edit 把清单读成可改对象交给回调 回调返回 true 才写回 返回是否真的改过文件
    //清单损坏时按没改过处理 静默跳过比抛一堆栈更合适
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

    //GetString 取一个字符串字段 没有或类型不符返回 null
    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
