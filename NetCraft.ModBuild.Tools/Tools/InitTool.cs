using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//InitTool 从项目模板建一个新的模组 收尾时顺手把图标也生成好
internal static class InitTool
{
    //TemplateShortName dotnet new 用的模板短名
    private const string TemplateShortName = "ncm";

    //Register 把本工具登记进注册表 名字与说明都写在这一行
    public static void Register()
        => ToolRegistry.Register("init", "Create a new NetCraft mod project in a subdirectory", Run);

    //Run 收集内容再落地
    //不带参数走表单问答 带参数就按位置直接取值 顺序与表单一致 一个都不问
    private static int Run(string[] args)
    {
        var directory = Environment.CurrentDirectory;
        var fields = BuildFields(directory);

        if (args.Length > 0)
        {
            if (args.Length > fields.Count)
            {
                Console.WriteLine($"init takes at most {fields.Count} arguments: {string.Join(' ', fields.Select(field => field.Key))}");
                return 1;
            }

            //这条路是给脚本用的 没人看得到确认提示 已经身在项目里就直接失败 免得误伤
            if (HasExistingProject(directory))
            {
                Console.WriteLine($"This directory already contains a csproj or {ModProject.ManifestName} file");
                return 1;
            }

            for (var index = 0; index < args.Length; index++)
                fields[index].Value = args[index];
        }
        else
        {
            if (HasExistingProject(directory) && !Prompt.Confirm(
                    $"This directory already contains a csproj or {ModProject.ManifestName} file. Creating here may affect the existing project. Continue?",
                    defaultYes: false, warn: true))
                return 0;

            if (!Prompt.Form(fields))
            {
                Console.WriteLine("A mod name is required");
                return 1;
            }
        }

        return Submit(directory, fields);
    }

    //BuildFields 表单字段 顺序就是位置参数的顺序 改一处两边同时生效
    private static List<FormField> BuildFields(string directory) => new()
    {
        //只有名字必填 其余都能留空或由模板与缺省约定兜底
        //名字每敲一键就判一回 同名目录已经在了是红 夹了不能进目录名的字符是黄
        new("name", "Mod name", required: true)
        {
            Validate = value => ValidateName(directory, value),
        },
        new("id", "Mod id"),
        new("description", "Description"),
        new("authors", "Authors"),
        new("homepage", "Homepage"),
        new("sources", "Sources"),
        new("license", "License"),
    };

    //Submit 两条入口共用的收尾 名字与目录先验一遍再落地
    private static int Submit(string directory, List<FormField> fields)
    {
        string Value(string key) => fields.First(field => field.Key == key).Value.Trim();

        var name = Value("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("A mod name is required");
            return 1;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Console.WriteLine("The mod name contains characters that cannot be used in a directory name");
            return 1;
        }

        //交互那条路靠表单标红拦着 带参数这条路没人看着 就把关搬到这里
        if (Directory.Exists(Path.Combine(directory, name)))
        {
            Console.WriteLine($"A directory named {name} already exists");
            return 1;
        }

        var id = Value("id");
        var answers = new Answers(
            name,
            string.IsNullOrWhiteSpace(id) ? DeriveId(name) : id,
            Value("description"),
            Value("authors"),
            Value("homepage"),
            Value("sources"),
            Value("license"));

        return Execute(directory, answers);
    }

    //Execute 建项目 补清单字段 删掉模板图标再造一张
    //与收集分开是为了让这条链路能脱离交互单独跑
    private static int Execute(string directory, Answers answers)
    {
        if (!RunTemplate(directory, answers.Name))
            return 1;

        var target = Path.Combine(directory, answers.Name);
        if (!File.Exists(Path.Combine(target, ModProject.ManifestName)))
        {
            Console.WriteLine($"Project created but {ModProject.ManifestName} was not found in {target}");
            return 1;
        }

        var project = ModProject.TryFind(target);
        if (project is null)
        {
            Console.WriteLine($"Project created but {ModProject.ManifestName} could not be read in {target}");
            return 1;
        }

        ApplyAnswers(project, answers);

        //清单刚改过 展示名要重读一次 图标文本取的正是它
        var refreshed = ModProject.TryFind(target) ?? project;

        //模板里那张是占位图 删掉换成按模组名生成的
        if (File.Exists(refreshed.IconPath))
            File.Delete(refreshed.IconPath);
        IconTool.Generate(refreshed);

        Console.WriteLine($"Created {target}");
        return 0;
    }

