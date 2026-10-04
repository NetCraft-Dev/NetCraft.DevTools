namespace NetCraft.ModBuild.Diagnostics;

//DiagnosticSeverity 一条诊断的轻重
public enum DiagnosticSeverity
{
    //Warning 提示但放行
    Warning,
    //Error 拦住构建
    Error,
}

//Diagnostic 一条诊断
//File Line Column 都用来定位 渲染时按 cargo 那样画出行号与指示箭头
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string File,
    int Line,
    int Column,
    int Length,
    string SourceLine,
    string Note,
    string FixTitle,
    IReadOnlyList<string> Fixes);

//DiagnosticBag 一次检查收集到的全部诊断
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    //ErrorCount 错误数
    public int ErrorCount => _items.Count(item => item.Severity == DiagnosticSeverity.Error);

    //WarningCount 警告数
    public int WarningCount => _items.Count(item => item.Severity == DiagnosticSeverity.Warning);

    //HasErrors 有没有拦构建的错误
    public bool HasErrors => ErrorCount > 0;

    //Add 收一条诊断
    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);

    //Sorted 按文件与位置排好 同一批源码每次输出的顺序都一致
    public IEnumerable<Diagnostic> Sorted()
        => _items
            .OrderBy(item => item.File, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.Column);
}
