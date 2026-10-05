using System.Text.Json;
using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Gui;

//UiPage 拼出面板页面
//文档与用法表在起窗口前就备好 直接写进页面里的一个 JSON 变量 不走消息桥来回问
internal static class UiPage
{
    //DataToken 页面里留给数据的位置
    private const string DataToken = "/*DATA*/";

    //MarkedToken 页面里留给 markdown 渲染库的位置
    private const string MarkedToken = "/*MARKED*/";

    private static readonly Lazy<string> Page = new(() => Resource("ui.html"));
    private static readonly Lazy<string> Marked = new(() => Resource("marked.min.js"));

    //Json 页面里读的字段名是小写驼峰 序列化跟着转 键与字符串不动
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    //Build 把文档与用法表塞进页面
    public static string Build(TemplateCatalog? catalog)
    {
        var data = JsonSerializer.Serialize(new Payload(LoadDocument(catalog), ScanUsage(catalog)), Json);
        Trace.Log($"page payload {data.Length} chars");
        return Page.Value.Replace(MarkedToken, Marked.Value).Replace(DataToken, data);
    }

    //ScanUsage 在模组项目里时扫一遍源码 不在项目里就交一张空表
    //空表只是让页面不显示侧边栏 不会去打扰用户
    private static List<Usage> ScanUsage(TemplateCatalog? catalog)
    {
        var items = new List<Usage>();
        if (catalog is null)
            return items;

        Trace.Log($"current directory {Environment.CurrentDirectory}");

        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
            return items;

        var root = Path.GetDirectoryName(project.ManifestPath)!;
        Trace.Log($"project root {root}");

        var usage = ApiUsage.Scan(root, catalog);

        //有问题的排前面 同组按名字排 面板上要改的地方一眼能看到
        var ordered = usage.Items
            .OrderBy(item => item.State == UsageState.Ok ? 1 : 0)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal);

        foreach (var item in ordered)
        {
            items.Add(new Usage(
                item.Symbol,
                item.State.ToString().ToLowerInvariant(),
                item.Type,
                item.Member,
                item.Candidates,
                item.Locations));
        }

        Console.WriteLine($"Template panel scanned {items.Count} api usage(s)");
        return items;
    }

    //LoadDocument 取清单指定的文档 取不到时把原因当成一段 markdown 交出去
    private static string LoadDocument(TemplateCatalog? catalog)
    {
        if (catalog is null)
            return "# No catalog available\n\nThe catalog could not be loaded. Check the network, or the local `Template` cache directory.";

        if (string.IsNullOrWhiteSpace(catalog.Document))
            return "# The catalog declares no document\n\nAdd a `document` field to `NetCraftTemplate.yaml`.";

        var url = catalog.ResolveUrl(catalog.Document);
        var path = TemplateStore.FetchFile(url, catalog.Document, out var error);
        if (path is null)
            return $"# The document could not be fetched\n\n`{url}`\n\n```\n{error}\n```";

        return File.ReadAllText(path);
    }

    //Resource 读同程序集里的内嵌资源
    private static string Resource(string name)
    {
        using var stream = typeof(UiPage).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    //Payload 页面要的数据 字段名就是页面里读的那几个
    private sealed record Payload(string Document, List<Usage> Usage);

    //Usage 侧边栏里的一条用法
    private sealed record Usage(
        string Symbol,
        string State,
        string Type,
        string Member,
        List<string> Candidates,
        List<UsageLocation> Locations);
}