    //RunTemplate 调 dotnet new 建项目 失败时把 dotnet 自己的输出带出来
    private static bool RunTemplate(string directory, string name)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add(TemplateShortName);
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(name);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            PrintTemplateError();
            return false;
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0)
            return true;

        if (!string.IsNullOrWhiteSpace(output))
            Console.WriteLine(output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(error))
            Console.WriteLine(error.TrimEnd());

        PrintTemplateError();
        return false;
    }

    //PrintTemplateError 失败统一给这一条 绝大多数情况就是模板没装
    private static void PrintTemplateError()
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: the '{TemplateShortName}' project template is not installed. Install it and try again.");
        Console.ResetColor();
    }

    //HasExistingProject 当前目录里已经有工程文件或模组清单
    private static bool HasExistingProject(string directory)
        => Directory.EnumerateFiles(directory, "*.csproj").Any()
            || File.Exists(Path.Combine(directory, ModProject.ManifestName));

    //BadNameChars 不能进目录名的字符 两个引号也算上 从别处粘名字常带着成对引号
    private static readonly char[] BadNameChars = BuildBadNameChars();

    //BuildBadNameChars 平台给的非法字符再加两个引号
    private static char[] BuildBadNameChars()
    {
        var chars = new List<char>(Path.GetInvalidFileNameChars()) { '\'', '"' };
        return chars.ToArray();
    }

    //ValidateName 名字实时校验 同名目录已经在了是红 夹了特殊字符是黄 空着不表态
    private static FieldState ValidateName(string directory, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return FieldState.Normal;
        if (Directory.Exists(Path.Combine(directory, value)))
            return FieldState.Error;
        return value.IndexOfAny(BadNameChars) >= 0 ? FieldState.Warn : FieldState.Normal;
    }

    //DeriveId 名字派生标识 与模板自己的 lowerCase 规则对齐 额外把空格与符号折成连字符
    private static string DeriveId(string name)
    {
        var builder = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    //ApplyAnswers 把填过的字段写进清单 留空的一律不碰 模板给的默认值保持不动
    private static void ApplyAnswers(ModProject project, Answers answers)
        => project.Edit(manifest =>
        {
            var changed = false;
            //name 由模板按 -n 替换 但旧版模板没有这个字段 补一次保证图标文本用的是展示名
            changed |= SetString(manifest, "name", answers.Name);
            changed |= SetString(manifest, "id", answers.Id);
            changed |= SetString(manifest, "description", answers.Description);
            changed |= SetString(manifest, "license", answers.License);
            changed |= SetAuthors(manifest, answers.Authors);
            changed |= SetContact(manifest, "homepage", answers.Homepage);
            changed |= SetContact(manifest, "sources", answers.Sources);
            return changed;
        });

    //SetString 有新值才覆盖
    private static bool SetString(JsonObject manifest, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        manifest[key] = value;
        return true;
    }

    //SetAuthors 逗号分隔拆成数组
    private static bool SetAuthors(JsonObject manifest, string value)
    {
        var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
            return false;

        var array = new JsonArray();
        foreach (var author in names)
            array.Add(author);
        manifest["authors"] = array;
        return true;
    }

    //SetContact 联系信息是嵌套对象 缺了就先建
    private static bool SetContact(JsonObject manifest, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (manifest["contact"] is not JsonObject contact)
        {
            contact = new JsonObject();
            manifest["contact"] = contact;
        }

        contact[key] = value;
        return true;
    }

    //Answers 表单收集到的内容
    private sealed record Answers(
        string Name,
        string Id,
        string Description,
        string Authors,
        string Homepage,
        string Sources,
        string License);
}
