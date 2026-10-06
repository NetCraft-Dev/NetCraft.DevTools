namespace NetCraft.ModBuild.Compile;

//Incremental 增量判定
//照搬 MSBuild 那套 拿输入跟输出比时间戳 输出不比任何输入旧就不用重编
//MSBuild 也只看时间戳 切分支或改系统时间造成的漏编它一样挡不住
internal static class Incremental
{
    //IsUpToDate 输出在且不比任何输入旧
    //输入清单里列不全就会漏判 所以那边宁多勿少
    public static bool IsUpToDate(string target, IEnumerable<string> inputs)
    {
        var output = new FileInfo(target);
        if (!output.Exists || output.Length == 0)
            return false;

        foreach (var path in inputs)
        {
            var input = new FileInfo(path);
            if (!input.Exists)
                continue;

            if (input.LastWriteTimeUtc > output.LastWriteTimeUtc)
                return false;
        }

        return true;
    }
}
