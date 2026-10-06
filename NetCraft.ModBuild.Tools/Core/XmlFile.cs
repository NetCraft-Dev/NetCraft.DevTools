using System.Xml;
using System.Xml.Linq;

namespace NetCraft.ModBuild.Core;

//XmlFile 项目配置文件的写回
//缩进统一成两格 与手写的 .ncproj 保持一致 末尾补一个换行
internal static class XmlFile
{
    //Save 把文档写回文件
    public static void Save(string path, XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = true,
            NewLineChars = Environment.NewLine,
        };

        using (var writer = XmlWriter.Create(path, settings))
            document.Save(writer);

        File.AppendAllText(path, Environment.NewLine);
    }
}
