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

        if (diagnostic.Fixes.Count > 0 && !string.IsNullOrWhiteSpace(diagnostic.FixTitle))
        {
            WriteColored("help", ConsoleColor.Cyan);
            Console.WriteLine($": {diagnostic.FixTitle}");
            Console.WriteLine($"{pad} |");
            foreach (var fix in diagnostic.Fixes)
                Console.WriteLine($"{pad} = {fix}");
        }

        Console.WriteLine();
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
