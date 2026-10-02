using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MafDoctor.Commands;

/// <summary>Outcome of compiling one registry example snippet.</summary>
/// <param name="Status">
/// <c>ok</c> compiles; <c>fail</c> has compiler errors against the MAF packages;
/// <c>unresolved</c> only errors on names the snippet never declares and no
/// referenced assembly defines (user types or placeholders); <c>invalid</c> is
/// not C# as written (placeholders such as <c>...</c> or prose); <c>empty</c>
/// has no code (comments only).
/// </param>
/// <param name="Prelude">The usings and declarations inferred for this snippet (reused by the claim check).</param>
internal sealed record ExampleResult(
    string Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Inferred,
    IReadOnlySet<string> DiagnosticIds,
    IReadOnlyList<string> Prelude,
    string Source = "",
    IReadOnlyList<string>? AllDiagnostics = null);

/// <summary>
/// Compiles registry example snippets against the reference assemblies of one
/// MAF train (ROADMAP Q-02, compiler oracle). Snippets are fragments: they use
/// variables they never declare (<c>agent</c>, <c>innerAgent</c>) and mix
/// statements with type declarations. Each snippet is compiled as a C# script
/// (statements, declarations and top-level <c>await</c> are all legal), and
/// what it leaves out is inferred from how it is used: a local's type from the
/// parameter it is passed to, the type that has the member being accessed, or
/// the other side of an assignment; a missing using from the type or extension
/// method that needs it. Only MAF's own namespaces are imported up front, so a
/// snippet's own usings decide which of two same-named types it means (MAF
/// moves types between namespaces). What is still wrong after that is a real
/// compile error against that MAF version, unless it only involves names no
/// assembly defines.
/// </summary>
internal sealed class ExampleCompiler
{
    private const int MaxRounds = 16;

    private static readonly CSharpParseOptions ScriptParse = new(LanguageVersion.Latest, kind: SourceCodeKind.Script);
    // Nullable on, so a cs_warning such as CS8625 can be observed.
    private static readonly CSharpCompilationOptions Options = new(
        OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions: NullableContextOptions.Enable,
        allowUnsafe: true);

    // Examples show only the members that changed, so a class in a snippet
    // rarely implements every abstract or interface member.
    private static readonly HashSet<string> IgnoredErrors = ["CS0534", "CS0535"];

    // A snippet that is really a method body (yield) cannot be checked as a script.
    private static readonly HashSet<string> UncheckableErrors = ["CS7020", "CS1624"];

