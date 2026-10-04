using Photino.NET;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Gui;

//TemplateWindow 模板面板窗口
//页面自己带全部数据 宿主只负责起窗与收尾 清单一栏表在页面上由 js 生成
internal static class TemplateWindow
{
    //WindowWidth/WindowHeight 开窗尺寸
    private const int WindowWidth = 1100;
    private const int WindowHeight = 720;

    //Show 起窗口并阻塞到它被关掉
    public static void Show(TemplateCatalog? catalog)
    {
        //页面整段落到临时文件 让窗口按路径去加载
        //LoadRawString 把整页字符串交给原生那一侧 页面一长就在原生构造里崩（0xC0000005）
        var page = Path.Combine(Path.GetTempPath(), "ncm-panel.html");
        File.WriteAllText(page, UiPage.Build(catalog));

        var window = new PhotinoWindow()
            //静音 Photino 自己那份 API 调用日志 否则每设一个属性就往控制台刷一行
            .SetLogVerbosity(0)
            .SetTitle("NetCraft 模板浏览")
            .SetUseOsDefaultSize(false)
            .SetSize(WindowWidth, WindowHeight)
            .SetResizable(true)
            .Load(page);

        window.WaitForClose();
    }
}
