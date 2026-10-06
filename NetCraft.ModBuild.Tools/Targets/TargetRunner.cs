using System.Text;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;
using NetCraft.ModBuild.Diagnostics;

namespace NetCraft.ModBuild.Tools;

//TargetRunner 借 msbuild 引擎在内存里拼一个项目 执行包带 targets 里的目标
//拼出来的项目不落盘 编译也不归它管 只借引擎的求值与本机跑任务的能力
//编译仍由 Roslyn 直编完成 这里只做编译之后的加工
internal static class TargetRunner
{
    //PostBuildTargets 编译完成之后要跑的目标 项目里没定义就跳过
    //名字与包 targets 里的一致 以后要接别的包把它的目标名补进来
    private static readonly string[] PostBuildTargets = ["CompileAvaloniaXaml"];

    //Run 执行这批后处理目标 返回是否可以继续
    //assembly 是刚直编出来的产物 目标多半要原地改写它
    public static bool Run(string root, NcProject project, string assembly, out string error)
    {
        error = string.Empty;

        //引擎跟着本机 sdk 走 机器上没装 sdk 就只能跳过这一层 别把整个构建拖垮
        if (!MsBuildLibrary.Attach(out var engineError))
        {
            Console.WriteLine($"warning: {engineError}, the targets of the packages are skipped");
            return true;
        }

        try
        {
            Sdks();
            var xml = Compose(root, project, assembly);
            Trace.Log(xml);

            using var reader = XmlReader.Create(new StringReader(xml));
            var element = ProjectRootElement.Create(reader);
            element.FullPath = Path.Combine(root, "ncm.targets.proj");

            var collection = new ProjectCollection();
            var evaluated = new Project(element, globalProperties: null, toolsVersion: null, projectCollection: collection);
            var logger = new ConsoleLogger(LoggerVerbosity.Normal);

            var ran = 0;
            foreach (var target in PostBuildTargets)
            {
                if (!evaluated.Targets.Any(item => item.Key == target))
                    continue;

                Console.WriteLine($"Running target {target}");
                if (!evaluated.Build(target, [logger]))
                {
                    error = $"target {target} failed";
                    return false;
                }

                ran++;
            }

            Trace.Log($"ran {ran} post build target(s)");
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Trace.Log($"target runner failure {e}");
            return false;
        }
    }

