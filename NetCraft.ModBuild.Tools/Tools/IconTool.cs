using System.Globalization;
using System.Text;
using NetCraft.ModBuild.Core;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NetCraft.ModBuild.Tools;

//IconTool 为当前模组项目生成图标
//白色背景配模组名的黑色文本 文本按墨迹放大到填满留白 放不下就折行
internal static class IconTool
{
    //Size 输出图标的边长
    private const int Size = 128;

    //Padding 文本与图标边缘的最小留白
    private const int Padding = 10;

    //LineSample 量一整行占位高度用的样本 带升降部的两个字母最能代表一行的实际占位
    private const string LineSample = "Ag";

    //FontResource 内嵌字体资源名
    private const string FontResource = "iconttf.ttf";

    //RefinePasses 字号与折行互相影响 多算几轮让两者收敛
    private const int RefinePasses = 3;

    //Family 内嵌字体只解析一次
    private static readonly Lazy<FontFamily> Family = new(LoadFamily);

    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("icon", "Generate a white background mod icon with the mod name", Run,
        [
            new("-f, --force", "Overwrite an existing icon without asking"),
        ]);

    //Run 执行 当前不在模组项目下时静默退出
    private static int Run(string[] args)
    {
        var force = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-f":
                case "--force":
                    force = true;
                    break;
                default:
                    Console.WriteLine($"error: unknown icon option {arg}");
                    Console.WriteLine("Usage: ncm icon [--force]");
                    return 1;
            }
        }

        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
            return 0;

        //带 force 就不问那一句 直接盖掉
        if (!force && File.Exists(project.IconPath) && !ConfirmOverwrite(project.IconPath))
            return 0;

        Generate(project);
        return 0;
    }

    //Generate 为项目画出图标并在清单缺 icon 时补一条
    //init 建完项目也要走这一步 所以生成逻辑与命令行入口分开
    internal static void Generate(ModProject project)
    {
        WriteIcon(project.IconPath, project.DisplayName);
        Console.WriteLine($"Icon written to {project.IconPath}");

        //清单没写 icon 时补一条 生成位置本来就是按缺省约定来的
        if (project.EnsureIcon(ModProject.DefaultIconName))
            Console.WriteLine($"Added icon entry to {Path.GetFileName(project.ManifestPath)}");
    }

    //ConfirmOverwrite 图标已存在时问一句
    //回车与 y 都算跳过 只有明确说不才覆盖
    private static bool ConfirmOverwrite(string path)
    {
        Console.Write($"Icon already exists ({Path.GetFileName(path)}). Skip? (Y/n) ");
        var answer = Console.ReadLine();
        return answer is not null && answer.StartsWith("n", StringComparison.OrdinalIgnoreCase);
    }

    //WriteIcon 画图并存盘
    //先从整幅边长起算 首轮几乎一定超出 缩放会把字号压回留白之内
    private static void WriteIcon(string path, string text)
    {
        var family = Family.Value;
        var available = Size - Padding * 2f;

        var fontSize = (float)Size;
        var lines = Wrap(family, fontSize, text, available);

        //字号一变折行结果跟着变 反复算几轮让两者稳下来
        for (var pass = 0; pass < RefinePasses; pass++)
        {
            var (width, height) = MeasureBlock(family, fontSize, lines);
            if (width <= 0f || height <= 0f)
                break;

            fontSize *= Math.Min(available / width, available / height);
            lines = Wrap(family, fontSize, text, available);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var image = new Image<Rgba32>(Size, Size, new Rgba32(255, 255, 255));
        DrawLines(image, family, fontSize, lines);
        image.SaveAsPng(path);
    }

    //DrawLines 逐行绘制 每行按自身墨迹水平居中 整块垂直居中
    //取墨迹而不是排版框是为了让字看起来真的居中 排版框上下自带的留白不均匀
    private static void DrawLines(Image<Rgba32> image, FontFamily family, float fontSize, List<string> lines)
    {
        var font = family.CreateFont(fontSize, FontStyle.Regular);
        var lineHeight = MeasureLineHeight(font);
        var top = (Size - lineHeight * lines.Count) / 2f;

        image.Mutate(context =>
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var ink = TextMeasurer.MeasureBounds(lines[i], new TextOptions(font));
                var origin = new PointF(
                    (Size - ink.Width) / 2f - ink.Left,
                    top + i * lineHeight - ink.Top);
                context.DrawText(new RichTextOptions(font) { Origin = origin }, lines[i], Color.Black);
            }
        });
    }

    //MeasureBlock 量多行文本的墨迹范围 缩放字号以它为基准
    //行距只发生在行与行之间 单行时不补
    private static (float Width, float Height) MeasureBlock(FontFamily family, float fontSize, List<string> lines)
    {
        var font = family.CreateFont(fontSize, FontStyle.Regular);
        var lineHeight = MeasureLineHeight(font);
        var width = 0f;
        var height = 0f;

        foreach (var line in lines)
        {
            var ink = TextMeasurer.MeasureBounds(line, new TextOptions(font));
            width = Math.Max(width, ink.Width);
            height = Math.Max(height, ink.Height);
        }

        return (width, height + lineHeight * (lines.Count - 1));
    }

    //Wrap 按宽度折行 优先在空格处断
    //单个词自己就超宽时让它独占一行 后面的缩放会把它压回留白之内
    private static List<string> Wrap(FontFamily family, float fontSize, string text, float maxWidth)
    {
        var font = family.CreateFont(fontSize, FontStyle.Regular);
        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length > 0 && MeasureWidth(font, candidate) > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');
            current.Append(word);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines.Count > 0 ? lines : new List<string> { text };
    }

    //MeasureLineHeight 量一整行的占位高度 行距由字体自身决定
    private static float MeasureLineHeight(Font font)
        => TextMeasurer.MeasureSize(LineSample, new TextOptions(font)).Height;

    //MeasureWidth 一段文本的排版宽度
    private static float MeasureWidth(Font font, string value)
        => TextMeasurer.MeasureSize(value, new TextOptions(font)).Width;

    //LoadFamily 从内嵌资源解析字体
    //解析要求流可寻址 资源流本身不满足 先整份读进内存
    private static FontFamily LoadFamily()
    {
        using var resource = typeof(IconTool).Assembly.GetManifestResourceStream(FontResource)
            ?? throw new InvalidOperationException($"Embedded font {FontResource} not found");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        buffer.Position = 0;
        return new FontCollection().Add(buffer, CultureInfo.InvariantCulture);
    }
}
