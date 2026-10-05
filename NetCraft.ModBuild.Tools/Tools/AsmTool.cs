using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using NetCraft.ModBuild.Core;

namespace NetCraft.ModBuild.Tools;

//AsmTool 查看任意程序集 列类型 看类型详情 反编译源码 读依赖
//摘要与详情走元数据 反编译那一档交给 ICSharpCode.Decompiler 直接给 C# 源码而不是 IL
internal static class AsmTool
{
    //Register 把本工具登记进注册表 名字说明与参数都写在这一行
    public static void Register()
        => ToolRegistry.Register("asm", "Inspect a .NET assembly: type list, type details, decompiled source, dependencies", Run,
        [
            new("<assembly>", "Path to a .NET assembly"),
            new("-t, --type <name>", "Show type details"),
            new("-d, --decompile <name>", "Decompile a type to C# source"),
            new("-dep, --dependencies", "List assembly dependencies"),
            new("-r, --reference <dir>", "Extra directory to look for dependencies, repeatable"),
            new("-o, --output <file>", "Write the result to a file instead of the console"),
        ]);

    //Mode 这次要出什么东西
    private enum Mode { Summary, Details, Decompile, Dependencies }

    //TypeRow 类型清单里的一行
    private readonly record struct TypeRow(string Kind, string FullName);

