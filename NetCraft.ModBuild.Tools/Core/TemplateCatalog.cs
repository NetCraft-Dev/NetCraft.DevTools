using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NetCraft.ModBuild.Core;

//TemplateEntry 清单里的一条 api 条目
public sealed class TemplateEntry
{
    //Id api 的完整标识 例如 NetCraft.ModApi.Wrapper.NcPlayer
    public string Id { get; set; } = string.Empty;

    //Title 一句话名字
    public string Title { get; set; } = string.Empty;

    //Summary 更细的说明
    public string Summary { get; set; } = string.Empty;

    //Template 示例文件相对 base 的路径
    public string Template { get; set; } = string.Empty;

    //Members 该 api 对外暴露的成员名 用来判断项目里的用法对不对
    //留空表示不校验成员 只要类型名对上就算用对
    public List<string> Members { get; set; } = new();
}

//GradeRule 扫描项目源码时的识别规则
//符号写成 类型.成员 类型命中这里的规则才当模组 api 看
public sealed class GradeRule
{
    //Prefixes 类型名前缀 例如 Nc
    public List<string> Prefixes { get; set; } = new() { "Nc" };

    //Types 不带前缀也要认的类型名 例如三个事件门面
    public List<string> Types { get; set; } = new() { "ServerEvents", "ClientEvents", "NetworkEvents" };
}

//TemplateCatalog NetCraftTemplate.yaml 的模型
//字段与 yaml 一一对应 多出来的键直接忽略 方便以后再往清单上加东西
public sealed class TemplateCatalog
{
    //Base 示例文件的拉取基准
    public string Base { get; set; } = string.Empty;

    //Document 图形面板渲染的文档 相对 base 的路径
    public string Document { get; set; } = string.Empty;

    //Grade 识别项目里 api 用法的形状规则
    public GradeRule Grade { get; set; } = new();

    //Apis 全部可用条目
    public List<TemplateEntry> Apis { get; set; } = new();

    //Read 读本地清单 读不出或解析不了返回 null
    public static TemplateCatalog? Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException e)
        {
            Console.WriteLine($"Failed to read {path}: {e.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine($"Template catalog {path} is empty");
            return null;
        }

        try
        {
            return new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<TemplateCatalog>(text);
        }
        catch (YamlException e)
        {
            Console.WriteLine($"Template catalog {path} is not valid yaml: {e.Message}");
            return null;
        }
    }

    //ResolveUrl 把条目里的相对路径拼成完整地址 两头的斜杠各归各的
    public string ResolveUrl(string relative)
        => $"{Base.TrimEnd('/')}/{relative.TrimStart('/')}";

    //Matches 条目 id 是否命中筛选模式
    //? 与 * 都当任意字符序列 其余字符按字面比 大小写不敏感 空模式一律命中
    public static bool Matches(string id, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        //先转义再放开通配 免得用户输入里的正则元字符意外生效
        var body = Regex.Escape(pattern).Replace(@"\?", ".*").Replace(@"\*", ".*");
        return Regex.IsMatch(id, $"^.*{body}.*$", RegexOptions.IgnoreCase);
    }
}
