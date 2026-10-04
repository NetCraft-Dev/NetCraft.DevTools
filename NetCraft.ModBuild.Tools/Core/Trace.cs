namespace NetCraft.ModBuild.Core;

//Trace 调试输出开关
//设了 NCM_DEBUG 环境变量才出声 平时不该往控制台刷东西
//用来查项目定位与用法扫描这种看不见的过程
public static class Trace
{
    //Enabled 当前是否开了调试
    public static bool Enabled { get; } = IsOn();

    //Log 打印一行调试信息
    public static void Log(string message)
    {
        if (Enabled)
            Console.WriteLine($"[debug] {message}");
    }

    //IsOn 环境变量有值且不是 0 或 false 就算开
    private static bool IsOn()
    {
        var value = Environment.GetEnvironmentVariable("NCM_DEBUG");
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }
}
