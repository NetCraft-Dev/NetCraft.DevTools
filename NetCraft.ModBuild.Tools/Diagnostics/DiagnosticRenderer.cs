using System.Globalization;

namespace NetCraft.ModBuild.Diagnostics;

//DiagnosticRenderer 把诊断画成 rustc 那样的块
//一条诊断一段 位置行加指示箭头 下面是说明与修复候选
public static class DiagnosticRenderer
{
    //Render 依次输出全部诊断
    public static void Render(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            RenderOne(diagnostic);
    }

    private static void RenderOne(Diagnostic diagnostic)
    {
        var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        var color = diagnostic.Severity == DiagnosticSeverity.Error ? ConsoleColor.Red : ConsoleColor.Yellow;

        //行号占几列 后面那些竖线就缩进几列 对不齐会很难看
        var pad = new string(' ', diagnostic.Line.ToString(CultureInfo.InvariantCulture).Length);

        WriteColored($"{severity}[{diagnostic.Code}]", color);
        Console.WriteLine($": {diagnostic.Message}");

        Console.WriteLine($"{pad}--> {diagnostic.File}:{diagnostic.Line}:{diagnostic.Column}");

        if (!string.IsNullOrEmpty(diagnostic.SourceLine))
        {
            Console.WriteLine($"{pad} |");
            Console.WriteLine($"{diagnostic.Line} | {diagnostic.SourceLine}");
            Console.WriteLine($"{pad} | {Indent(diagnostic.Column)}{new string('^', Math.Max(1, diagnostic.Length))}");
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.Note))
        {
            Console.WriteLine($"{pad} |");
            Console.WriteLine($"{pad} = {diagnostic.Note}");
        }

        //带落点的建议一条一段 画成原行与改后的行
        foreach (var fix in diagnostic.Fixes)
        {
            if (fix.Replaceable)
                RenderReplacement(pad, diagnostic, fix);
        }

        //没有落点的文字建议直接列出来 不再加一层套话标题
        var advice = diagnostic.Fixes.Where(fix => !fix.Replaceable).ToList();
        if (advice.Count > 0)
        {
            Console.WriteLine($"{pad} |");
            foreach (var fix in advice)
                Console.WriteLine($"{pad} = {fix.Message}");
        }
        //一条建议都给不出时才拿文档地址兜底 可替换的那种也算给了
        else if (diagnostic.Fixes.Count == 0 && !string.IsNullOrWhiteSpace(diagnostic.Link))
        {
            Console.WriteLine($"{pad} |");
            Console.WriteLine($"{pad} = {LinkText(diagnostic.Link)}");
        }

        Console.WriteLine();
    }

    //RenderReplacement 画一条替换建议 原行标减 改后的行标加
    private static void RenderReplacement(string pad, Diagnostic diagnostic, Suggestion fix)
    {
        //建议落在别行时手上没有那行的原文 退回文字 免得画出对不上的代码
        if (fix.Line != diagnostic.Line || string.IsNullOrEmpty(diagnostic.SourceLine))
        {
            Console.WriteLine($"{pad} = {fix.Message}");
            return;
        }

        var updated = Replace(diagnostic.SourceLine, fix);
        if (updated is null)
        {
            Console.WriteLine($"{pad} = {fix.Message}");
            return;
        }

        var gutter = fix.Line.ToString(CultureInfo.InvariantCulture).PadLeft(pad.Length);

        WriteColored("help", ConsoleColor.Cyan);
        Console.WriteLine($": {fix.Message}");
        Console.WriteLine($"{pad} |");

        WriteColored($"{gutter} - {diagnostic.SourceLine}", ConsoleColor.Red);
        Console.WriteLine();
        WriteColored($"{gutter} + {updated}", ConsoleColor.Green);
        Console.WriteLine();
    }

    //Replace 拿建议里的正文盖掉行内的那一段
    private static string? Replace(string line, Suggestion fix)
    {
        var start = fix.Column - 1;
        if (start < 0 || start > line.Length)
            return null;

        var end = start + fix.Length;
        if (end > line.Length)
            return null;

        return line[..start] + fix.Replacement + line[end..];
    }

    //LinkText 把文档地址渲染成终端里可点的一段文字
    //认不出终端支持超链接就退回带上地址的纯文本 免得看不出来指向哪
    private static string LinkText(string url)
        => SupportsLinks()
            ? $"\u001b[36m\u001b[4m\u001b]8;;{url}\u001b\\docs\u001b]8;;\u001b\\\u001b[0m"
            : $"docs: {url}";

    //SupportsLinks 当前终端认不认 OSC 8 超链接
    //输出被重定向时一律不发 免得往文件里塞转义序列
    private static bool SupportsLinks()
    {
        if (Console.IsOutputRedirected)
            return false;

        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term) && !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return true;

        //Windows 上没有 TERM 这套 认这两个终端标记 Windows Terminal 与 VS Code
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM_PROGRAM"));
    }

    //Indent 列号从 1 起 转成箭头前要留的空格
    private static string Indent(int column)
        => column <= 1 ? string.Empty : new string(' ', column - 1);

    //WriteColored 临时换个颜色写一段 写完恢复
    private static void WriteColored(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
