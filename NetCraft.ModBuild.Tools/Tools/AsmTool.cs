using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using NetCraft.ModBuild.Core;
//Cecil and the metadata reader both name several types, the aliases below keep them apart
using CecilEmbedded = Mono.Cecil.EmbeddedResource;
using CecilModule = Mono.Cecil.ModuleDefinition;

namespace NetCraft.ModBuild.Tools;

//AsmTool inspects any assembly: list types, show type details, decompile source, read dependencies
//Summaries and details come from metadata while decompilation is delegated to ICSharpCode.Decompiler so it yields C# rather than IL
internal static class AsmTool
{
    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("asm", "Inspect a .NET assembly: type list, type details, decompiled source, dependencies", Run,
        [
            new("<assembly>", "Path to a .NET assembly"),
            new("-t, --type <name>", "Show type details"),
            new("-d, --decompile <name>", "Decompile a type to C# source"),
            new("-dep, --dependencies", "List assembly dependencies"),
            new("-res, --resources [name]", "List resources, or extract one by name with -o"),
            new("-ar, --add-resource <file>", "Embed a file into the target assembly as a resource, repeatable"),
            new("-r, --reference <dir>", "Extra directory to look for dependencies, repeatable"),
            new("-o, --output <file>", "Write the result to a file instead of the console"),
        ]);

    //Mode selects what this run produces
    private enum Mode { Summary, Details, Decompile, Dependencies, Resources }

    //TypeRow one entry in the type list
    private readonly record struct TypeRow(string Kind, string FullName);

    //Run parses the arguments and produces a text block per mode, printing it unless -o is given
    private static int Run(string[] args)
    {
        var path = default(string);
        var query = default(string);
        var output = default(string);
        var references = new List<string>();
        var additions = new List<string>();
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
                case "-res":
                case "--resources":
                    mode = Mode.Resources;
                    //the name is optional, without one this lists the resources and with one it extracts that resource
                    if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
                        query = args[++index];
                    break;
                case "-ar":
                case "--add-resource":
                    if (!TakeValue(args, ref index, out var addition)) return 1;
                    additions.Add(addition!);
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

        //Adding a resource rewrites the target itself, so it stands alone instead of sharing the read modes
        if (additions.Count > 0)
        {
            if (mode != Mode.Summary || output is not null)
            {
                Console.WriteLine("error: --add-resource writes the target, it does not combine with a read mode or -o");
                return 1;
            }

            try
            {
                return AddResources(path, additions);
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"error: {e.GetType().Name}: {e.Message}");
                Console.ResetColor();
                return 1;
            }
        }

        //Extracting writes bytes rather than text, so it stands before the text modes
        if (mode == Mode.Resources && query is not null)
            return Extract(path, query, output);

