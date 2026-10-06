using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Compile;

//AvaloniaResources 把 Avalonia 的资源打成加载器认的那一个包
//格式与 Avalonia.Build.Tasks 的 GenerateAvaloniaResourcesTask 一致
//  开头四字节是索引长度 接着是索引 再往后依次是各份文件
//  索引里每条记着路径 相对数据段的偏移与大小
//  运行时 StandardAssetLoader 就照这个索引找 avares:// 地址
//x:Class 与资源路径的对应关系另存成一条 那份是 DataContract 序列化出来的 xml
public static class AvaloniaResources
{
    //ResourceName 包在程序集里的资源名 加载器只认这个名字
    public const string ResourceName = "!AvaloniaResources";

    //XamlInfoPath 记录 x:Class 对应关系的那个特殊条目
    private const string XamlInfoPath = "/!AvaloniaResourceXamlInfo";

    //IndexVersion 二进制索引的版本
    private const int IndexVersion = 2;

    //XamlNamespace x:Class 所在的那个命名空间
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    //XamlInfoNamespace 那份数据契约的命名空间 与 Avalonia 那边的类型对齐
    private const string XamlInfoNamespace = "http://schemas.datacontract.org/2004/07/Avalonia.Markup.Xaml.PortableXaml";

    //XamlInfoRoot 数据契约的根元素名 同样与 Avalonia 那边对齐
    private const string XamlInfoRoot = "AvaloniaResourceXamlInfo";

    //Inputs 参与打包的文件 增量判定要盯它们
    public static IReadOnlyList<string> Inputs(string root, IReadOnlyList<string> patterns)
        => Collect(root, patterns);

    //Pack 按模式收资源打成包 一份都没有时返回 null
    //patterns 相对项目根 支持 * ? 与 ** 三种通配
    public static byte[]? Pack(string root, IReadOnlyList<string> patterns)
    {
        var files = Collect(root, patterns);
        if (files.Count == 0)
            return null;

        var entries = new List<Entry>();
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            entries.Add(new Entry("/" + relative, new FileInfo(path).Length, () => File.OpenRead(path)));

            //类名到资源路径的对应要登记下来 运行时按类名找 axaml 靠的就是它
            if (relative.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                && ClassOf(path) is { Length: > 0 } name)
            {
                classes[name] = "/" + relative;
            }
        }

        var info = XamlInfo(classes);
        entries.Add(new Entry(XamlInfoPath, info.Length, () => new MemoryStream(info, writable: false)));

        return Write(entries);
    }

    //Collect 配置里的模式加上项目里全部 axaml 去重后按路径排序
    //排序只为让同一份输入每次打出同一个包 加载器那边不看顺序
    private static List<string> Collect(string root, IReadOnlyList<string> patterns)
    {
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            foreach (var path in PathPattern.Match(root, pattern))
                files.Add(path);
        }

        //axaml 是 Avalonia 的默认项 不必在配置里再声明一遍
        foreach (var path in SourceFiles.Enumerate(root, "*.axaml"))
            files.Add(path);

        return files.ToList();
    }

    //Write 先摆索引再摆数据 开头的索引长度最后才知道
    private static byte[] Write(List<Entry> entries)
    {
        var index = new MemoryStream();
        using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(IndexVersion);
            writer.Write(entries.Count);

            var offset = 0;
            foreach (var entry in entries)
            {
                writer.Write(entry.Path);
                writer.Write(offset);
                writer.Write((int)entry.Size);
                offset += (int)entry.Size;
            }
        }

        var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((int)index.Length);
            index.Position = 0;
            index.CopyTo(output);

            foreach (var entry in entries)
            {
                using var stream = entry.Open();
                stream.CopyTo(output);
            }
        }

        return output.ToArray();
    }

    //XamlInfo 类名到资源路径的对应表
    //契约的根名与命名空间都显式指定 与 Avalonia 那边那份同名 两边都认同一份 xml
    private static byte[] XamlInfo(Dictionary<string, string> classes)
    {
        var serializer = new DataContractSerializer(typeof(XamlInfoContract), XamlInfoRoot, XamlInfoNamespace);
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, new XamlInfoContract { ClassToResourcePathIndex = classes });
        return stream.ToArray();
    }

    //ClassOf 取 axaml 根元素上的 x:Class 没写或读不动返回 null
    private static string? ClassOf(string path)
    {
        try
        {
            return XDocument.Load(path).Root?.Attribute(XName.Get("Class", XamlNamespace))?.Value;
        }
        catch (Exception e) when (e is XmlException or IOException)
        {
            Trace.Log($"cannot read {path}: {e.Message}");
            return null;
        }
    }

    //Entry 包里的一条 大小与打开方式都摆出来
    private sealed record Entry(string Path, long Size, Func<Stream> Open);

    //XamlInfoContract 序列化用的契约
    //命名空间得与 Avalonia 那边那个类型落在一起 成员的命名空间是从这儿来的 对不上就反序列化不出来
    [DataContract(Namespace = XamlInfoNamespace)]
    private sealed class XamlInfoContract
    {
        [DataMember]
        public Dictionary<string, string> ClassToResourcePathIndex { get; set; } = new();
    }
}
