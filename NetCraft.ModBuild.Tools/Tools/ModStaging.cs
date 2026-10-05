using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//ModStaging 把本次要加载的模组备进运行目录
//先搬 libs 里的 modapi 再把当前项目构建一遍 两者都落进 run/mods 服务端一启动就能扫到
internal static class ModStaging
{
    //ModApiFileName libs 里的 modapi 文件名
    //模组 dll 引用它 但内核内嵌子库与模组内嵌依赖都不管这个程序集 只能在 mods 里备一份
    private const string ModApiFileName = "NetCraft.ModApi.dll";

    //ModsDirectoryName 模组目录名 加载器扫的就是这一个
    private const string ModsDirectoryName = "mods";

    //Stage 备好 mods 目录 返回是否可以继续启动
    //任何一步没过都返回 false 没备好模组就不该把服务端拉起来
    public static bool Stage(string projectRoot, string runDirectory)
    {
        var mods = Path.Combine(runDirectory, ModsDirectoryName);
        Directory.CreateDirectory(mods);

        StageModApi(projectRoot, mods);

        //诊断加构建 产物收进项目根的 Build 再整份搬进 mods
        if (BuildTool.Run([]) != 0)
            return false;

        return StageBuildOutput(projectRoot, mods);
    }

    //StageModApi 把 libs 里的 modapi 搬进 mods
    private static void StageModApi(string projectRoot, string mods)
    {
        var source = Path.Combine(projectRoot, "libs", ModApiFileName);
        if (!File.Exists(source))
        {
            Trace.Log($"no libs/{ModApiFileName} under {projectRoot}");
            return;
        }

        File.Copy(source, Path.Combine(mods, ModApiFileName), overwrite: true);
        Console.WriteLine($"Staged {ModApiFileName} to {mods}");
    }

    //StageBuildOutput 把构建产物搬进 mods
    //调试符号不搬 加载器扫的是 dll
    private static bool StageBuildOutput(string projectRoot, string mods)
    {
        var output = BuildTool.OutputPath(projectRoot);
        if (!Directory.Exists(output))
        {
            Console.WriteLine($"error: no build output at {output}");
            return false;
        }

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(output, "*.dll"))
        {
            File.Copy(file, Path.Combine(mods, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        Console.WriteLine($"Staged {copied} mod assembly(ies) to {mods}");
        return true;
    }
}
