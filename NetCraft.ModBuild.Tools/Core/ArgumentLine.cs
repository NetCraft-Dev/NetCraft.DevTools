namespace NetCraft.ModBuild.Core;

//ArgumentLine 把配置里写的一行参数拆成一条条命令行参数
//按空白拆 双引号能裹住带空格的取值 引号本身不留在结果里
internal static class ArgumentLine
{
    //Split 拆一行 空行拆出空表
    public static string[] Split(string line)
    {
        var parts = new List<string>();
        var builder = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;

        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (started)
                {
                    parts.Add(builder.ToString());
                    builder.Clear();
                    started = false;
                }
                continue;
            }

            builder.Append(character);
            started = true;
        }

        //收尾时还攒着东西就是最后一个参数 引号没闭合也认它
        if (started)
            parts.Add(builder.ToString());

        return parts.ToArray();
    }
}
