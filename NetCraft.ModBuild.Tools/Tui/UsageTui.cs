using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;
using NetCraft.ModBuild.Tools;
using Spectre.Console;

namespace NetCraft.ModBuild.Tui;

//UsageTui 扫描项目 api 用法的终端界面
//不铺多面板 只走 概览 问题表 详情 这条线 判断都交给已有的 ApiUsage 这边只负责展示与动作
//输入输出被重定向时退回一次性报告 脚本与 ci 里也能跑
internal static class UsageTui
{
    //Run 入口 返回进程退出码
    public static int Run()
    {
        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
        {
            AnsiConsole.MarkupLine("[red]error[/] the template catalog is unavailable, the api check needs it");
            return 1;
        }

        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
        {
            AnsiConsole.MarkupLine($"[red]error[/] no {ModProject.ManifestName} in this directory or any parent");
            return 1;
        }

        var root = Path.GetDirectoryName(project.ManifestPath)!;
        return IsInteractive() ? Loop(root, catalog, project) : Report(root, catalog, project);
    }

    //IsInteractive 输入输出都没被重定向才当有终端
    private static bool IsInteractive()
        => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    //Report 一次性报告 有 error 返回非零 便于钉在构建脚本里
    private static int Report(string root, TemplateCatalog catalog, ModProject project)
    {
        var report = UsageReport.Scan(root, catalog);
        report.RenderSummary(project.DisplayName);
        report.RenderTable();
        return report.ErrorCount > 0 ? 1 : 0;
    }

    //Loop 主循环 扫一次可以反复看 重新扫描才再走一遍源码
    private static int Loop(string root, TemplateCatalog catalog, ModProject project)
    {
        var report = ScanWithStatus(root, catalog, project);

        while (true)
        {
            report.RenderSummary(project.DisplayName);
            report.RenderTable();

            var entry = AskEntry(report);
            if (entry.Action == EntryAction.Quit)
                return 0;
            if (entry.Action == EntryAction.Rescan)
            {
                report = ScanWithStatus(root, catalog, project);
                continue;
            }

            var usage = entry.Usage!;
            report.RenderDetail(usage);

            switch (AskDetail())
            {
                case DetailAction.Back:
                    break;
                case DetailAction.Rescan:
                    report = ScanWithStatus(root, catalog, project);
                    break;
                case DetailAction.Quit:
                    return 0;
                case DetailAction.WriteExample:
                    WriteExample(catalog, usage);
                    break;
            }
        }
    }

    //ScanWithStatus 扫描时转个圈 这一步要读遍项目源码 没提示会像是卡住了
    private static UsageReport ScanWithStatus(string root, TemplateCatalog catalog, ModProject project)
        => AnsiConsole.Status()
            .Start($"scanning {project.DisplayName}...", _ => UsageReport.Scan(root, catalog))!;

    //AskEntry 主菜单 选一条用法 或者重新扫描 或者退出
    private static Entry AskEntry(UsageReport report)
    {
        var entries = new List<Entry>();
        for (var index = 0; index < report.Problems.Count; index++)
        {
            var item = report.Problems[index];
            entries.Add(new Entry(EntryAction.Open, $"#{index + 1}  {item.Symbol}", item));
        }

        entries.Add(new Entry(EntryAction.Rescan, "rescan the project", null));
        entries.Add(new Entry(EntryAction.Quit, "quit", null));

        return AnsiConsole.Prompt(new SelectionPrompt<Entry>()
            .Title("[grey]pick an api usage to inspect[/]")
            .PageSize(15)
            .UseConverter(entry => Markup.Escape(entry.Label))
            .AddChoices(entries));
    }

    //AskDetail 详情页的动作
    private static DetailAction AskDetail()
        => AnsiConsole.Prompt(new SelectionPrompt<DetailAction>()
            .Title("[grey]what next[/]")
            .UseConverter(action => action switch
            {
                DetailAction.WriteExample => "pull the matching template into the current directory",
                DetailAction.Back => "back to the list",
                DetailAction.Rescan => "rescan the project",
                _ => "quit",
            })
            .AddChoices(
                DetailAction.WriteExample,
                DetailAction.Back,
                DetailAction.Rescan,
                DetailAction.Quit));

    //WriteExample 把候选里能在清单里找到的条目拉到当前目录
    //候选写的是代码里的名字 清单里是完整标识 两边按类型名对齐
    private static void WriteExample(TemplateCatalog catalog, ApiUsageItem usage)
    {
        var matches = MatchingEntries(catalog, usage);
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]warning[/] no candidate of this usage has a template entry in the catalog");
            return;
        }

        var entry = matches.Count == 1
            ? matches[0]
            : AnsiConsole.Prompt(new SelectionPrompt<TemplateEntry>()
                .Title("[grey]pick a template to pull[/]")
                .PageSize(12)
                .UseConverter(item => $"{Markup.Escape(item.Id)}  [grey]{Markup.Escape(item.Title)}[/]")
                .AddChoices(matches));

        TemplateTool.PullExample(catalog, entry);
    }

    //MatchingEntries 按候选的类型名在清单里找条目 候选一个都没有时退回这处用法自己的类型名
    private static List<TemplateEntry> MatchingEntries(TemplateCatalog catalog, ApiUsageItem usage)
    {
        var names = usage.Candidates
            .Select(TypeNameOf)
            .Append(usage.Type)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        return catalog.Apis
            .Where(entry => names.Contains(LastSegment(entry.Id)))
            .ToList();
    }

    //TypeNameOf 候选写成 类型.成员 或 类型 取前面那段
    private static string TypeNameOf(string candidate)
    {
        var dot = candidate.IndexOf('.');
        return dot < 0 ? candidate : candidate[..dot];
    }

    //LastSegment 清单标识的最后一段就是代码里写的类型名
    private static string LastSegment(string id)
    {
        var dot = id.LastIndexOf('.');
        return dot < 0 ? id : id[(dot + 1)..];
    }

    //EntryAction 主菜单一行的种类
    private enum EntryAction
    {
        Open,
        Rescan,
        Quit,
    }

    //DetailAction 详情页能做的事
    private enum DetailAction
    {
        WriteExample,
        Back,
        Rescan,
        Quit,
    }

    //Entry 主菜单一行 Usage 非空表示它是一条用法
    private sealed record Entry(EntryAction Action, string Label, ApiUsageItem? Usage);
}
