using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NetCraft.ModBuild.Compile;
using NetCraft.ModBuild.Core;
//Roslyn's Diagnostic collides with the local one in this namespace, so alias it
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace NetCraft.ModBuild.Diagnostics;

//CSharpAnalyzer compiles the project sources through Roslyn
//syntax and semantic errors surface here, and assembly is left to CompilationFactory so the check shares the real build's setup
public static class CSharpAnalyzer
{
    //XamlName matches control names declared in UI files, as in x:Name="ModList"
    private static readonly Regex XamlName = new(
        @"(?:x:)?Name\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    //MaxNamespaces caps how many candidate namespaces a missing using advice lists
    private const int MaxNamespaces = 4;

    //MaxSignatures caps how many real signatures an argument list advice lists
    private const int MaxSignatures = 4;

    //MaxMembers caps how many real members a missing member advice names
    //a wall of names is its own kind of noise, and the reader only needs a lead, not the whole type
    private const int MaxMembers = 5;

    //Hidden memoizes the accessibility answers for members kept out of the compiler's view, keyed by assembly, type and name
    private static readonly Dictionary<string, string?> Hidden = new(StringComparer.Ordinal);

    //Analyze compiles the project sources and collects errors into bag, skipping identifiers the catalog pass already reported
    public static void Analyze(string root, DiagnosticBag bag, IReadOnlySet<string> covered)
        => Analyze(root, CompileOptions.Default, bag, covered);

    //Analyze overload taking caller-supplied compile options so the check matches the real build
    public static void Analyze(string root, CompileOptions options, DiagnosticBag bag, IReadOnlySet<string> covered)
    {
        var project = CompilationFactory.Create(root, options, out var failure);
        if (project is null)
        {
            Trace.Log($"skipping the Roslyn check: {failure}");
            return;
        }

        var sourcePaths = new HashSet<string>(project.Sources, StringComparer.Ordinal);
        //UI files declare named controls and an initialization method that the compiler synthesizes, so they have no source on disk
        var declared = XamlMembers(root);
        var cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var namespaces = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var reported = 0;

        foreach (var diagnostic in project.Compilation.GetDiagnostics())
        {
            //only errors are reported; warnings belong to the real build, which would otherwise flood the screen with suggestions
            if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                continue;

            var span = diagnostic.Location.GetLineSpan();
            //trees from a generator are not project sources, so errors they report are dropped
            if (string.IsNullOrEmpty(span.Path)
                || span.Path == ProjectCompilation.GeneratedPath
                || !sourcePaths.Contains(span.Path))
                continue;

            var line = span.StartLinePosition.Line + 1;
            var sourceLine = LineOf(cache, span.Path, line);

            //a name the catalog pass already reported would only be noise here
            //names declared in UI files are skipped too since the UI compiler supplies them
            var identifier = IdentifierAt(sourceLine, span.StartLinePosition.Character);
            if (identifier is not null && (covered.Contains(identifier) || declared.Contains(identifier)))
                continue;

            //try a concrete fix first; when it applies, skip the generic advice
            var fixes = Fixes(project.Compilation, diagnostic, namespaces);

            bag.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                Path.GetRelativePath(root, span.Path).Replace('\\', '/'),
                line,
                span.StartLinePosition.Character + 1,
                Math.Max(1, diagnostic.Location.SourceSpan.Length),
                sourceLine,
                string.Empty,
                diagnostic.Descriptor.HelpLinkUri,
                fixes));

            reported++;
        }

