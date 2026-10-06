using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Gui;
using NetCraft.ModBuild.Tui;

namespace NetCraft.ModBuild.Tools;

//TemplateTool 浏览与拉取模组开发模板
//清单与示例文件都缓存在程序根目录的 Template 下 只有本地没有时才联网
internal static class TemplateTool
{
    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("template", "Browse and pull mod templates", Run,
        [
            new("view [pattern]", "List template entries, ? and * work as wildcards"),
            new("example <api id>", "Pull the example file of an entry into the current directory"),
            new("gui", "Open the template panel in a window"),
            new("tui", "Open the terminal panel, grading the api usage of the project"),
            new("--refresh", "Clear the local cache first and pull everything again"),
        ]);

    //RefreshOption 清掉本地缓存再重新拉 放在子命令前后都行
    private const string RefreshOption = "--refresh";

    //Run 第一个非选项参数选子命令
    private static int Run(string[] args)
    {
        //模板来源跟着当前目录的项目配置走 没有工程就用内置那个
        TemplateStore.Configure(NcProject.TryFind(Environment.CurrentDirectory, out _));

        var refresh = false;
        var rest = new List<string>(args.Length);

        foreach (var arg in args)
        {
            if (arg == RefreshOption)
                refresh = true;
            else
                rest.Add(arg);
        }

        if (refresh)
        {
            //下载那条路也要知道这次是刷新 否则还是会命中 CDN 那份旧副本
            TemplateStore.Refresh = true;
            if (!ClearCache())
                return 1;
        }

        var remaining = rest.ToArray();
        if (remaining.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return remaining[0] switch
        {
            "view" => View(remaining[1..]),
            "example" => Example(remaining[1..]),
            "gui" => Gui(),
            "tui" => Tui(),
            _ => Unknown(remaining[0]),
        };
    }

    //ClearCache 删掉整个缓存目录 之后照常走「本地没有就下载」那条路
    private static bool ClearCache()
    {
        var root = TemplateStore.Root;
        if (!Directory.Exists(root))
        {
            Console.WriteLine($"Template cache is already empty: {root}");
            return true;
        }

        try
        {
            Directory.Delete(root, recursive: true);
            Console.WriteLine($"Cleared template cache: {root}");
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"error: could not clear {root}: {e.Message}");
            return false;
        }
    }

    //View 按模式列出条目 不带模式列全部
    private static int View(string[] args)
    {
        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
            return 1;

        var pattern = args.Length > 0 ? args[0] : null;
        var matched = catalog.Apis.Where(entry => TemplateCatalog.Matches(entry.Id, pattern)).ToList();
        if (matched.Count == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(pattern)
                ? "The template catalog has no entries"
                : $"No template entry matches {pattern}");
            return 1;
        }

        foreach (var entry in matched)
        {
            Console.WriteLine(entry.Id);
            if (!string.IsNullOrWhiteSpace(entry.Title))
                Console.WriteLine($"    {entry.Title}");
            if (!string.IsNullOrWhiteSpace(entry.Summary))
                Console.WriteLine($"    {entry.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine($"{matched.Count} entry(s)");
        return 0;
    }

    //Example 按 id 找到条目再把模板文件拉到当前目录
    private static int Example(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: ncm template example <api id>");
            return 1;
        }

        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
            return 1;

        var id = args[0];
        var entry = catalog.Apis.FirstOrDefault(
            candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Console.WriteLine($"No template entry with id {id}, run 'ncm template view' to list them");
            return 1;
        }

        return PullExample(catalog, entry);
    }

    //PullExample 把一个条目的示例拉到当前目录 供 example 子命令与 tui 共用
    //缓存命中就不联网 目标已存在时问一句
    internal static int PullExample(TemplateCatalog catalog, TemplateEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Template))
        {
            Console.WriteLine($"{entry.Id} does not declare a template file");
            return 1;
        }

        var url = catalog.ResolveUrl(entry.Template);
        var source = TemplateStore.FetchFile(url, entry.Template, out var error);
        if (source is null)
        {
            Console.WriteLine($"Failed to fetch {url}: {error}");
            return 1;
        }

        var target = Path.Combine(Environment.CurrentDirectory, Path.GetFileName(entry.Template));
        if (File.Exists(target) && !ConfirmOverwrite(target))
            return 0;

        //底稿里留着两个占位标识 这里照当前项目填掉 不在项目里就统一用 Example
        var text = Fill(File.ReadAllText(source), ModProject.TryFind(Environment.CurrentDirectory), entry.Id);
        File.WriteAllText(target, text);

        Console.WriteLine($"Written to {target}");
        return 0;
    }

    //NamespaceToken/ClassToken 底稿里留的两个占位标识
    private const string NamespaceToken = "__MOD_NAMESPACE__";
    private const string ClassToken = "__MOD_CLASS__";

    //FallbackName 不在模组项目里时用的名字
    private const string FallbackName = "Example";

    //Fill 把底稿里的占位标识换成当前项目对应的名字
    //在项目里命名空间取清单 entry 推出来的那个 类名用 api 名加 Example
    //不在项目里两处都退成 Example
    private static string Fill(string text, ModProject? project, string apiId)
    {
        var inside = project is not null && !string.IsNullOrEmpty(project.Namespace);
        var namespaceName = inside ? project!.Namespace : FallbackName;
        var className = inside ? apiId.Split('.')[^1] + "Example" : FallbackName;

        return text
            .Replace(NamespaceToken, namespaceName)
            .Replace(ClassToken, className);
    }

    //Gui 打开图形面板 清单取不到也照常开 窗口里会说明原因
    private static int Gui()
    {
        var catalog = TemplateStore.LoadCatalog();
        TemplateWindow.Show(catalog);
        return 0;
    }

    //Tui 打开终端面板 侧重扫项目里的 api 用法
    private static int Tui() => UsageTui.Run();

    //Unknown 子命令没认出来
    private static int Unknown(string command)
    {
        Console.WriteLine($"Unknown template command: {command}");
        PrintUsage();
        return 1;
    }

    //ConfirmOverwrite 目标已存在时问一句 回车算不覆盖
    private static bool ConfirmOverwrite(string path)
    {
        Console.Write($"{Path.GetFileName(path)} already exists. Overwrite? (y/N) ");
        var answer = Console.ReadLine();
        return answer is not null && answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    //PrintUsage 子命令一览
    private static void PrintUsage()
    {
        Console.WriteLine("Usage: ncm template <command>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  view [pattern]     List template entries, ? and * work as wildcards");
        Console.WriteLine("  example <api id>   Pull the example file of an entry into the current directory");
        Console.WriteLine("  gui                Open the template panel in a window");
        Console.WriteLine("  tui                Open the terminal panel, grading the api usage of the project");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --refresh          Clear the local cache first and pull everything again");
    }
}