    // `using X.Y;`, `using static X.Y;` and `using A = X.Y;` (not `using var x = ...;`).
    private static readonly Regex UsingDirective = new(
        @"^\s*using\s+(?:static\s+[A-Za-z_][\w.]*|[A-Za-z_]\w*\s*=\s*[A-Za-z_][\w.<>, ]*|[A-Za-z_][\w.]*)\s*;\s*$",
        RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    private static readonly string[] CommonNamespaces =
    [
        "System", "System.Collections.Generic", "System.ComponentModel", "System.IO", "System.Linq",
        "System.Net.Http", "System.Text", "System.Text.Json", "System.Threading", "System.Threading.Tasks",
        "Microsoft.Extensions.AI", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging", "Microsoft.Extensions.Configuration",
    ];

    private readonly IReadOnlyList<MetadataReference> _references;
    private readonly HashSet<string> _otherSideTypeNames;
    private readonly List<string> _commonUsings;
    private readonly List<string> _mafUsings;
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesByName = new(StringComparer.Ordinal);
    // Member name -> (receiver type, type that declares the member; the static class for extensions).
    private readonly Dictionary<string, List<(ITypeSymbol Receiver, INamedTypeSymbol Declarer)>> _receiversByMember = new(StringComparer.Ordinal);

    /// <param name="referencePaths">Compile-time assemblies of the train (MAF packages, dependencies, framework reference packs).</param>
    /// <param name="otherSideTypeNames">
    /// Public type names of the OTHER side of the train. A name missing here but
    /// present there is a real error (the type does not exist in this version),
    /// not a user placeholder.
    /// </param>
    public ExampleCompiler(IEnumerable<string> referencePaths, IEnumerable<string>? otherSideTypeNames = null)
    {
        _references = Deduplicate(referencePaths).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        _otherSideTypeNames = new HashSet<string>(otherSideTypeNames ?? [], StringComparer.Ordinal);

        var probe = CSharpCompilation.Create("index", references: _references, options: Options);
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in _references)
        {
            if (probe.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
            foreach (var type in PublicTypes(assembly.GlobalNamespace))
            {
                namespaces.Add(type.ContainingNamespace.ToDisplayString());
                Add(_typesByName, type.Name, type);
                foreach (var member in type.GetMembers())
                {
                    if (member.DeclaredAccessibility != Accessibility.Public || member.IsImplicitlyDeclared) continue;
                    if (member is IMethodSymbol { IsExtensionMethod: true } extension && extension.Parameters.Length > 0)
                    {
                        if (extension.Parameters[0].Type is not ITypeParameterSymbol)
                            Add(_receiversByMember, extension.Name, (extension.Parameters[0].Type, type));
                    }
                    else if (!member.IsStatic && member.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event)
                    {
                        Add(_receiversByMember, member.Name, ((ITypeSymbol)type, type));
                    }
                }
            }
        }
        _commonUsings = CommonNamespaces.Where(namespaces.Contains).Select(n => $"using {n};").ToList();
        _mafUsings = namespaces.Where(n => n == "Microsoft.Agents.AI" || n.StartsWith("Microsoft.Agents.AI.", StringComparison.Ordinal))
            .Select(n => $"using {n};").ToList();
        TypeNames = _typesByName.Keys.ToHashSet(StringComparer.Ordinal);
        AssemblyNames = _references.OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath ?? ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Public type names in the references (pass to the other side's compiler).</summary>
    public IReadOnlySet<string> TypeNames { get; }

    /// <summary>Simple names of the referenced assemblies (a MAF package id is its assembly name).</summary>
    public IReadOnlySet<string> AssemblyNames { get; }

    /// <param name="snippet">The example code.</param>
    /// <param name="hintType">The entry's <c>type</c>: preferred when several types could own a member.</param>
    /// <param name="fixedPrelude">
    /// Declarations inferred by an earlier compile (<see cref="ExampleResult.Prelude"/>). When given,
    /// nothing is inferred: the claim check compiles the OLD code, with the locals typed as on the
    /// old side, against the NEW packages, the way a user's existing code meets an upgrade.
    /// </param>
    /// <param name="snippetUsingsOnly">
    /// Import MAF namespaces only through the snippet's own usings. For a claim that a type
    /// moved (CS0246), so the old using is not rescued by the new namespace.
    /// </param>
    public ExampleResult Compile(string snippet, string? hintType = null, IReadOnlyList<string>? fixedPrelude = null, bool snippetUsingsOnly = false)
    {
        snippet = (snippet ?? "").Replace("\r\n", "\n");
        var (userUsings, body) = SplitUsings(snippet);
        var bodyTree = CSharpSyntaxTree.ParseText(body, ScriptParse);
        if (!bodyTree.GetRoot().DescendantTokens().Any(t => !t.IsKind(SyntaxKind.EndOfFileToken)))
            return new("empty", [], [], [], new HashSet<string>(), []);
        // Some statements are legal in a method but not at script level (`using var x = ...;`):
        // when the snippet only parses as a method body, compile it as one.
        string? methodWrapper = null;
        var syntaxErrors = bodyTree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (syntaxErrors.Count > 0)
        {
            const string header = "async global::System.Threading.Tasks.Task __Example() {";
            var asMethod = CSharpSyntaxTree.ParseText($"{header}\n{body}}}\n", ScriptParse);
            if (asMethod.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                return new("invalid", syntaxErrors.Select(d => $"{d.Id} {d.GetMessage()}").Distinct().ToList(), [], [], new HashSet<string>(), []);
            methodWrapper = header;
        }

        var prelude = new List<string>(fixedPrelude ?? []);
        var inferred = new List<string>();
        var unresolvedNames = new SortedSet<string>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        // Type name -> the using added for it; undone when the snippet turns out to mean its own type.
        var importedFor = new Dictionary<string, string>(StringComparer.Ordinal);
        hintType = ShortTypeName(hintType);

        // A snippet that is only overriding members (no class around them) is a member
        // fragment of a class deriving from the entry's type: wrap it in one, so a
        // signature that no longer matches shows up as CS0115.
        var classWrapper = methodWrapper is null ? WrapperFor(bodyTree, hintType) : null;

        Compilation compilation = null!;
        SyntaxTree tree = null!;
        int userUsingsEnd = 0, declarationsStart = 0, bodyStart = 0;
        bool Counted(Diagnostic d) => d.Location.IsInSource && (d.Location.SourceSpan.Start < userUsingsEnd || d.Location.SourceSpan.Start >= bodyStart);
        var globalUsings = snippetUsingsOnly ? _commonUsings : _commonUsings.Concat(_mafUsings).ToList();
        // A local passed to a call that does not bind yet (its receiver is still undeclared)
        // waits a round for the parameter type; only a round without progress allows the
        // weaker guess from the members accessed on it.
        var allowWeak = false;

        for (var round = 0; round < MaxRounds; round++)
        {
            // Layout: the snippet's usings (checked), MAF and BCL usings, inferred usings
            // and aliases, inferred declarations, then the snippet body (checked).
            var source = new StringBuilder();
            foreach (var line in userUsings) source.Append(line).Append('\n');
            userUsingsEnd = source.Length;
            var preludeUsings = prelude.Where(l => l.StartsWith("using ", StringComparison.Ordinal)).ToList();
            foreach (var line in globalUsings.Concat(preludeUsings).Except(userUsings)) source.Append(line).Append('\n');
            // Inferred declarations become fields of a class wrapper, but stay at script
            // level (visible to it) around a method wrapper, where stub classes are not legal.
            if (classWrapper is not null) source.Append(classWrapper).Append('\n');
            declarationsStart = source.Length;
            foreach (var line in prelude.Except(preludeUsings)) source.Append(line).Append('\n');
            if (methodWrapper is not null) source.Append(methodWrapper).Append('\n');
            bodyStart = source.Length;
            source.Append(body);
            if (classWrapper is not null || methodWrapper is not null) source.Append("}\n");
            tree = CSharpSyntaxTree.ParseText(source.ToString(), ScriptParse);
            compilation = CSharpCompilation.CreateScriptCompilation("example", tree, _references, Options);

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var progress = false;
            foreach (var diagnostic in compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && Counted(d)))
            {
                // With a fixed prelude only the ambiguity our own usings create is resolved.
                if (fixedPrelude is not null && diagnostic.Id != "CS0104") continue;
                if (diagnostic.Id is "CS0117" or "CS1061" && WrongImport(diagnostic, importedFor, prelude, unresolvedNames))
                {
                    progress = true;
                    continue;
                }
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                // CS1929 is reported on the receiver of `x.Member`, CS1061 on the member name. CS0411 and
                // CS1501 can mean the same: a same-named extension (LINQ Count) hides the one not imported.
                if (diagnostic.Id is "CS1061" or "CS1929" or "CS0411" or "CS1501")
                {
                    if (node.Parent is MemberAccessExpressionSyntax onReceiver && onReceiver.Expression == node) node = onReceiver.Name;
                    else if (node is MemberAccessExpressionSyntax whole) node = whole.Name;
                }
                var name = node switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    GenericNameSyntax g => g.Identifier.ValueText,
                    _ => null,
                };
                if (name is null) continue;
                switch (diagnostic.Id)
                {
                    case "CS0104" when settled.Add("alias:" + name):
                    {
                        // Ambiguous between two imported namespaces: prefer the snippet's own using.
                        var candidates = model.GetSymbolInfo(node).CandidateSymbols.OfType<INamedTypeSymbol>().ToList();
                        var pick = candidates.FirstOrDefault(c => userUsings.Contains($"using {c.ContainingNamespace.ToDisplayString()};"))
                            ?? candidates.FirstOrDefault(c => c.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal))
                            ?? candidates.FirstOrDefault();
                        if (pick is not null && !pick.IsGenericType)
                        {
                            prelude.Insert(0, $"using {name} = {pick.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)};");
                            progress = true;
                        }
                        break;
                    }
                    case "CS1061" or "CS1929" or "CS0411" or "CS1501" when node.Parent is MemberAccessExpressionSyntax access && access.Name == node && settled.Add("ext:" + name):
                    {
                        // An extension method whose namespace the snippet did not import.
                        var receiver = model.GetTypeInfo(access.Expression).Type;
                        var ns = receiver is null ? null : FindExtensionNamespace(name, receiver);
                        if (ns is not null && !prelude.Contains($"using {ns};"))
                        {
                            prelude.Insert(0, $"using {ns};");
                            inferred.Add($"{name}: using {ns}");
                            progress = true;
                        }
                        break;
                    }
                    case "CS0103" or "CS0246" when !settled.Contains(name):
                    {
                        var resolution = Resolve(name, node, model, hintType, diagnostic.Id == "CS0246", bodyStart, allowWeak);
                        if (resolution is null) break; // try again once the context binds
                        settled.Add(name);
                        progress = true;
                        switch (resolution.Value.Kind)
                        {
                            case "using":
                                prelude.Insert(0, $"using {resolution.Value.Text};");
                                inferred.Add($"{name}: using {resolution.Value.Text}");
                                importedFor[name] = $"using {resolution.Value.Text};";
                                break;
                            case "local": prelude.Add($"{resolution.Value.Text} {name} = default!;"); inferred.Add($"{name}: {resolution.Value.Text}"); break;
                            case "stub": prelude.Insert(0, resolution.Value.Text); unresolvedNames.Add(name); break;
                            case "missing": break; // stays an error
                            default: unresolvedNames.Add(name); break;
                        }
                        break;
                    }
                }
            }
            if (progress) allowWeak = false;
            else if (!allowWeak && fixedPrelude is null) allowWeak = true;
            else break;
        }

        var bodyFirstLine = tree.GetText().Lines.GetLineFromPosition(bodyStart).LineNumber;
        string Where(Diagnostic d) => d.Location.SourceSpan.Start < userUsingsEnd
            ? "using"
            : $"line {tree.GetLineSpan(d.Location.SourceSpan).StartLinePosition.Line - bodyFirstLine + 1}";

        var counted = compilation.GetDiagnostics().Where(Counted).ToList();
        var ids = counted.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        // With a fixed prelude, a local whose old type no longer exists is a break the
        // user sees too: report it with the snippet's diagnostics.
        if (fixedPrelude is not null)
            ids.UnionWith(compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.IsInSource
                    && d.Location.SourceSpan.Start >= declarationsStart && d.Location.SourceSpan.Start < bodyStart)
                .Select(d => d.Id));
        var errors = new List<string>();
        var unresolved = new List<string>(unresolvedNames.Select(n => $"{n} is never declared and no referenced assembly defines it"));
        foreach (var d in counted.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            // Non-CS ids come from [Experimental] attributes ("for evaluation purposes only"):
            // a property of the package, not of the example.
            if (!d.Id.StartsWith("CS", StringComparison.Ordinal) || IgnoredErrors.Contains(d.Id)) continue;
            // A derived class in a snippet that omits its constructor: the implicit
            // constructor cannot call a base constructor that takes arguments.
            if (d.Id is "CS7036" or "CS1729" && tree.GetRoot().FindNode(d.Location.SourceSpan) is BaseTypeDeclarationSyntax) continue;
            var message = d.GetMessage();
            if (UncheckableErrors.Contains(d.Id))
            {
                unresolved.Add($"{Where(d)}: {d.Id} {message} (a method body, not checkable as a script)");
                continue;
            }
            if (d.Id is "CS0103" or "CS0246" && unresolvedNames.Any(n => message.Contains($"'{n}'", StringComparison.Ordinal)))
                continue; // already listed as unresolved
            var text = $"{Where(d)}: {d.Id} {message}";
            if (d.Id == "CS0103" || unresolvedNames.Any(n => message.Contains($"'{n}", StringComparison.Ordinal)))
                unresolved.Add(text);
            else
                errors.Add(text);
        }
        var status = errors.Count > 0 ? "fail" : unresolved.Count > 0 ? "unresolved" : "ok";
        var all = compilation.GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.IsInSource)
            .Select(d => $"{tree.GetLineSpan(d.Location.SourceSpan).StartLinePosition.Line + 1}: {d.Severity} {d.Id} {d.GetMessage()}")
            .ToList();
        return new(status, errors.Distinct().ToList(), unresolved.Distinct().ToList(), inferred, ids, prelude, tree.ToString(), all);
    }

    private string? WrapperFor(SyntaxTree bodyTree, string? hintType)
    {
        var members = ((CompilationUnitSyntax)bodyTree.GetRoot()).Members;
        var overridesOnly = members.Count > 0
            && members.All(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax)
            && members.Any(m => m.Modifiers.Any(SyntaxKind.OverrideKeyword));
        if (!overridesOnly || hintType is null || !_typesByName.TryGetValue(hintType, out var candidates)) return null;
        var baseType = candidates.Where(t => t.TypeKind == TypeKind.Class && !t.IsSealed && !t.IsStatic).OrderBy(Rank).FirstOrDefault();
        return baseType is null ? null : $"abstract class __Example : {Display(baseType)} {{";
    }

    /// <summary>
    /// A using added for an unknown type name pulled in a referenced type that lacks the
    /// members the snippet uses (<c>Document.TenantId</c>): the snippet means a type of its
    /// own. Replace the using with a stub so the name counts as unresolved, not as an error.
    /// </summary>
    private static bool WrongImport(Diagnostic diagnostic, Dictionary<string, string> importedFor, List<string> prelude, ISet<string> unresolvedNames)
    {
        var message = diagnostic.GetMessage();
        foreach (var (name, usingLine) in importedFor)
        {
            if (!message.StartsWith($"'{name}' does not contain", StringComparison.Ordinal)) continue;
            prelude.Remove(usingLine);
            prelude.Insert(0, $"class {name} {{ }}");
            unresolvedNames.Add(name);
            importedFor.Remove(name);
            return true;
        }
        return false;
    }

    private (string Kind, string Text)? Resolve(string name, SyntaxNode node, SemanticModel model, string? hintType, bool typePosition, int bodyStart, bool allowWeak)
    {
        // A type that exists in the references but whose namespace is not imported.
        if (_typesByName.TryGetValue(name, out var types))
        {
            var type = types.OrderBy(Rank).First();
            // A nested type is not reachable through a namespace using: alias it.
            if (type.ContainingType is not null && !type.IsGenericType)
                return ("using", $"{name} = {type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}");
            if (!type.ContainingNamespace.IsGlobalNamespace)
                return ("using", type.ContainingNamespace.ToDisplayString());
        }
        var looksLikeType = typePosition || char.IsUpper(name[0]);
        if (looksLikeType)
        {
            // Exists only on the other side of the train: a genuine error in this snippet.
            if (_otherSideTypeNames.Contains(name)) return ("missing", "");
            // A type of the user's own (or a placeholder): stub it so the rest still compiles.
            if (typePosition || node.Parent is MemberAccessExpressionSyntax or ObjectCreationExpressionSyntax)
            {
                var arity = node is GenericNameSyntax g ? g.TypeArgumentList.Arguments.Count : 0;
                var parameters = arity == 0 ? "" : "<" + string.Join(", ", Enumerable.Range(1, arity).Select(i => $"T{i}")) + ">";
                return ("stub", $"class {name}{parameters} {{ }}");
            }
        }
        var inferredType = InferFromUses(name, node, model, hintType, bodyStart, allowWeak);
        if (inferredType is not null) return ("local", inferredType);
        return null;
    }

    /// <summary>
    /// The type of an undeclared local from all its uses, strongest evidence first: the
    /// parameter it is passed to, the other side of an assignment, a declared type, a
    /// collection's element type. Only then the type owning a member accessed on it.
    /// </summary>
    private string? InferFromUses(string name, SyntaxNode first, SemanticModel model, string? hintType, int bodyStart, bool allowWeak)
    {
        var uses = first.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(n => n.Identifier.ValueText == name && n.SpanStart >= bodyStart)
            .ToList();
        foreach (var use in uses.Where(u => u.Parent is ArgumentSyntax or AssignmentExpressionSyntax or EqualsValueClauseSyntax or ExpressionElementSyntax))
        {
            if (InferLocalType(use, model, hintType) is { } strong) return strong;
        }
        if (!allowWeak && uses.Any(u => u.Parent is ArgumentSyntax)) return null;
        foreach (var use in uses)
        {
            if (InferLocalType(use, model, hintType) is { } weak) return weak;
        }
        return null;
    }

    private string? InferLocalType(SyntaxNode node, SemanticModel model, string? hintType)
    {
        switch (node.Parent)
        {
            // x.Member / x.Member(...) / x.Member.Next(...)
            case MemberAccessExpressionSyntax access when access.Expression == node:
            {
                var member = access.Name.Identifier.ValueText;
                var arguments = access.Parent is InvocationExpressionSyntax call ? call.ArgumentList.Arguments : (SeparatedSyntaxList<ArgumentSyntax>?)null;
                // The next link: x.A.Next or x.A(...).Next
                SyntaxNode link = access.Parent is InvocationExpressionSyntax invoked ? invoked : access;
                var next = link.Parent is MemberAccessExpressionSyntax chained && chained.Expression == link ? chained.Name.Identifier.ValueText : null;
                var variable = node is IdentifierNameSyntax id ? id.Identifier.ValueText : "";
                return FindReceiverType(member, arguments, next, variable, hintType);
            }
            // Foo(x) / new Foo(x) / Foo(name: x)
            case ArgumentSyntax argument when argument.Parent is BaseArgumentListSyntax list && list.Parent is ExpressionSyntax or ConstructorInitializerSyntax:
            {
                // Among the overloads, keep those the rest of the call fits (argument count,
                // names, the types of arguments already known), then prefer the one whose
                // parameter shares the variable's name (messages -> messages).
                var info = model.GetSymbolInfo(list.Parent!);
                var index = list.Arguments.IndexOf(argument);
                var name = node is IdentifierNameSyntax id ? id.Identifier.ValueText : "";
                var fits = (info.Symbol is null ? info.CandidateSymbols : [info.Symbol]).OfType<IMethodSymbol>()
                    .Select(m => (Method: m, Parameter: ParameterFor(m, list, argument)))
                    .Where(c => c.Parameter is not null && !ContainsTypeParameter(c.Parameter.Type) && CallFits(c.Method, list, argument, model))
                    .OrderBy(c => name.EndsWith(c.Parameter!.Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ToList();
                if (fits.Count == 0) return null;
                var (method, parameter) = fits[0];
                var type = parameter!.IsParams && parameter.Type is IArrayTypeSymbol array && index >= method.Parameters.Length - 1 && list.Arguments.Count != method.Parameters.Length
                    ? array.ElementType : parameter.Type;
                return Display(type);
            }
            // [x] in a collection expression: the element type of the target collection.
            case ExpressionElementSyntax { Parent: CollectionExpressionSyntax collection }:
            {
                var target = model.GetTypeInfo(collection).ConvertedType;
                var element = target switch
                {
                    IArrayTypeSymbol array => array.ElementType,
                    INamedTypeSymbol named => named.AllInterfaces.Prepend(named)
                        .FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0],
                    _ => null,
                };
                return element is null or IErrorTypeSymbol || ContainsTypeParameter(element) ? null : Display(element);
            }
            // x = expr  /  Prop = x (object initializers included)
            case AssignmentExpressionSyntax assignment:
            {
                var other = assignment.Left == node ? assignment.Right : assignment.Left;
                var type = model.GetTypeInfo(other).Type ?? (model.GetSymbolInfo(other).Symbol as IPropertySymbol)?.Type;
                return type is null or IErrorTypeSymbol ? null : Display(type);
            }
            // T y = x;
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } when !declaration.Type.IsVar:
            {
                var type = model.GetTypeInfo(declaration.Type).Type;
                return type is null or IErrorTypeSymbol ? null : Display(type);
            }
            case AwaitExpressionSyntax:
                return "global::System.Threading.Tasks.Task";
            // The element type is the type that has the members the loop uses on its item
            // (entry.FileName); with none, dynamic leaves that access unchecked.
            case ForEachStatementSyntax loop when loop.Expression == node:
            {
                var item = loop.Identifier.ValueText;
                var use = loop.Statement.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                    .FirstOrDefault(a => a.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == item);
                var element = use is null ? null : FindReceiverType(
                    use.Name.Identifier.ValueText, (use.Parent as InvocationExpressionSyntax)?.ArgumentList.Arguments, null, item, hintType);
                return $"global::System.Collections.Generic.IEnumerable<{element ?? "dynamic"}>";
            }
            case InterpolationSyntax:
                return "string";
            case IfStatementSyntax or WhileStatementSyntax or PrefixUnaryExpressionSyntax:
                return "bool";
            case ExpressionStatementSyntax or ReturnStatementSyntax or ArrowExpressionClauseSyntax:
                return "object";
        }
        return null;
    }

    private static IParameterSymbol? ParameterFor(IMethodSymbol method, BaseArgumentListSyntax list, ArgumentSyntax argument)
    {
        if (argument.NameColon is { } named)
            return method.Parameters.FirstOrDefault(p => p.Name == named.Name.Identifier.ValueText);
        var index = list.Arguments.IndexOf(argument);
        return index < method.Parameters.Length ? method.Parameters[index]
            : method.Parameters.LastOrDefault() is { IsParams: true } last ? last : null;
    }

    /// <summary>Whether every other argument of the call has a parameter it converts to.</summary>
    private static bool CallFits(IMethodSymbol method, BaseArgumentListSyntax list, ArgumentSyntax skip, SemanticModel model)
    {
        if (list.Arguments.Count > method.Parameters.Length && method.Parameters.LastOrDefault()?.IsParams != true) return false;
        if (method.Parameters.Count(p => !p.IsOptional && !p.IsParams) > list.Arguments.Count) return false;
        foreach (var other in list.Arguments)
        {
            if (other == skip) continue;
            var parameter = ParameterFor(method, list, other);
            if (parameter is null) return false;
            var type = model.GetTypeInfo(other.Expression).Type;
            if (type is null or IErrorTypeSymbol || ContainsTypeParameter(parameter.Type)) continue;
            var target = parameter.IsParams && parameter.Type is IArrayTypeSymbol array && !model.Compilation.ClassifyConversion(type, parameter.Type).IsImplicit
                ? array.ElementType : parameter.Type;
            if (!model.Compilation.ClassifyConversion(type, target).IsImplicit) return false;
        }
        return true;
    }

    private string? FindReceiverType(string member, SeparatedSyntaxList<ArgumentSyntax>? arguments, string? next, string variable, string? hintType)
    {
        if (!_receiversByMember.TryGetValue(member, out var receivers)) return null;
        // The entry's type is either the receiver or the static class declaring the extension.
        // Then: a method whose parameters fit the call (count and argument names), a member
        // whose type has the next member in the chain (x.Services.AddAGUI()), a type named
        // like the variable (chatClient -> IChatClient), MAF first.
        var best = receivers
            .OrderBy(r => r.Receiver.Name == hintType || r.Declarer.Name == hintType ? 0 : 1)
            .ThenBy(r => arguments is null || HasCompatibleMethod(r, member, arguments.Value) ? 0 : 1)
            .ThenBy(r => next is null || MemberTypeHas(r.Declarer, member, next) ? 0 : 1)
            .ThenBy(r => variable.Length > 2 && r.Receiver.Name.Contains(variable, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => r.Receiver is INamedTypeSymbol named ? Rank(named) : 3)
            .ThenBy(r => r.Receiver.ToDisplayString(), StringComparer.Ordinal)
            .First();
        return Display(best.Receiver);
    }

    /// <summary>Whether the type of <paramref name="declarer"/>.<paramref name="member"/> has (or is extended with) <paramref name="next"/>.</summary>
    private bool MemberTypeHas(INamedTypeSymbol declarer, string member, string next)
    {
        var type = declarer.GetMembers(member).Select(m => m switch
        {
            IPropertySymbol p => p.Type,
            IFieldSymbol f => f.Type,
            IMethodSymbol method => method.ReturnType,
            _ => null,
        }).FirstOrDefault(t => t is not null);
        if (type is null || !_receiversByMember.TryGetValue(next, out var owners)) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null; t = t.BaseType) names.Add(t.OriginalDefinition.ToDisplayString());
        foreach (var i in type.AllInterfaces) names.Add(i.OriginalDefinition.ToDisplayString());
        return owners.Any(o => names.Contains(o.Receiver.OriginalDefinition.ToDisplayString()));
    }

    /// <summary>Namespace of an extension method <paramref name="member"/> that applies to <paramref name="receiver"/>.</summary>
    private string? FindExtensionNamespace(string member, ITypeSymbol receiver)
    {
        if (!_receiversByMember.TryGetValue(member, out var candidates)) return null;
        // Symbols come from different compilations, so compare by display name.
        var receiverNames = new HashSet<string>(StringComparer.Ordinal);
        for (var t = receiver; t is not null; t = t.BaseType) receiverNames.Add(t.OriginalDefinition.ToDisplayString());
        foreach (var i in receiver.AllInterfaces) receiverNames.Add(i.OriginalDefinition.ToDisplayString());
        receiverNames.Add("object");
        return candidates
            .Where(c => c.Declarer.IsStatic && receiverNames.Contains(c.Receiver.OriginalDefinition.ToDisplayString()))
            .OrderBy(c => Rank(c.Declarer))
            .Select(c => c.Declarer.ContainingNamespace.ToDisplayString())
            .FirstOrDefault();
    }

    private static bool HasCompatibleMethod((ITypeSymbol Receiver, INamedTypeSymbol Declarer) candidate, string member, SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        // An extension's first parameter is the receiver, not an argument.
        var isExtension = !SymbolEqualityComparer.Default.Equals(candidate.Receiver, candidate.Declarer);
        var names = arguments.Where(a => a.NameColon is not null).Select(a => a.NameColon!.Name.Identifier.ValueText).ToList();
        return candidate.Declarer.GetMembers(member).OfType<IMethodSymbol>().Any(m =>
        {
            var parameters = isExtension && m.IsExtensionMethod ? m.Parameters.Skip(1).ToList() : m.Parameters.ToList();
            return (parameters.Count >= arguments.Count || parameters.LastOrDefault()?.IsParams == true)
                && parameters.Count(p => !p.IsOptional && !p.IsParams) <= arguments.Count
                && names.All(n => parameters.Any(p => p.Name == n));
        });
    }

    // MAF first, then Microsoft.Extensions.AI, then everything else.
    private static int Rank(INamedTypeSymbol type)
    {
        var assembly = type.ContainingAssembly?.Name ?? "";
        return assembly.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal) ? 0
            : assembly.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal) ? 1
            : 2;
    }

    private static string Display(ITypeSymbol type)
    {
        // Open generic arguments (an unbound T in a parameter type) become object.
        if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Any(a => a is ITypeParameterSymbol))
        {
            var objectType = named.ContainingAssembly.GetTypeByMetadataName("System.Object");
            if (objectType is not null)
                type = named.ConstructedFrom.Construct(named.TypeArguments.Select(a => a is ITypeParameterSymbol ? objectType : a).ToArray());
        }
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    /// <summary>Whether the snippet states its own using directives.</summary>
    public static bool DeclaresUsings(string? snippet) => SplitUsings((snippet ?? "").Replace("\r\n", "\n")).Usings.Count > 0;

    private static (List<string> Usings, string Body) SplitUsings(string snippet)
    {
        var usings = new List<string>();
        var body = new StringBuilder();
        foreach (var line in snippet.Split('\n'))
        {
            if (UsingDirective.IsMatch(line)) usings.Add(line.Trim());
            else body.Append(line).Append('\n');
        }
        return (usings, body.ToString());
    }

    private static string? ShortTypeName(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var name = type.Trim().Split('<')[0].Split('`')[0];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    // Nested public types included (MAF's AgentMode is nested in a provider type).
    private static IEnumerable<INamedTypeSymbol> PublicTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            foreach (var nested in PublicTypes(type))
                yield return nested;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in PublicTypes(child))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> PublicTypes(INamedTypeSymbol type)
    {
        if (type.DeclaredAccessibility != Accessibility.Public) yield break;
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var inner in PublicTypes(nested))
                yield return inner;
    }

    private static void Add<T>(Dictionary<string, List<T>> index, string key, T value)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(value);
    }

    // The same assembly can arrive from a package and from a framework pack: keep the highest version.
    private static IEnumerable<string> Deduplicate(IEnumerable<string> paths)
    {
        var byName = new Dictionary<string, (Version Version, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(File.Exists))
        {
            Version version;
            try { version = System.Reflection.AssemblyName.GetAssemblyName(path).Version ?? new Version(0, 0); }
            catch (BadImageFormatException) { continue; }
            var name = Path.GetFileNameWithoutExtension(path);
            if (!byName.TryGetValue(name, out var existing) || version > existing.Version) byName[name] = (version, path);
        }
        return byName.Values.Select(v => v.Path);
    }
}