        string text;
        try
        {
            text = mode switch
            {
                Mode.Details => Details(path, query!),
                Mode.Decompile => Decompile(path, query!, references),
                Mode.Dependencies => Dependencies(path),
                Mode.Resources => Resources(path),
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

    //TakeValue consumes the value after an option, failing when there is none left
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

    //PrintUsage prints the help text
    private static void PrintUsage()
    {
        Console.WriteLine("Usage: ncm asm <assembly> [-t <type> | -d <type> | -dep | -res [name]] [-r <directory>] [-o <file>]");
        Console.WriteLine("  <assembly>        path to a .NET assembly");
        Console.WriteLine("  -t, --type        show type details");
        Console.WriteLine("  -d, --decompile   decompile a type to C# source");
        Console.WriteLine("  -dep              list assembly dependencies");
        Console.WriteLine("  -res [name]       list embedded resources, or extract one by name");
        Console.WriteLine("  -ar, --add-resource  embed a file into the target assembly as a resource, repeatable");
        Console.WriteLine("  -r, --reference   extra directory to look for dependencies, repeatable");
        Console.WriteLine("  -o, --output      write to a file: the text output, or the extracted resource");
    }

    //Summary an assembly summary plus the full type list
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

    //Details the base type, fields, properties and methods of one type
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

    //Decompile turns one type into C# source
    //Missing dependencies must not fail the whole run, a relaxed resolver keeps unresolved types as full names so the source still comes out
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

        //Look for the missing ones in the NuGet cache by assembly name, they only count as missing when that fails
        ResolveFromNuGet(module, resolver);

        var missing = Unresolved(module, resolver);
        if (missing.Count > 0)
        {
            //Missing dependencies do not stop the run but the user should know some types stayed unresolved, -r fixes it
            Console.Error.WriteLine($"warning: unresolved assemblies: {string.Join(", ", missing)}");
            Console.Error.WriteLine("warning: types from them stay as full names in the source, use -r <directory> to add a search path");
        }

        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new CSharpDecompiler(module, resolver, settings);
        return decompiler.DecompileTypeAsString(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(full));
    }

    //Unresolved names of the assemblies that are still unresolved
    private static List<string> Unresolved(PEFile module, UniversalAssemblyResolver resolver)
    {
        var names = new List<string>();
        foreach (var reference in module.AssemblyReferences)
            if (resolver.Resolve(reference) is null) names.Add(reference.Name);
        return names;
    }

    //ResolveFromNuGet finds missing assemblies in the NuGet global package cache by assembly name
    //An assembly name is not always the package name (Avalonia.Base ships in avalonia), so fall back to the first segment when the full name misses
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

    //PackageRoot the NuGet global package cache location, the environment variable wins
    private static string? PackageRoot()
    {
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return Directory.Exists(root) ? root : null;
    }

    //NuGetDirectories the framework directories an assembly may live in inside the cache, only a matching version qualifies
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
                    //A lib folder carries the implementation, no need to fall back to the signature-only ref
                    break;
                }
            }
        }
    }

    //VersionMatches whether a directory name matches the referenced version, 12.1.3 and 12.1.3.0 count as the same
    private static bool VersionMatches(string directory, Version version)
        => Version.TryParse(directory, out var parsed) && Normalize(parsed) == Normalize(version);

    //Normalize keeps the first three version parts and ignores the trailing revision
    private static string Normalize(Version version)
        => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    //PickFramework the target framework subdirectory under a version, preferring the current runtime
    private static string? PickFramework(string path)
    {
        var directories = Directory.GetDirectories(path);
        if (directories.Length == 0) return null;
        var current = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        return directories.FirstOrDefault(directory => Path.GetFileName(directory).Equals(current, StringComparison.OrdinalIgnoreCase))
            ?? directories.OrderBy(directory => Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase).First();
    }

    //Dependencies the assembly reference list
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

    //Resources the embedded and linked resources of the assembly
    //The loader takes ncmod.json as the mod declaration and every .dll resource as an embedded dependency, so both are named
    private static string Resources(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var rows = new List<(string Name, long Size, string Kind)>();
        foreach (var handle in reader.ManifestResources)
        {
            var resource = reader.GetManifestResource(handle);
            var embedded = resource.Implementation.IsNil;
            var name = reader.GetString(resource.Name);
            rows.Add((name, embedded ? EmbeddedSize(pe, resource) : -1, KindOf(name, embedded)));
        }

        var builder = new StringBuilder();
        builder.AppendLine($"resources ({rows.Count}):");
        foreach (var row in rows.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"  {row.Name,-30} {Size(row.Size),10}  {row.Kind}");
        return builder.ToString();
    }

    //Block the section data an embedded resource sits in, starting at its own length prefix
    //ManifestResource.Offset is relative to the CLI resources directory, not to the file
    private static ImmutableArray<byte> Block(PEReader pe, ManifestResource resource)
    {
        var directory = pe.PEHeaders.CorHeader?.ResourcesDirectory.RelativeVirtualAddress ?? 0;
        return directory == 0 ? default : pe.GetSectionData(directory + (int)resource.Offset).GetContent();
    }

    //EmbeddedSize the byte length of an embedded resource, or -1 when it cannot be read
    private static long EmbeddedSize(PEReader pe, ManifestResource resource)
    {
        var block = Block(pe, resource);
        return block.IsDefault || block.Length < 4 ? -1 : BitConverter.ToInt32(block.AsSpan());
    }

    //Bytes the content of an embedded resource, its length prefix excluded
    private static byte[] Bytes(PEReader pe, ManifestResource resource)
    {
        var block = Block(pe, resource);
        if (block.IsDefault || block.Length < 4)
            return [];

        //A declared length past the section would mean a broken file, the section is the limit that counts
        var length = Math.Clamp(BitConverter.ToInt32(block.AsSpan()), 0, block.Length - 4);
        return block.AsSpan(4, length).ToArray();
    }

    //Extract writes one embedded resource out to a file
    //Without -o it lands in the current directory under the resource name, the way template example pulls a file in
    private static int Extract(string path, string query, string? output)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var handle = ResolveResource(reader, query);
        if (handle is null)
        {
            Console.WriteLine(NotFoundResource(reader, query));
            return 1;
        }

        var resource = reader.GetManifestResource(handle.Value);
        if (!resource.Implementation.IsNil)
        {
            Console.WriteLine($"error: {reader.GetString(resource.Name)} is a linked resource, only embedded ones can be taken out");
            return 1;
        }

        var target = Path.GetFullPath(output ?? reader.GetString(resource.Name));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, Bytes(pe, resource));
        Console.WriteLine($"written to {target}");
        return 0;
    }

    //ResolveResource matches a resource by full name, or by a unique trailing part the way types are matched
    private static ManifestResourceHandle? ResolveResource(MetadataReader reader, string query)
    {
        var rows = new List<(ManifestResourceHandle Handle, string Name)>();
        foreach (var handle in reader.ManifestResources)
            rows.Add((handle, reader.GetString(reader.GetManifestResource(handle).Name)));

        var exact = rows.FirstOrDefault(row => row.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact.Name is not null)
            return exact.Handle;

        var suffix = rows.Where(row => row.Name.EndsWith("." + query, StringComparison.OrdinalIgnoreCase)).ToList();
        return suffix.Count == 1 ? suffix[0].Handle : null;
    }

    //NotFoundResource the message when nothing matched, naming the ones that look similar
    private static string NotFoundResource(MetadataReader reader, string query)
    {
        var near = reader.ManifestResources
            .Select(handle => reader.GetString(reader.GetManifestResource(handle).Name))
            .Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();

        return near.Count > 0
            ? $"error: no resource named {query}, did you mean: {string.Join(", ", near)}"
            : $"error: no resource named {query}";
    }

    //KindOf how the loader reads a resource: the mod declaration, an embedded dependency, or anything else
    private static string KindOf(string name, bool embedded)
    {
        if (!embedded)
            return "linked";
        if (name == ModProject.ManifestName)
            return "mod manifest";
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "mod dependency" : "resource";
    }

    //Size renders a byte count, or a dash when it could not be read
    private static string Size(long bytes) => bytes < 0 ? "-" : $"{bytes} B";

    //AddResources embeds each file into the target assembly as a public manifest resource
    //The resource name is the file name, which is what the loader matches an embedded dependency by
    //A name already taken is replaced, so pointing at an existing resource is how a replacement is done
    private static int AddResources(string target, List<string> paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"error: no such resource file {Path.GetFullPath(path)}");
                return 1;
            }
        }

        //Written beside the target and moved over it, so a failure never leaves a half written assembly behind
        var temporary = target + ".rewriting";
        var done = new List<string>();

        try
        {
            using (var module = CecilModule.ReadModule(target, new Mono.Cecil.ReaderParameters { InMemory = true }))
            {
                foreach (var path in paths)
                {
                    var full = Path.GetFullPath(path);
                    var name = Path.GetFileName(full);
                    var existing = module.Resources.FirstOrDefault(resource => resource.Name == name);
                    if (existing is not null)
                        module.Resources.Remove(existing);

                    module.Resources.Add(new CecilEmbedded(name, Mono.Cecil.ManifestResourceAttributes.Public, File.ReadAllBytes(full)));
                    done.Add($"{(existing is null ? "added" : "replaced")} {name}, {new FileInfo(full).Length} B");
                }

                //A rewrite cannot keep a strong name, the signature covers the bytes that just changed
                if (module.Assembly.Name.HasPublicKey)
                    Console.Error.WriteLine("warning: the target is strong named, the rewrite drops its signature");

                module.Write(temporary);
            }
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }

        File.Move(temporary, target, overwrite: true);
        foreach (var line in done)
            Console.WriteLine(line);
        return 0;
    }

    //NotFound the message when a type is missing, including a few similarly named ones
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

    //Resolve matches a short or full name the user gave to a type definition, returning null when nothing matches
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

    //Types every type definition in metadata order
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

    //MethodCount the total method count across real types, compiler generated ones excluded
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

    //IsCompilerGenerated compiler generated types start with an angle bracket and should not be listed
    private static bool IsCompilerGenerated(TypeDefinition type, MetadataReader reader)
    {
        var name = reader.GetString(type.Name);
        return name.Length == 0 || name[0] == '<';
    }

    //CountOf how many rows of a kind the summary reports
    private static int CountOf(List<TypeRow> rows, string kind)
        => rows.Count(row => row.Kind == kind);

    //Kind classifies a type by its interface flag and base type
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

    //FullName the full name including the namespace, nested types joined with /
    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return FullName(reader, reader.GetTypeDefinition(declaring)) + "/" + name;
        var space = reader.GetString(type.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    //ShortName the last segment of a full name, nested types only show the innermost part
    private static string ShortName(string full)
    {
        var index = full.LastIndexOfAny(new[] { '.', '/' });
        return index < 0 ? full : full[(index + 1)..];
    }

    //BaseName the base type name resolved from either a definition or a reference handle, empty when unknown
    private static string BaseName(MetadataReader reader, EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.TypeDefinition => FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => TypeReferenceName(reader, (TypeReferenceHandle)handle),
            _ => string.Empty,
        };

    //TypeReferenceName the name of a referenced type
    private static string TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var space = reader.GetString(reference.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    //Signature a readable method signature with parameter names taken from the parameter table
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

    //NameProvider renders signature types back into C# spelling
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

    //Primitive maps a primitive type code to its C# keyword
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
