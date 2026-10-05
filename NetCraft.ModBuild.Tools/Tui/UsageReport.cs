using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;
using Spectre.Console;

namespace NetCraft.ModBuild.Tui;

//UsageReport 一次扫描的结果 概览 问题表 详情都照它渲染
//tui 与重定向时的一次性报告共用同一份渲染 两边看到的东西才一致
internal sealed class UsageReport
{
    private UsageReport(string root, List<ApiUsageItem> items, int fileCount)
    {
        Root = root;
        Items = items;
        FileCount = fileCount;
        Problems = items.Where(item => item.State != UsageState.Ok).ToList();
    }

    //Root 扫的是哪个目录
    public string Root { get; }

    //Items 全部用法 有问题的排在前面
    public IReadOnlyList<ApiUsageItem> Items { get; }

    //Problems 需要处理的那些 即不是 Ok 的
    public IReadOnlyList<ApiUsageItem> Problems { get; }

    //FileCount 扫到的源码文件数
    public int FileCount { get; }

    public int ErrorCount => Items.Count(item => item.State == UsageState.Missing);

    public int WarningCount => Items.Count(item => item.State == UsageState.Warn);

    public int OkCount => Items.Count(item => item.State == UsageState.Ok);

    //Scan 扫目录 有问题的排前面 同组按符号名排
    public static UsageReport Scan(string root, TemplateCatalog catalog)
    {
        var items = ApiUsage.Scan(root, catalog).Items
            .OrderBy(item => item.State == UsageState.Ok ? 1 : 0)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .ToList();

        return new UsageReport(root, items, SourceFiles.Enumerate(root).Count());
    }

    //RenderSummary 概览 一行统计
    public void RenderSummary(string title)
    {
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"[grey]{FileCount} file(s) scanned[/]   "
            + $"[red]{ErrorCount} error(s)[/]   "
            + $"[yellow]{WarningCount} warning(s)[/]   "
            + $"[green]{OkCount} ok[/]");
        AnsiConsole.WriteLine();
    }

    //RenderTable 问题清单 干净的项目只打一行
    public void RenderTable()
    {
        if (Problems.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No api usage problem found[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("level")
            .AddColumn("symbol")
            .AddColumn("where")
            .AddColumn("fix hints");

        for (var index = 0; index < Problems.Count; index++)
        {
            var item = Problems[index];
            table.AddRow(
                new Markup($"[grey]{index + 1}[/]"),
                new Markup($"[{Color(item.State)}]{Label(item.State)}[/]"),
                new Markup(Markup.Escape(item.Symbol)),
                new Markup(Markup.Escape(Where(item))),
                new Markup(Hints(item)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    //RenderDetail 单条用法的详情 源码行与候选都给全
    public void RenderDetail(ApiUsageItem item)
    {
        var lines = new List<string>
        {
            $"[bold]{Markup.Escape(item.Symbol)}[/]",
            $"[grey]level[/]    [{Color(item.State)}]{Label(item.State)}[/]",
            $"[grey]type[/]     {Markup.Escape(item.Type)}",
            $"[grey]member[/]   {(string.IsNullOrWhiteSpace(item.Member) ? "[grey]-[/]" : Markup.Escape(item.Member))}",
        };

        if (item.Locations.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("[grey]where[/]");
            foreach (var location in item.Locations)
            {
                //源码行按原样贴出来 缩进也留着 位置对不对一眼能看出来
                lines.Add($"  [blue]{Markup.Escape(location.File)}:{location.Line}[/]");
                lines.Add($"    [grey]{Markup.Escape(location.Text.TrimEnd())}[/]");
            }
        }

        if (item.Candidates.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("[grey]candidates[/]");
            foreach (var candidate in item.Candidates)
                lines.Add($"  [green]{Markup.Escape(candidate)}[/]");
        }

        AnsiConsole.Write(new Panel(new Markup(string.Join('\n', lines)))
            .Header("[bold]usage detail[/]")
            .Border(BoxBorder.Rounded)
            .Expand());
        AnsiConsole.WriteLine();
    }

    //Color 级别对应的颜色 判断逻辑与诊断那边一致
    private static string Color(UsageState state) => state switch
    {
        UsageState.Missing => "red",
        UsageState.Warn => "yellow",
        _ => "green",
    };

    //Label 级别在终端里的叫法 与诊断的 error/warning 对齐
    private static string Label(UsageState state) => state switch
    {
        UsageState.Missing => "error",
        UsageState.Warn => "warning",
        _ => "ok",
    };

    //Where 第一个出现位置 还有别处时补一个计数
    private static string Where(ApiUsageItem item)
    {
        if (item.Locations.Count == 0)
            return "?";

        var first = item.Locations[0];
        var text = $"{first.File}:{first.Line}";
        return item.Locations.Count > 1 ? $"{text} (+{item.Locations.Count - 1})" : text;
    }

    //Hints 表格里只放前几个候选 全量留给详情页
    private static string Hints(ApiUsageItem item)
        => item.Candidates.Count == 0
            ? "[grey]-[/]"
            : Markup.Escape(string.Join(", ", item.Candidates.Take(3)));
}
