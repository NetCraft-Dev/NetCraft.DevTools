namespace NetCraft.ModBuild.Diagnostics;

//Applicability 一条建议的可信度 与 rustc 的四档对齐
public enum Applicability
{
    //MachineApplicable 照着改一定对 机器可以直接应用
    MachineApplicable,
    //HasPlaceholders 骨架对 里面还留着要人补的占位
    HasPlaceholders,
    //MaybeIncorrect 是猜的 需要人确认
    MaybeIncorrect,
    //Unspecified 说不准
    Unspecified,
}

//Suggestion 一条修复建议
//带 Replacement 的画成改后的代码 只有 Message 的当纯文字提示
//Span 用 1 起算的行列 与 Diagnostic 那套保持一致
public sealed record Suggestion(
    string Message,
    int Line,
    int Column,
    int Length,
    string? Replacement,
    Applicability Applicability)
{
    //Replaceable 这条能不能给出可替换的正文
    public bool Replaceable => Replacement is not null && Length > 0;

    //Text 纯文字提示 没有指向源码的落点
    public static Suggestion Text(string message)
        => new(message, 0, 0, 0, null, Applicability.Unspecified);

    //Replace 可替换的建议 指向某一行的某一段
    public static Suggestion Replace(
        string message,
        int line,
        int column,
        int length,
        string replacement,
        Applicability applicability)
        => new(message, line, column, length, replacement, applicability);
}