        RunPolicyChecks(project.Compilation, sourcePaths, root, cache, bag);
        Trace.Log($"Roslyn reported {reported} error(s)");
    }

    //PolicyChecks the analyzers that run over the bound compilation
    //they cover rules the compiler has no opinion of, such as a mod taking memory management into its own hands
    private static readonly ImmutableArray<DiagnosticAnalyzer> PolicyChecks =
        ImmutableArray.Create<DiagnosticAnalyzer>(new MemorySafetyAnalyzer());

    //RunPolicyChecks runs the policy analyzers over the compilation the project check already built
    //parsing and binding happen once, so the extra pass costs only the rules themselves
    private static void RunPolicyChecks(Compilation compilation, HashSet<string> sourcePaths, string root,
        Dictionary<string, string[]> cache, DiagnosticBag bag)
    {
        var found = compilation.WithAnalyzers(PolicyChecks).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();

        foreach (var diagnostic in found)
        {
            //an error would stop the build, so only a rule that means it reports one
            if (diagnostic.Severity is not (Microsoft.CodeAnalysis.DiagnosticSeverity.Warning
                or Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                continue;

            var span = diagnostic.Location.GetLineSpan();
            if (string.IsNullOrEmpty(span.Path) || !sourcePaths.Contains(span.Path))
                continue;

            var line = span.StartLinePosition.Line + 1;
            bag.Add(new Diagnostic(
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                    ? DiagnosticSeverity.Error
                    : DiagnosticSeverity.Warning,
                diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                Path.GetRelativePath(root, span.Path).Replace('\\', '/'),
                line,
                span.StartLinePosition.Character + 1,
                Math.Max(1, diagnostic.Location.SourceSpan.Length),
                LineOf(cache, span.Path, line),
                string.Empty,
                diagnostic.Descriptor.HelpLinkUri,
                Advice(diagnostic)));
        }
    }

    //Advice reads the line a policy check attached through its advice property, which the renderer shows as the fix
    private static List<Suggestion> Advice(RoslynDiagnostic diagnostic)
        => diagnostic.Properties.TryGetValue(MemorySafetyAnalyzer.AdviceProperty, out var advice)
            && !string.IsNullOrEmpty(advice)
                ? [Suggestion.Text(advice)]
                : [];

    //Fixes computes the advice for one error: the symbol based suggestion when there is one and the static table otherwise
    //the reference compile path uses this too, so a referenced project is advised the same way as the project being built
    //namespaces memoizes the missing using candidates, which is why it is carried in rather than built per call
    internal static List<Suggestion> Fixes(Compilation compilation, RoslynDiagnostic diagnostic,
        Dictionary<string, List<string>> namespaces)
    {
        var suggestion = SuggestFor(compilation, diagnostic, namespaces);
        return suggestion is not null
            ? [suggestion]
            : CompileAdvice.Text(diagnostic.Id).Select(Suggestion.Text).ToList();
    }

    //SuggestFor derives one actionable fix for a compile error, or null when it cannot
    //the name, member and argument list cases are handled here; the rest fall back to the static table in CompileAdvice
    private static Suggestion? SuggestFor(Compilation compilation, RoslynDiagnostic diagnostic,
        Dictionary<string, List<string>> namespaces)
    {
        var tree = diagnostic.Location.SourceTree;
        if (tree is null)
            return null;

        var model = compilation.GetSemanticModel(tree);
        var span = diagnostic.Location.SourceSpan;

        return diagnostic.Id switch
        {
            "CS0103" or "CS0246" => SuggestName(model, compilation, diagnostic, span, namespaces),
            "CS0117" or "CS1061" => SuggestMember(model, compilation, diagnostic, span),
            "CS1729" => SuggestConstructor(model, compilation, diagnostic, span),
            "CS7036" or "CS1501" => SuggestOverloads(model, diagnostic, span),
            _ => null,
        };
    }

    //SuggestName turns a name that does not resolve into the most useful advice
    //a name declared in a namespace this file does not import is a missing using, and telling the user to rename it
    //to whatever symbol happens to sit nearby would send them the wrong way
    private static Suggestion? SuggestName(SemanticModel model, Compilation compilation, RoslynDiagnostic diagnostic,
        TextSpan span, Dictionary<string, List<string>> namespaces)
    {
        var written = TextOf(diagnostic, span);
        if (string.IsNullOrEmpty(written))
            return null;

        //a qualified use reports the first part that does not resolve, so only that part can be a missing using
        var name = LeadingIdentifier(written);

        //a name that still resolves to a type or namespace here is some other problem, so keep to the closeness advice
        if (model.LookupNamespacesAndTypes(span.Start, name: name).Length == 0)
        {
            var declared = DeclaringNamespaces(compilation, name, namespaces);
            if (declared.Count == 1)
                return Suggestion.Text($"`{name}` is declared in `{declared[0]}`, add `using {declared[0]};`");
            if (declared.Count > 1)
                return Suggestion.Text($"`{name}` is declared in {Joined(declared)}, add a using for the one you meant");
        }

        var names = model.LookupSymbols(span.Start).Select(symbol => symbol.Name);
        var candidate = Similarity.Closest(written, names);
        if (candidate is null)
            return null;

        var start = diagnostic.Location.GetLineSpan().StartLinePosition;
        return Suggestion.Replace(
            $"did you mean `{candidate}`?",
            start.Line + 1,
            start.Character + 1,
            span.Length,
            candidate,
            Applicability.MaybeIncorrect);
    }

    //LeadingIdentifier takes the part before the first dot, which is the name that fails to resolve
    private static string LeadingIdentifier(string text)
    {
        var dot = text.IndexOf('.');
        return dot < 0 ? text : text[..dot];
    }

    //DeclaringNamespaces lists the namespaces holding a type of that name, nearest few only so the advice stays readable
    //looked up once per name, a project tends to trip over the same missing using across many lines
    private static List<string> DeclaringNamespaces(Compilation compilation, string name,
        Dictionary<string, List<string>> cache)
    {
        if (cache.TryGetValue(name, out var cached))
            return cached;

        var found = new List<string>();
        CollectNamespaces(compilation.GlobalNamespace, name, compilation, found);
        cache[name] = found;
        return found;
    }

    //CollectNamespaces walks the compilation's namespaces, both the project's own and the referenced assemblies'
    //a type this assembly cannot reach is not something a using can fix, so accessibility is checked here
    private static void CollectNamespaces(INamespaceSymbol space, string name, Compilation compilation,
        List<string> found)
    {
        foreach (var type in space.GetTypeMembers(name))
        {
            if (!compilation.IsSymbolAccessibleWithin(type, compilation.Assembly))
                continue;

            var declared = type.ContainingNamespace.ToDisplayString();
            if (declared.Length > 0 && !found.Contains(declared, StringComparer.Ordinal))
                found.Add(declared);
            if (found.Count >= MaxNamespaces)
                return;
        }

        foreach (var nested in space.GetNamespaceMembers())
        {
            CollectNamespaces(nested, name, compilation, found);
            if (found.Count >= MaxNamespaces)
                return;
        }
    }

    //Joined renders namespaces as a comma separated list of code spans
    private static string Joined(IEnumerable<string> namespaces)
        => string.Join(", ", namespaces.Select(item => $"`{item}`"));

    //SuggestMember names the member a type really carries, since a member error is about the name and not about a namespace
    //suggesting a using directive for an extension method that does not exist sends the reader down the wrong path
    private static Suggestion? SuggestMember(SemanticModel model, Compilation compilation,
        RoslynDiagnostic diagnostic, TextSpan span)
    {
        var root = diagnostic.Location.SourceTree?.GetRoot();
        var node = root?.FindNode(span, getInnermostNodeForTie: true);
        var access = node?.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
        if (access?.Name is not IdentifierNameSyntax name)
            return null;

        var type = model.GetTypeInfo(access.Expression).Type;
        if (type is null)
            return null;

        var written = name.Identifier.Text;

        //the member may be declared in the declaring assembly but kept internal, and the compiler's view of another assembly
        //leaves those out entirely, so a name based suggestion alone would leave the reader guessing why nothing matches
        //the metadata still carries them, and reading it is what answers `NcServer.Connections` correctly
        var hidden = HiddenAccessibility(compilation, type, written);
        if (hidden is not null)
            return Suggestion.Text($"`{type.Name}.{written}` is declared {hidden} and is not part of what mods can call");

        var candidate = Similarity.Closest(written, Members(type).Select(member => member.Name));
        if (candidate is not null)
        {
            var start = name.GetLocation().GetLineSpan().StartLinePosition;
            return Suggestion.Replace(
                $"did you mean `{candidate}`?",
                start.Line + 1,
                start.Character + 1,
                written.Length,
                candidate,
                Applicability.MaybeIncorrect);
        }

        //nothing close enough to be a typo, so what the type actually offers is listed
        //only members a mod can reach are named, since advertising a hidden one would mislead again
        var visible = Members(type)
            .Where(member => compilation.IsSymbolAccessibleWithin(member, compilation.Assembly))
            .Select(member => member.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (visible.Count == 0)
            return null;

        //the nearest names come first, so the line leads somewhere instead of starting at whatever the metadata happened to hold
        //the rest are only counted, since naming them all buries the hint the reader came for
        var ranked = Similarity.Rank(written, visible, MaxMembers, int.MaxValue);
        var shown = string.Join(", ", ranked.Select(member => $"`{member}`"));
        var extra = visible.Count - ranked.Count;
        return Suggestion.Text($"`{type.Name}` declares {shown}{(extra > 0 ? $" and {extra} more" : string.Empty)}");
    }

    //Members yields the member symbols of a type including its base types, internal ones included
    //accessors cannot be written by name and object's members are on every type, so both are left out rather than offered
    private static IEnumerable<ISymbol> Members(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member.Kind is SymbolKind.NamedType)
                    continue;

                if (member.ContainingType?.SpecialType == SpecialType.System_Object)
                    continue;

                if (member is IMethodSymbol method && method.MethodKind
                    is MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor
                    or MethodKind.PropertyGet or MethodKind.PropertySet
                    or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise)
                    continue;

                yield return member;
            }
        }
    }

    //HiddenAccessibility reports how a member that exists in the declaring assembly is kept out of the compiler's view of it
    //null means no such member is there at all, and the reader gets the ordinary name based advice instead
    private static string? HiddenAccessibility(Compilation compilation, ITypeSymbol type, string name)
    {
        var assemblyName = type.ContainingAssembly?.Name;
        var typeName = type.ToDisplayString();
        if (string.IsNullOrEmpty(assemblyName) || string.IsNullOrEmpty(typeName))
            return null;

        //one read per member per run is enough, the same missing member repeats across files
        var key = $"{assemblyName}|{typeName}|{name}";
        if (Hidden.TryGetValue(key, out var cached))
            return cached;

        var found = ReadHiddenAccessibility(compilation, assemblyName, typeName, name);
        Hidden[key] = found;
        return found;
    }

    //ReadHiddenAccessibility scans the metadata of the assembly that declares the type for a member of that name
    //the reference is picked by file name, which is the convention ncm writes its own assemblies under
    private static string? ReadHiddenAccessibility(Compilation compilation, string assemblyName, string typeName, string name)
    {
        var path = compilation.References
            .OfType<PortableExecutableReference>()
            .Select(reference => reference.FilePath)
            .FirstOrDefault(file => file is not null
                && string.Equals(Path.GetFileNameWithoutExtension(file), assemblyName, StringComparison.OrdinalIgnoreCase));
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata ? ReadHiddenAccessibility(pe.GetMetadataReader(), typeName, name) : null;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            Trace.Log($"cannot read {path} for member advice: {e.GetType().Name}");
            return null;
        }
    }

    //Scans one module for the member, answering null when the type is not in it or the member is public
    private static string? ReadHiddenAccessibility(MetadataReader reader, string typeName, string name)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            if (!string.Equals(MetadataName(reader, definition), typeName, StringComparison.Ordinal))
                continue;

            foreach (var fieldHandle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (reader.GetString(field.Name) == name)
                    return AccessibilityWord((int)(field.Attributes & FieldAttributes.FieldAccessMask));
            }

            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                if (reader.GetString(property.Name) != name)
                    continue;

                //a property keeps no accessibility of its own, its accessor carries it
                var accessors = property.GetAccessors();
                var accessor = accessors.Getter.IsNil ? accessors.Setter : accessors.Getter;
                return accessor.IsNil
                    ? null
                    : AccessibilityWord((int)(reader.GetMethodDefinition(accessor).Attributes & MethodAttributes.MemberAccessMask));
            }

            foreach (var methodHandle in definition.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) == name)
                    return AccessibilityWord((int)(method.Attributes & MethodAttributes.MemberAccessMask));
            }

            return null;
        }

        return null;
    }

    //AccessibilityWord names an access mask, null when it is public and therefore not kept out of the view
    //field and method masks share their values, so one table covers both
    private static string? AccessibilityWord(int mask)
        => mask switch
        {
            1 => "private",
            2 => "private protected",
            3 => "internal",
            4 => "protected",
            5 => "protected internal",
            _ => null,
        };

    //MetadataName the full name of a type definition, spelled the way the symbol layer spells it
    private static string MetadataName(MetadataReader reader, TypeDefinition definition)
    {
        var ns = definition.Namespace.IsNil ? string.Empty : reader.GetString(definition.Namespace);
        var name = reader.GetString(definition.Name);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    //SuggestConstructor reports the constructors a type really declares when the argument list matches none of them
    //telling the user to pick one with a matching parameter list is wrong when no such constructor exists at all,
    //so what is actually there is looked up and listed instead
    private static Suggestion? SuggestConstructor(SemanticModel model, Compilation compilation,
        RoslynDiagnostic diagnostic, TextSpan span)
    {
        var creation = NodeAt<BaseObjectCreationExpressionSyntax>(diagnostic, span);
        if (creation is null)
            return null;

        var type = model.GetTypeInfo(creation).Type;
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error)
            return null;

        var constructors = named.InstanceConstructors
            .Where(constructor => compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly))
            .ToList();

        if (constructors.Count == 0)
            return Suggestion.Text($"`{named.Name}` declares no accessible constructor, `new` cannot build it");

        return Suggestion.Text($"`{named.Name}` is built as {Signatures(constructors)}");
    }

    //SuggestOverloads lists the real overloads when a call passes the wrong number of arguments
    private static Suggestion? SuggestOverloads(SemanticModel model, RoslynDiagnostic diagnostic, TextSpan span)
    {
        var invocation = NodeAt<InvocationExpressionSyntax>(diagnostic, span);
        if (invocation is null)
            return null;

        var overloads = model.GetMemberGroup(invocation.Expression).OfType<IMethodSymbol>().ToList();
        if (overloads.Count == 0)
            return null;

        return Suggestion.Text($"`{overloads[0].Name}` is called as {Signatures(overloads)}");
    }

    //NodeAt walks up from the reported span to the first node of the requested kind, the one the arguments belong to
    private static T? NodeAt<T>(RoslynDiagnostic diagnostic, TextSpan span) where T : SyntaxNode
    {
        var node = diagnostic.Location.SourceTree?.GetRoot().FindNode(span, getInnermostNodeForTie: true);
        return node?.AncestorsAndSelf().OfType<T>().FirstOrDefault();
    }

    //Signatures renders the parameter lists of real members, capped so the advice stays readable
    private static string Signatures(IReadOnlyList<IMethodSymbol> members)
    {
        var shown = string.Join(", ", members.Take(MaxSignatures).Select(member => $"`{Signature(member)}`"));
        var extra = members.Count - MaxSignatures;
        return extra > 0 ? $"{shown} and {extra} more" : shown;
    }

    //Signature renders one member as its name and parameter types, which is what a call site has to match
    //a constructor is named .ctor in metadata, so its type name stands in for it
    private static string Signature(IMethodSymbol member)
    {
        var name = member.MethodKind == MethodKind.Constructor ? member.ContainingType.Name : member.Name;
        var parameters = string.Join(", ", member.Parameters.Select(
            parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return $"{name}({parameters})";
    }

    //TextOf reads the source text a diagnostic points at
    private static string TextOf(RoslynDiagnostic diagnostic, TextSpan span)
        => diagnostic.Location.SourceTree?.GetText().ToString(span) ?? string.Empty;

    //XamlMembers collects the names declared in UI files
    //the UI compiler generates a field per x:Name plus an InitializeComponent, none of which reach disk
    private static HashSet<string> XamlMembers(string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in SourceFiles.Enumerate(root, "*.axaml"))
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException e)
            {
                Trace.Log($"cannot read {path}: {e.Message}");
                continue;
            }

            foreach (Match match in XamlName.Matches(text))
                names.Add(match.Groups[1].Value);
        }

        //only a project with UI files can call that initialization method, so add it only when such files exist
        if (names.Count > 0)
            names.Add("InitializeComponent");
        return names;
    }

    //LineOf returns the text of a line, caching the whole file per path
    private static string LineOf(Dictionary<string, string[]> cache, string path, int line)
    {
        if (!cache.TryGetValue(path, out var lines))
        {
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                lines = Array.Empty<string>();
            }
            cache[path] = lines;
        }

        var index = line - 1;
        return index >= 0 && index < lines.Length ? lines[index] : string.Empty;
    }

    //IdentifierAt returns the identifier containing a 0-based column
    //Roslyn sometimes points mid-identifier, so back up to the word start and take the whole word
    private static string? IdentifierAt(string line, int column)
    {
        if (string.IsNullOrEmpty(line) || column < 0 || column >= line.Length)
            return null;

        var start = column;
        while (start > 0 && IsWordChar(line[start - 1]))
            start--;

        var end = start;
        while (end < line.Length && IsWordChar(line[end]))
            end++;

        return end > start ? line[start..end] : null;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
}