    //Compose 拼出内存项目
    //属性与项都按直编那套来 引用直接用算好的清单 不经过包还原那一层
    private static string Compose(string root, NcProject project, string assembly)
    {
        var name = AssemblyName(root, project);
        var builder = new StringBuilder();
        builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");

        builder.AppendLine("  <PropertyGroup>");
        builder.AppendLine($"    <TargetFramework>{TargetFramework.Name}</TargetFramework>");
        builder.AppendLine($"    <Configuration>{project.Build.Configuration}</Configuration>");
        builder.AppendLine($"    <AssemblyName>{Escape(name)}</AssemblyName>");
        builder.AppendLine($"    <RootNamespace>{Escape(name)}</RootNamespace>");
        builder.AppendLine("    <EnableDefaultItems>false</EnableDefaultItems>");
        builder.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        builder.AppendLine("    <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>");
        builder.AppendLine("    <DesignTimeBuild>false</DesignTimeBuild>");
        //引用与包都由直编那边算好了 这里不走 nuget 资产 也就没有 assets 文件这回事
        builder.AppendLine("    <SkipResolvePackageAssets>true</SkipResolvePackageAssets>");
        //这个项目不产引用程序集 默认开着 sdk 会先给一个 refint 路径
        //那文件只有它自己那条编译目标才会生成 直编根本不跑那条
        //Avalonia 认为无需写回时会去给那个文件打时间戳 路径在文件不在直接抛
        builder.AppendLine("    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>");
        //产物交给 sdk 认定的那个中间位置 目标改写的就是它 改完再收回产物目录
        builder.AppendLine($"    <NcmAssembly>{Escape(assembly)}</NcmAssembly>");
        builder.AppendLine($"    <NcmOutput>{Escape(assembly)}</NcmOutput>");
        builder.AppendLine($"    <BaseIntermediateOutputPath>{Escape(Intermediate(root))}{Path.DirectorySeparatorChar}</BaseIntermediateOutputPath>");
        builder.AppendLine($"    <IntermediateOutputPath>{Escape(Intermediate(root))}{Path.DirectorySeparatorChar}</IntermediateOutputPath>");
        builder.AppendLine($"    <MSBuildProjectExtensionsPath>{Escape(Intermediate(root))}{Path.DirectorySeparatorChar}</MSBuildProjectExtensionsPath>");
        builder.AppendLine($"    <OutputPath>{Escape(Path.Combine(root, ProjectLayout.Output))}{Path.DirectorySeparatorChar}</OutputPath>");
        builder.AppendLine("  </PropertyGroup>");

        builder.AppendLine("  <ItemGroup>");
        foreach (var path in CompilationFactory.References(root))
            builder.AppendLine($"    <Reference Include=\"{Escape(path)}\" />");

        foreach (var file in SourceFiles.Enumerate(root, "*.axaml"))
            builder.AppendLine($"    <AvaloniaXaml Include=\"{Escape(file)}\" />");
        builder.AppendLine("  </ItemGroup>");

        //包的 targets 按它自己的相对结构导入 任务程序集路径靠 MSBuildThisFileDirectory 解析
        foreach (var import in PackageTargets.Imports(root))
            builder.AppendLine($"  <Import Project=\"{Escape(import)}\" />");

        //sdk 认定中间产物在哪个位置我们就备到哪 不去猜它的目录结构
        builder.AppendLine("""
              <Target Name="NcmStageAssembly" BeforeTargets="CompileAvaloniaXaml">
                <Copy SourceFiles="$(NcmAssembly)" DestinationFiles="@(IntermediateAssembly)" SkipUnchangedFiles="false" />
              </Target>
              <Target Name="NcmCollectAssembly" AfterTargets="CompileAvaloniaXaml" Condition="Exists('$(NcmOutput)')">
                <Copy SourceFiles="@(IntermediateAssembly)" DestinationFiles="$(NcmOutput)" SkipUnchangedFiles="false" />
              </Target>
            """);

        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    //Intermediate 交给 targets 的中间目录 都塞在 Build 下
    private static string Intermediate(string root)
        => Path.Combine(root, ProjectLayout.Intermediate);

    //AssemblyName 产物名 与直编那边同一套取法
    private static string AssemblyName(string root, NcProject project)
        => string.IsNullOrWhiteSpace(project.Build.AssemblyName)
            ? new DirectoryInfo(root).Name
            : project.Build.AssemblyName;

    //Sdks 让引擎找得到本机 sdk 少了它 Sdk 属性那句没法求值
    //扩展路径也要一起指过去 sdk 根下才有 Current\Microsoft.Common.props
    internal static void Sdks()
    {
        var sdk = MsBuildLibrary.SdkDirectory;
        if (sdk is null)
            return;

        var sdkRoot = sdk + Path.DirectorySeparatorChar;
        Keep("MSBuildSDKsPath", Path.Combine(sdk, "Sdks"));
        Keep("MSBuildExtensionsPath", sdkRoot);
        Keep("MSBuildExtensionsPath32", sdkRoot);
        Keep("MSBuildExtensionsPath64", sdkRoot);
        //引擎按 msbuild 程序集的位置找 SdkResolvers 指到 sdk 里那份它就认 sdk 目录
        var exe = Path.Combine(sdk, "MSBuild.dll");
        if (File.Exists(exe))
            Keep("MSBUILD_EXE_PATH", exe);

        Trace.Log($"sdk {sdk}");
    }

    //Keep 已经指着的就不动 只补空的
    private static void Keep(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    //Escape xml 里的路径要转义
    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