    //Run 解析参数 按模式产出一段文本 不给 -o 就打到控制台
    private static int Run(string[] args)
    {
        var path = default(string);
        var query = default(string);
        var output = default(string);
        var references = new List<string>();
        var mode = Mode.Summary;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "-t":
                case "--type":
                    if (!TakeValue(args, ref index, out query)) return 1;
                    mode = Mode.Details;
                    break;
                case "-d":
                case "--decompile":
                    if (!TakeValue(args, ref index, out query)) return 1;
                    mode = Mode.Decompile;
                    break;
                case "-dep":
                case "--dependencies":
                    mode = Mode.Dependencies;
                    break;
                case "-r":
                case "--reference":
                    if (!TakeValue(args, ref index, out var directory)) return 1;
                    references.Add(directory!);
                    break;
                case "-o":
                case "--output":
                    if (!TakeValue(args, ref index, out output)) return 1;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.WriteLine($"error: unknown asm option {arg}");
                        PrintUsage();
                        return 1;
                    }
                    if (path is not null)
                    {
                        Console.WriteLine("error: only one assembly path is accepted");
                        return 1;
                    }
                    path = arg;
                    break;
            }
        }

        if (path is null)
        {
            PrintUsage();
            return 1;
        }

        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            Console.WriteLine($"error: no such assembly {path}");
            return 1;
        }

        string text;
        try
        {
            text = mode switch
            {
                Mode.Details => Details(path, query!),
                Mode.Decompile => Decompile(path, query!, references),
                Mode.Dependencies => Dependencies(path),
                _ => Summary(path),
            };
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {e.GetType().Name}: {e.Message}");
            Console.ResetColor();
            return 1;
        }

        if (output is null)
        {
            Console.Write(text);
            return 0;
        }

        var target = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text);
        Console.WriteLine($"written to {target}");
        return 0;
    }

    //TakeValue 取走选项后面那个值 后面没有了就报错
    private static bool TakeValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length)
        {
            Console.WriteLine($"error: {args[index]} needs a value");
            value = null;
            return false;
        }
        value = args[++index];
        return true;
    }

    //PrintUsage 帮助文本
    private static void PrintUsage()
    {
        Console.WriteLine("Usage: ncm asm <assembly> [-t <type> | -d <type> | -dep] [-r <directory>] [-o <file>]");
        Console.WriteLine("  <assembly>        path to a .NET assembly");
        Console.WriteLine("  -t, --type        show type details");
        Console.WriteLine("  -d, --decompile   decompile a type to C# source");
        Console.WriteLine("  -dep              list assembly dependencies");
        Console.WriteLine("  -r, --reference   extra directory to look for dependencies, repeatable");
        Console.WriteLine("  -o, --output      write the result to a file instead of the console");
    }

    //Summary 程序集摘要加完整类型清单
    private static string Summary(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        var rows = Types(reader);
        var version = assembly.Version?.ToString() ?? "0.0.0.0";
        var methods = MethodCount(reader);

        var builder = new StringBuilder();
        builder.AppendLine($"[{reader.GetString(assembly.Name)} v{version}]");
        builder.AppendLine(new string('-', 50));
        builder.AppendLine($"{CountOf(rows, "class")} class | {CountOf(rows, "interface")} interface | {CountOf(rows, "enum")} enum | {CountOf(rows, "struct")} struct | {CountOf(rows, "delegate")} delegate | {methods} method");
        builder.AppendLine();
        builder.AppendLine("Types:");
        foreach (var row in rows)
            builder.AppendLine($"  [{row.Kind}] {row.FullName}");
        return builder.ToString();
    }

    //Details 一个类型的基类 字段 属性 方法
    private static string Details(string path, string query)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var handle = Resolve(reader, query) ?? throw new InvalidOperationException(NotFound(reader, query));
        var type = reader.GetTypeDefinition(handle);
        var provider = new NameProvider();

        var builder = new StringBuilder();
        builder.AppendLine($"=== {FullName(reader, type)} ===");
        builder.AppendLine();
        builder.AppendLine($"base: {(BaseName(reader, type.BaseType) is { Length: > 0 } name ? name : "none")}");
        builder.AppendLine();

        var fields = type.GetFields();
        builder.AppendLine($"fields ({fields.Count}):");
        foreach (var fieldHandle in fields)
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            var fieldType = field.DecodeSignature(provider, null);
            builder.AppendLine($"  {fieldType} {reader.GetString(field.Name)}");
        }
        builder.AppendLine();

        var properties = type.GetProperties();
        builder.AppendLine($"properties ({properties.Count}):");
        foreach (var propertyHandle in properties)
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            var propertyType = property.DecodeSignature(provider, null).ReturnType;
            var accessors = property.GetAccessors();
            var get = accessors.Getter.IsNil ? string.Empty : "get; ";
            var set = accessors.Setter.IsNil ? string.Empty : "set; ";
            builder.AppendLine($"  {propertyType} {reader.GetString(property.Name)} {{ {get}{set}}}");
        }
        builder.AppendLine();

        var methods = type.GetMethods();
        builder.AppendLine($"methods ({methods.Count}):");
        foreach (var methodHandle in methods)
            builder.AppendLine($"  {Signature(reader, provider, methodHandle)}");
        return builder.ToString();
    }

    //Decompile 把一个类型反编译成 C# 源码
    //依赖找不齐不能整块失败 解析器放宽之后未解析的类型会按全名留在源码里 代码照样出得来
    private static string Decompile(string path, string query, List<string> references)
    {
        string full;
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
        {
            var reader = pe.GetMetadataReader();
            var handle = Resolve(reader, query) ?? throw new InvalidOperationException(NotFound(reader, query));
            full = FullName(reader, reader.GetTypeDefinition(handle));
        }

        var module = new PEFile(path);
        var resolver = new UniversalAssemblyResolver(path, throwOnError: false, module.DetectTargetFrameworkId());
        foreach (var reference in references)
            resolver.AddSearchDirectory(Path.GetFullPath(reference));

        //缺的先按程序集名去 NuGet 缓存里翻一轮 翻不到才算真缺
        ResolveFromNuGet(module, resolver);

        var missing = Unresolved(module, resolver);
        if (missing.Count > 0)
        {
            //缺依赖不拦 但要让用户知道源码里有类型没解析上 补 -r 能修
            Console.Error.WriteLine($"warning: unresolved assemblies: {string.Join(", ", missing)}");
            Console.Error.WriteLine("warning: types from them stay as full names in the source, use -r <directory> to add a search path");
        }

        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new CSharpDecompiler(module, resolver, settings);
        return decompiler.DecompileTypeAsString(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(full));
    }

    //Unresolved 还没解析上的程序集名
    private static List<string> Unresolved(PEFile module, UniversalAssemblyResolver resolver)
    {
        var names = new List<string>();
        foreach (var reference in module.AssemblyReferences)
            if (resolver.Resolve(reference) is null) names.Add(reference.Name);
        return names;
    }

    //ResolveFromNuGet 缺的按程序集名去 NuGet 全局包缓存里翻同版本的库
    //程序集名和包名不一定一样 Avalonia.Base 在包 avalonia 里 所以整名不中就退回首段
    private static void ResolveFromNuGet(PEFile module, UniversalAssemblyResolver resolver)
    {
        var root = PackageRoot();
        if (root is null) return;

        foreach (var reference in module.AssemblyReferences)
        {
            if (resolver.Resolve(reference) is not null) continue;
            foreach (var directory in NuGetDirectories(root, reference.Name, reference.Version))
                resolver.AddSearchDirectory(directory);
        }
    }

    //PackageRoot NuGet 全局包缓存的位置 环境变量优先
    private static string? PackageRoot()
    {
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return Directory.Exists(root) ? root : null;
    }

    //NuGetDirectories 一个程序集在缓存里可能落到的框架目录 版本对上才给
    private static IEnumerable<string> NuGetDirectories(string root, string name, Version? version)
    {
        if (version is null) yield break;
        var candidates = new[] { name.ToLowerInvariant(), name.Split('.')[0].ToLowerInvariant() };
        foreach (var packageName in candidates.Distinct(StringComparer.Ordinal))
        {
            var package = Path.Combine(root, packageName);
            if (!Directory.Exists(package)) continue;
            foreach (var directory in Directory.GetDirectories(package))
            {
                if (!VersionMatches(Path.GetFileName(directory), version)) continue;
                foreach (var kind in new[] { "lib", "ref" })
                {
                    var path = Path.Combine(directory, kind);
                    if (!Directory.Exists(path)) continue;
                    var framework = PickFramework(path);
                    if (framework is null) continue;
                    yield return framework;
                    //lib 里有实现就不用再退到只有签名的 ref
                    break;
                }
            }
        }
    }

    //VersionMatches 目录名与引用版本对上 12.1.3 与 12.1.3.0 算同一个
    private static bool VersionMatches(string directory, Version version)
        => Version.TryParse(directory, out var parsed) && Normalize(parsed) == Normalize(version);

    //Normalize 版本取前三段 最后那位零头不看
    private static string Normalize(Version version)
        => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    //PickFramework 版本目录下的目标框架子目录 优先当前运行时那个
    private static string? PickFramework(string path)
    {
        var directories = Directory.GetDirectories(path);
        if (directories.Length == 0) return null;
        var current = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        return directories.FirstOrDefault(directory => Path.GetFileName(directory).Equals(current, StringComparison.OrdinalIgnoreCase))
            ?? directories.OrderBy(directory => Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase).First();
    }

    //Dependencies 程序集引用清单
    private static string Dependencies(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var references = new List<string>();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            references.Add($"{reader.GetString(reference.Name)} {reference.Version}");
        }

        var builder = new StringBuilder();
        builder.AppendLine($"dependencies ({references.Count}):");
        foreach (var reference in references.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"  {reference}");
        return builder.ToString();
    }

    //NotFound 找不到类型时给的消息 顺带列几个名字像的
    private static string NotFound(MetadataReader reader, string query)
    {
        var near = Types(reader)
            .Where(row => row.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .Select(row => row.FullName)
            .ToList();
        return near.Count > 0
            ? $"no type named {query}, did you mean: {string.Join(", ", near)}"
            : $"no type named {query}";
    }

    //Resolve 把用户给的短名或全名对上类型定义 对不上返回 null
    private static TypeDefinitionHandle? Resolve(MetadataReader reader, string query)
    {
        var rows = new List<(TypeDefinitionHandle Handle, string Full, string Short)>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (IsCompilerGenerated(type, reader)) continue;
            var full = FullName(reader, type);
            rows.Add((handle, full, ShortName(full)));
        }

        var exact = rows.FirstOrDefault(row => row.Full.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact.Full is not null) return exact.Handle;

        var matches = rows.Where(row => row.Short.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Handle : null;
    }

    //Types 全部类型定义 顺序跟元数据里一致
    private static List<TypeRow> Types(MetadataReader reader)
    {
        var rows = new List<TypeRow>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (IsCompilerGenerated(type, reader)) continue;
            rows.Add(new TypeRow(Kind(reader, type), FullName(reader, type)));
        }
        return rows;
    }

    //MethodCount 真实类型上的方法总数 编译器生成的那些不算
    private static int MethodCount(MetadataReader reader)
    {
        var total = 0;
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (IsCompilerGenerated(type, reader)) continue;
            total += type.GetMethods().Count;
        }
        return total;
    }

    //IsCompilerGenerated 名字以尖括号开头的都是编译器生成的 列清单时不该出现
    private static bool IsCompilerGenerated(TypeDefinition type, MetadataReader reader)
    {
        var name = reader.GetString(type.Name);
        return name.Length == 0 || name[0] == '<';
    }

    //CountOf 摘要里某一类有多少个
    private static int CountOf(List<TypeRow> rows, string kind)
        => rows.Count(row => row.Kind == kind);

    //Kind 类型归到哪一类 看接口标志与基类
    private static string Kind(MetadataReader reader, TypeDefinition type)
    {
        if ((type.Attributes & TypeAttributes.Interface) != 0) return "interface";
        return BaseName(reader, type.BaseType) switch
        {
            "System.Enum" => "enum",
            "System.ValueType" => "struct",
            "System.MulticastDelegate" => "delegate",
            _ => "class",
        };
    }

    //FullName 带命名空间的全名 嵌套类型用 / 连接
    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return FullName(reader, reader.GetTypeDefinition(declaring)) + "/" + name;
        var space = reader.GetString(type.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    //ShortName 全名的最后一段 嵌套类型只看最内层
    private static string ShortName(string full)
    {
        var index = full.LastIndexOfAny(new[] { '.', '/' });
        return index < 0 ? full : full[(index + 1)..];
    }

    //BaseName 基类的名字 定义与引用两种句柄都认 认不出给空
    private static string BaseName(MetadataReader reader, EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.TypeDefinition => FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => TypeReferenceName(reader, (TypeReferenceHandle)handle),
            _ => string.Empty,
        };

    //TypeReferenceName 引用类型的名字
    private static string TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var space = reader.GetString(reference.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    //Signature 一个方法的可读签名 参数名从参数表里取
    private static string Signature(MetadataReader reader, NameProvider provider, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        var signature = method.DecodeSignature(provider, null);
        var names = method.GetParameters()
            .Select(item => reader.GetString(reader.GetParameter(item).Name))
            .ToList();

        var parameters = signature.ParameterTypes
            .Select((type, index) => index < names.Count && names[index].Length > 0 ? $"{type} {names[index]}" : type);
        return $"{signature.ReturnType} {reader.GetString(method.Name)}({string.Join(", ", parameters)})";
    }

    //NameProvider 把签名里的类型原样拼回 C# 写法
    private sealed class NameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape)
            => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "methodptr";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => Primitive(typeCode);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => FullName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => TypeReferenceName(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    //Primitive 基元类型码换成 C# 关键字
    private static string Primitive(PrimitiveTypeCode code) => code switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.Void => "void",
        _ => code.ToString(),
    };
}
