namespace NetCraft.ModBuild.Core;

//FormField 表单里的一行
public sealed class FormField(string key, string label, bool required = false)
{
    //Key 取值时用来认出这一行
    public string Key { get; } = key;

    //Label 行首显示的字段名
    public string Label { get; } = label;

    //Required 必填项 空着不放行
    public bool Required { get; } = required;

    //Value 用户填的内容
    public string Value { get; set; } = string.Empty;

    //IsFilled 这一项是否已经填了
    public bool IsFilled => !string.IsNullOrWhiteSpace(Value);

    //Validate 值每变动一次就判一回 决定这一行显示什么颜色 不设就走默认配色
    public Func<string, FieldState>? Validate { get; set; }
}

//FieldState 一行内容当前看起来对不对
public enum FieldState
{
    //Normal 没问题
    Normal,

    //Warn 能用但建议改
    Warn,

    //Error 这样不行
    Error,
}

//Prompt 交互原语
//输入或输出被重定向时自动退化成逐行问答 脚本与 CI 里也能跑
public static class Prompt
{
    //InvalidHighlight 必填没填时红色提示的停留时长
    private static readonly TimeSpan InvalidHighlight = TimeSpan.FromSeconds(3);

    //MaxLineAttempts 逐行模式下缺必填最多再问几轮
    private const int MaxLineAttempts = 3;

    //Confirm 问一个是非题 defaultYes 决定空回车算哪一边 warn 为真时整句用黄色
    public static bool Confirm(string question, bool defaultYes, bool warn = false)
    {
        if (warn)
            Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{question} ({(defaultYes ? "Y/n" : "y/N")}) ");
        Console.ResetColor();

        var answer = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(answer))
            return defaultYes;
        return answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    //Form 收集一组字段 返回必填项是否都填上了
    //终端里是纵列表单 上下键选行 直接敲字符填值 回车提交
    //必填没填时按回车不放行 空的项标红三秒并把指针带到最近的那个必填项
    //重定向时逐行提问 每行一次 ReadLine 填不全由调用方按返回值处置
    public static bool Form(IReadOnlyList<FormField> fields)
    {
        if (fields.Count == 0)
            return true;

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            //降级要让人看得见 不然用户只会以为是终端坏了
            Console.WriteLine("Interactive form is not available here (console is redirected), using line-by-line prompts.");
            return FillByLines(fields);
        }

        return FillInteractive(fields);
    }

    //FillByLines 逐行问答 空行表示不填这一项
    //缺必填就再问一轮 只补还空着的那些 输入断了就作罢 免得在管道里空转
    private static bool FillByLines(IReadOnlyList<FormField> fields)
    {
        for (var attempt = 0; attempt < MaxLineAttempts; attempt++)
        {
            //首轮挨个问 之后只补还空着的必填项
            IReadOnlyList<FormField> pending = attempt == 0
                ? fields
                : fields.Where(field => field.Required && !field.IsFilled).ToList();

            if (pending.Count == 0)
                return true;

            foreach (var field in pending)
            {
                Console.Write($"{field.Label}: ");
                var value = Console.ReadLine();
                if (value is null)
                    return false;
                if (!string.IsNullOrWhiteSpace(value))
                    field.Value = value.Trim();
            }

            if (fields.All(field => !field.Required || field.IsFilled))
                return true;

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("A required field is still empty, fill it in and I will ask again.");
            Console.ResetColor();
        }

        return false;
    }

    //FillInteractive 列表表单
    //先占好整块行数 之后每帧把光标移回块首原地重绘 免得重画时整屏滚动
    private static bool FillInteractive(IReadOnlyList<FormField> fields)
    {
        for (var i = 0; i < fields.Count; i++)
            Console.WriteLine();

        //占满一行后光标会停在块末 由此反推块首 触底滚动时夹住不越界
        var top = Math.Max(0, Console.CursorTop - fields.Count);
        var selected = 0;
        var highlightUntil = DateTime.MinValue;
        var showCursor = OperatingSystem.IsWindows() && Console.CursorVisible;
        SetCursorVisible(false);

        try
        {
            while (true)
            {
                var highlighting = DateTime.UtcNow < highlightUntil;
                DrawFrame(top, fields, selected, highlighting);

                //标红要自己过期 过期前也接按键 一按就当即恢复常态
                if (highlighting)
                {
                    while (!Console.KeyAvailable && DateTime.UtcNow < highlightUntil)
                        Thread.Sleep(30);
                    if (!Console.KeyAvailable)
                        continue;
                }

                var key = Console.ReadKey(true);
                highlightUntil = DateTime.MinValue;

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = (selected - 1 + fields.Count) % fields.Count;
                        break;
                    case ConsoleKey.DownArrow:
                        selected = (selected + 1) % fields.Count;
                        break;
                    case ConsoleKey.Enter:
                        var missing = FirstMissing(fields, selected);
                        if (missing < 0)
                            return true;
                        selected = missing;
                        highlightUntil = DateTime.UtcNow + InvalidHighlight;
                        break;
                    case ConsoleKey.Backspace:
                        if (fields[selected].Value.Length > 0)
                            fields[selected].Value = fields[selected].Value[..^1];
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                            fields[selected].Value += key.KeyChar;
                        break;
                }
            }
        }
        finally
        {
            SetCursorVisible(showCursor);
            //提交或异常都要把光标落到块外 后续输出别糊在表单上
            Console.SetCursorPosition(0, Math.Min(top + fields.Count - 1, Console.BufferHeight - 1));
            Console.WriteLine();
        }
    }

    //FirstMissing 从当前行往下绕一圈找最近的那个没填的必填项 找不着返回 -1
    //从下一行起数 绕满一圈会回到当前行自己 所以它是空的也会被认出来
    private static int FirstMissing(IReadOnlyList<FormField> fields, int selected)
    {
        for (var offset = 1; offset <= fields.Count; offset++)
        {
            var index = (selected + offset) % fields.Count;
            if (fields[index].Required && !fields[index].IsFilled)
                return index;
        }
        return -1;
    }

    //SetCursorVisible 光标可见性只有 Windows 支持 别处连读都会抛
    private static void SetCursorVisible(bool visible)
    {
        if (OperatingSystem.IsWindows())
            Console.CursorVisible = visible;
    }

    //DrawFrame 重绘整块表单 当前行反白 行尾补空格抹掉上一帧的残留
    //highlightInvalid 为真时空着的必填项整行标红 必填项标签后面带一个星号
    private static void DrawFrame(int top, IReadOnlyList<FormField> fields, int selected, bool highlightInvalid)
    {
        var width = Math.Max(Console.WindowWidth - 1, 20);

        for (var i = 0; i < fields.Count; i++)
        {
            Console.SetCursorPosition(0, top + i);
            var field = fields[i];
            var label = field.Required ? $"{field.Label} *" : field.Label;
            var line = $"{(i == selected ? ">" : " ")} {label}: {field.Value}";

            //校验状态优先 该改的地方先让人看见 选中只是光标在哪一行
            var state = field.Validate?.Invoke(field.Value) ?? FieldState.Normal;
            Console.ForegroundColor = state switch
            {
                FieldState.Error => ConsoleColor.Red,
                FieldState.Warn => ConsoleColor.Yellow,
                _ when highlightInvalid && field.Required && !field.IsFilled => ConsoleColor.Red,
                _ => i == selected ? ConsoleColor.Cyan : ConsoleColor.Gray,
            };

            Console.Write(line.Length > width ? line[..width] : line.PadRight(width));
            Console.ResetColor();
        }

        Console.SetCursorPosition(0, top + selected);
    }
}
