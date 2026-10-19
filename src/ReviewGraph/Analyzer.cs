using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace ReviewGraph;

internal enum UnitKind { Member, Type, TopLevel, File }

/// <summary>A reviewable unit of code: a member, a type, a file's top-level statements, or a non-C# file.</summary>
internal sealed record Unit(string Key, UnitKind Kind, ISymbol? Symbol, SyntaxNode Decl, TextSpan Span);

internal sealed class GNode
{
    public required string Key { get; init; }
    public required UnitKind Kind { get; init; }
    public required string RelPath { get; init; }
    public ISymbol? Symbol { get; init; }
    public Document? Doc { get; init; }
    public SyntaxNode? Decl { get; init; }
    public TextSpan Span { get; init; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int HeaderStartLine { get; set; }
    public int HeaderEndLine { get; set; }
    public string Display { get; set; } = "";
    public string MemberKind { get; set; } = "";
    public string? Entry { get; set; }
    public bool IsTest { get; set; }
    public FileDiff? Diff { get; set; }

    public bool Changed { get; set; }
    public bool IsNew { get; set; }
    public bool SignatureChanged { get; set; }
    public SortedSet<int> AddedLines { get; } = new();
    public SortedDictionary<int, List<DeletedLine>> Deleted { get; } = new();
    public SortedSet<int> Anchors { get; } = new();
    public List<CheckOut> Checks { get; } = new();

    public string Id { get; set; } = "";
    public int Layer { get; set; }
    public int Order { get; set; }

    public int Adds => AddedLines.Count;
    public int Dels => Deleted.Values.Sum(d => d.Count);
    public ITypeSymbol? ContainingType => Symbol is INamedTypeSymbol t ? t : Symbol?.ContainingType;
}

internal sealed class GEdge
{
    public required GNode From { get; init; }
    public required int FromLine { get; init; }
    public required GNode To { get; init; }
    public required string Label { get; init; }
    public SyntaxNode? Site { get; init; }
}

internal sealed record RefSite(Document Doc, int Position, int Line, string RelPath);

public sealed partial class Analyzer
{
    private readonly Solution _sln;
    private readonly Options _o;
    private readonly List<string> _warnings;
    private readonly bool _sourceOnly;

    private readonly Dictionary<string, GNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<GEdge> _edges = new();
    private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Document> _docs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileDiff> _diffs = new(StringComparer.Ordinal);
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();
    private readonly Dictionary<string, List<RefSite>> _refCache = new(StringComparer.Ordinal);
    private DiRegistry? _di;

    private const int MaxContextCallers = 3;
    private const int MaxTestCallers = 1;
    private const int EntrySearchDepth = 3;
    private const int TestSearchDepth = 3;

    public Analyzer(Solution sln, Options o, List<FileDiff> diffs, List<string> warnings, bool sourceOnly)
    {
        _sln = sln; _o = o; _warnings = warnings; _sourceOnly = sourceOnly;
        foreach (var d in diffs) _diffs[d.Path] = d;
        foreach (var project in sln.Projects.Where(p => p.Language == LanguageNames.CSharp).OrderBy(p => p.Name, StringComparer.Ordinal))
            foreach (var doc in project.Documents.Where(d => d.FilePath is not null).OrderBy(d => d.FilePath, StringComparer.Ordinal))
            {
                var rel = Rel(doc.FilePath);
                if (rel is not null) _docs.TryAdd(rel, doc);
            }
    }

    // =====================================================================================
    //  Pipeline
    // =====================================================================================

    public async Task<(List<NodeOut>, List<EdgeOut>, List<StepOut>, List<string>)> RunAsync()
    {
        // 1. Which units did the diff touch?
        foreach (var diff in _diffs.Values.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            if (diff.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !diff.IsDeleted && _docs.TryGetValue(diff.Path, out var doc))
                await MapCsDiffAsync(diff, doc);
            else
                AddFileNode(diff);
        }
        DropRedundantTypeNodes();
        foreach (var n in Changed()) Classify(n);

        // 2. What does each changed unit reach? (calls, object creation, DI registration, data it touches)
        foreach (var n in Changed().ToList()) await OutgoingAsync(n);

        // 3. Interfaces and overrides: connect the abstraction a caller sees to the code that runs.
        await LinkImplementationsAsync();

        // 4. Who calls the changed code? Add the closest callers as context.
        await IncomingAsync();

        // 5. Walk unchanged callers back to an entry point (HTTP route, Main, handler, test).
        await ExtendToEntriesAsync();

        // 6. Rule-based review checks.
        foreach (var n in Changed().ToList()) await ChecksAsync(n);

        // 7. Reading order, layout and story.
        AssignIds();
        var (preorder, forward) = Order();
        Layout(preorder, forward);
        var steps = BuildSteps(preorder);
        var overview = Overview(steps);
        return (EmitNodes(), EmitEdges(), steps, overview);
    }

    private IEnumerable<GNode> Changed() => _nodes.Values.Where(n => n.Changed).OrderBy(n => n.RelPath, StringComparer.Ordinal).ThenBy(n => n.StartLine);
    private IEnumerable<GNode> AllNodes() => _nodes.Values.OrderBy(n => n.RelPath, StringComparer.Ordinal).ThenBy(n => n.StartLine).ThenBy(n => n.Key, StringComparer.Ordinal);

    // =====================================================================================
    //  1. Diff → units
    // =====================================================================================

    private async Task MapCsDiffAsync(FileDiff diff, Document doc)
    {
        var text = await doc.GetTextAsync();
        var root = await doc.GetSyntaxRootAsync() ?? throw new InvalidOperationException("no syntax root");
        var model = await ModelAsync(doc);

        foreach (var line in diff.AddedLines)
        {
            var pos = FirstCharPos(text, line);
            if (pos is null) continue; // blank line: shown inside its unit, never decides one
            var unit = UnitsAround(root.FindToken(pos.Value), model).FirstOrDefault();
            if (unit is null) continue; // using directives, namespace lines
            var n = GetOrAddNode(unit, doc, text);
            n.Changed = true;
            n.AddedLines.Add(line);
        }

        foreach (var (at, lines) in diff.DeletedBefore)
        {
            // Deleted lines sit in a gap before head line `at`. They belong to the innermost unit
            // that strictly surrounds the gap, or to a unit that was rewritten starting right there.
            var x = at <= text.Lines.Count ? text.Lines[at - 1].Start : text.Length;
            var token = root.FindToken(Math.Min(x, Math.Max(0, text.Length - 1)));
            Unit? owner = null;
            foreach (var u in UnitsAround(token, model))
            {
                var startLine = LineOf(text, u.Span.Start);
                if ((u.Span.Start < x && x <= u.Span.End) || (startLine == at && diff.AddedLines.Contains(at)))
                {
                    owner = u;
                    break;
                }
            }
            if (owner is null) continue;
            var n = GetOrAddNode(owner, doc, text);
            n.Changed = true;
            n.Deleted[at] = lines;
        }
    }

    /// <summary>Units containing a token, innermost first.</summary>
    private static IEnumerable<Unit> UnitsAround(SyntaxToken token, SemanticModel model)
    {
        for (var n = token.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or EventDeclarationSyntax:
                {
                    var s = model.GetDeclaredSymbol(n);
                    if (s is not null) yield return new Unit(KeyOf(s), UnitKind.Member, s, n, n.Span);
                    break;
                }
                case BaseFieldDeclarationSyntax f when f.Declaration.Variables.Count > 0:
                {
                    var s = model.GetDeclaredSymbol(f.Declaration.Variables[0]);
                    if (s is not null) yield return new Unit(KeyOf(s), UnitKind.Member, s, n, n.Span);
                    break;
                }
                case DelegateDeclarationSyntax or BaseTypeDeclarationSyntax:
                {
                    var s = model.GetDeclaredSymbol(n);
                    if (s is not null) yield return new Unit(KeyOf(s), UnitKind.Type, s, n, n.Span);
                    break;
                }
                case GlobalStatementSyntax:
                {
                    var cu = (CompilationUnitSyntax)n.Parent!;
                    var globals = cu.Members.OfType<GlobalStatementSyntax>().ToList();
                    var span = TextSpan.FromBounds(globals[0].SpanStart, globals[^1].Span.End);
                    yield return new Unit("toplevel:" + cu.SyntaxTree.FilePath, UnitKind.TopLevel, null, cu, span);
                    yield break;
                }
            }
        }
    }

    private GNode GetOrAddNode(Unit u, Document doc, SourceText text)
    {
        if (_nodes.TryGetValue(u.Key, out var existing)) return existing;
        var rel = Rel(doc.FilePath)!;
        var n = new GNode { Key = u.Key, Kind = u.Kind, RelPath = rel, Symbol = u.Symbol, Doc = doc, Decl = u.Decl, Span = u.Span };
        n.StartLine = LineOf(text, u.Span.Start);
        n.EndLine = LineOf(text, Math.Max(u.Span.Start, u.Span.End - 1));
        var (hs, he) = HeaderSpan(u);
        n.HeaderStartLine = LineOf(text, hs);
        n.HeaderEndLine = Math.Max(n.HeaderStartLine, LineOf(text, Math.Max(hs, he - 1)));
        n.Display = DisplayOf(u, rel);
        n.MemberKind = MemberKindOf(u);
        n.Entry = EntryOf(u);
        n.IsTest = n.Entry == "Test" || IsTestProject(doc.Project);
        n.Diff = _diffs.GetValueOrDefault(rel);
        _nodes[u.Key] = n;
        return n;
    }

    private void AddFileNode(FileDiff diff)
    {
        var n = new GNode { Key = "file:" + diff.Path, Kind = UnitKind.File, RelPath = diff.Path, Diff = diff, Changed = true };
        n.Display = System.IO.Path.GetFileName(diff.Path);
        n.MemberKind = diff.IsBinary ? "binary file" : diff.IsDeleted ? "deleted file" : "file";
        n.StartLine = 1;
        foreach (var l in diff.AddedLines) n.AddedLines.Add(l);
        foreach (var (k, v) in diff.DeletedBefore) n.Deleted[k] = v;
        n.IsNew = diff.IsNew;
        _nodes[n.Key] = n;
    }

    /// <summary>
    /// A brand-new type whose members are shown as their own cards does not need a card of its
    /// own for the class header and braces. Keep type cards only when the type itself changed
    /// (base list, attributes, record parameters, enum values) or it has no member cards.
    /// </summary>
    private void DropRedundantTypeNodes()
    {
        foreach (var t in _nodes.Values.Where(n => n.Kind == UnitKind.Type && n.Changed).ToList())
        {
            var text = t.Doc!.GetTextAsync().Result;
            var memberCards = _nodes.Values.Where(m => m.Kind == UnitKind.Member && m.Changed &&
                SymbolEqualityComparer.Default.Equals(m.Symbol?.ContainingType, t.Symbol)).ToList();
            if (memberCards.Count == 0) continue;
            var fileAdded = t.Diff?.AddedLines ?? t.AddedLines;
            var isAllNew = Enumerable.Range(t.StartLine, t.EndLine - t.StartLine + 1)
                .All(l => IsBlank(text, l) || fileAdded.Contains(l));
            var onlyStructural = t.AddedLines.All(l => IsBraceOnly(text, l)) && t.Deleted.Count == 0;
            if (isAllNew || onlyStructural) _nodes.Remove(t.Key);
        }
    }

    private void Classify(GNode n)
    {
        if (n.Kind == UnitKind.File) return;
        var text = n.Doc!.GetTextAsync().Result;
        var fileAdded = n.Diff?.AddedLines ?? n.AddedLines;
        n.IsNew = n.Deleted.Count == 0 && Enumerable.Range(n.StartLine, n.EndLine - n.StartLine + 1)
            .All(l => IsBlank(text, l) || fileAdded.Contains(l));
        if (!n.IsNew && n.Kind == UnitKind.Member)
            n.SignatureChanged = Enumerable.Range(n.HeaderStartLine, n.HeaderEndLine - n.HeaderStartLine + 1)
                .Any(l => n.AddedLines.Contains(l) || n.Deleted.ContainsKey(l));
    }

    // =====================================================================================
    //  2. Outgoing edges
    // =====================================================================================

    private async Task OutgoingAsync(GNode n)
    {
        if (n.Doc is null || n.Decl is null) return;
        var model = await ModelAsync(n.Doc);
        var text = await n.Doc.GetTextAsync();
        IEnumerable<SyntaxNode> scope = n.Kind == UnitKind.TopLevel
            ? ((CompilationUnitSyntax)n.Decl).Members.OfType<GlobalStatementSyntax>()
            : new[] { n.Decl };

        foreach (var x in scope.SelectMany(s => s.DescendantNodes(d => n.Kind != UnitKind.Type || d == n.Decl ||
                     d is not (BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax or EventDeclarationSyntax or BaseTypeDeclarationSyntax))))
        {
            if (n.Kind == UnitKind.Type && x is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax or EventDeclarationSyntax or BaseTypeDeclarationSyntax) continue;
            switch (x)
            {
                case InvocationExpressionSyntax inv when !IsNameof(inv):
                {
                    var line = LineOf(text, NamePosition(inv));
                    var onChanged = n.AddedLines.Contains(line);
                    if (Resolve(model.GetSymbolInfo(inv)) is IMethodSymbol m)
                    {
                        await LinkAsync(n, line, m, MinimalApiRoute(inv, model) ?? "calls", onChanged, inv);
                        if (onChanged && DiRegistry.IsRegistrationMethod(m) && m.TypeArguments.Length > 0 &&
                            m.TypeArguments[^1] is INamedTypeSymbol impl && IsSource(impl))
                        {
                            var target = await NodeForTypeAsync(impl);
                            if (target is not null && target != n) AddEdge(n, line, target, "registers", inv);
                        }
                    }
                    // app.MapGet("/x", Handler) – a method group is wired, not called.
                    if (onChanged && MinimalApiVerb(inv) is { } verb)
                        foreach (var arg in inv.ArgumentList.Arguments.Skip(1))
                            if (arg.Expression is not LambdaExpressionSyntax &&
                                model.GetSymbolInfo(arg.Expression).Symbol is IMethodSymbol handler)
                                await LinkAsync(n, line, handler, $"{verb} {RouteLiteral(inv)}", true, inv);
                    break;
                }
                case BaseObjectCreationExpressionSyntax oc:
                {
                    var line = LineOf(text, oc.SpanStart);
                    var onChanged = n.AddedLines.Contains(line);
                    if (model.GetSymbolInfo(oc).Symbol is IMethodSymbol ctor && IsSource(ctor))
                    {
                        if (ctor.IsImplicitlyDeclared)
                        {
                            var t = await NodeForTypeAsync(ctor.ContainingType, createContext: false);
                            if (t is not null && t.Changed && onChanged && t != n) AddEdge(n, line, t, "creates", oc);
                        }
                        else await LinkAsync(n, line, ctor, "creates", onChanged, oc);
                    }
                    break;
                }
                case IdentifierNameSyntax id when n.AddedLines.Contains(LineOf(text, id.SpanStart)):
                {
                    // Data the new code touches: only linked when that data is itself part of the change.
                    var s = model.GetSymbolInfo(id).Symbol;
                    if (s is IPropertySymbol or IFieldSymbol or INamedTypeSymbol && IsSource(s))
                    {
                        var t = s is INamedTypeSymbol nt ? _nodes.GetValueOrDefault(KeyOf(nt)) : await NodeForAsync(Normalize(s), create: false);
                        if (t is not null && t.Changed && t != n)
                            AddEdge(n, LineOf(text, id.SpanStart), t, s is INamedTypeSymbol ? "uses" : IsWrite(id) ? "sets" : "reads", id);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Edges from changed lines always count. Edges from unchanged lines of a changed member only
    /// count when they point at other changed code, which keeps the graph about the change.
    /// </summary>
    private async Task LinkAsync(GNode from, int line, IMethodSymbol target, string label, bool onChangedLine, SyntaxNode site)
    {
        if (target.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction || !IsSource(target)) return;
        var sym = Normalize(target);
        var t = await NodeForAsync(sym, create: false);
        if (t is null)
        {
            if (!onChangedLine) return;
            t = await NodeForAsync(sym, create: true);
        }
        if (t is null || t == from) return;
        if (!onChangedLine && !t.Changed) return;
        AddEdge(from, line, t, label, site);
    }

    private void AddEdge(GNode from, int line, GNode to, string label, SyntaxNode? site = null)
    {
        if (from == to) return;
        if (!_edgeKeys.Add(from.Key + "→" + to.Key)) return;
        _edges.Add(new GEdge { From = from, FromLine = line, To = to, Label = label, Site = site });
        from.Anchors.Add(line);
    }

    // =====================================================================================
    //  3. Interfaces and overrides
    // =====================================================================================

    private async Task LinkImplementationsAsync()
    {
        // a) Changed implementations point back to the abstraction callers actually use.
        foreach (var n in Changed().Where(n => n.Kind == UnitKind.Member).ToList())
        {
            foreach (var abstraction in Abstractions(n.Symbol!))
            {
                var a = await NodeForAsync(abstraction, create: true);
                if (a is not null && a != n)
                    AddEdge(a, a.HeaderStartLine, n, abstraction.ContainingType.TypeKind == TypeKind.Interface ? "implemented by" : "overridden by");
            }
        }

        // b) Calls that land on an interface or virtual member: show where they go.
        foreach (var n in AllNodes().Where(n => n.Symbol is IMethodSymbol or IPropertySymbol && IsAbstraction(n.Symbol)).ToList())
        {
            var impls = (await ImplementationsAsync(n.Symbol!)).ToList();
            var targets = new List<GNode>();
            foreach (var impl in impls)
            {
                var t = await NodeForAsync(impl, create: false);
                if (t is not null && t.Changed) targets.Add(t);
            }
            if (targets.Count == 0 && impls.Count == 1 && _edges.Any(e => e.To == n && e.Label != "implemented by"))
            {
                var t = await NodeForAsync(impls[0], create: true);
                if (t is not null) targets.Add(t);
            }
            foreach (var t in targets)
                AddEdge(n, n.HeaderStartLine, t, n.Symbol!.ContainingType.TypeKind == TypeKind.Interface ? "implemented by" : "overridden by");
        }
    }

    private static IEnumerable<ISymbol> Abstractions(ISymbol s)
    {
        var results = new List<ISymbol>();
        var type = s.ContainingType;
        if (type is null) return results;
        foreach (var iface in type.AllInterfaces.OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal))
            foreach (var member in iface.GetMembers())
            {
                var impl = type.FindImplementationForInterfaceMember(member);
                if (impl is not null && SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, s.OriginalDefinition) && IsSource(member))
                    results.Add(member.OriginalDefinition);
            }
        ISymbol? baseMember = s switch
        {
            IMethodSymbol { IsOverride: true } m => m.OverriddenMethod,
            IPropertySymbol { IsOverride: true } p => p.OverriddenProperty,
            _ => null,
        };
        if (baseMember is not null && IsSource(baseMember)) results.Add(baseMember.OriginalDefinition);
        return results;
    }

    private static bool IsAbstraction(ISymbol s) =>
        s.ContainingType?.TypeKind == TypeKind.Interface || s.IsAbstract || s.IsVirtual;

    private async Task<IEnumerable<ISymbol>> ImplementationsAsync(ISymbol s)
    {
        IEnumerable<ISymbol> found = s.ContainingType?.TypeKind == TypeKind.Interface
            ? await SymbolFinder.FindImplementationsAsync(s, _sln)
            : await SymbolFinder.FindOverridesAsync(s, _sln);
        return found.Where(IsSource).Select(x => x.OriginalDefinition)
            .Distinct<ISymbol>(SymbolEqualityComparer.Default)
            .OrderBy(x => SortKey(x), StringComparer.Ordinal).ToList();
    }

    // =====================================================================================
    //  4. Incoming edges (callers)
    // =====================================================================================

    private async Task IncomingAsync()
    {
        var targets = Changed().Where(n => n.Symbol is IMethodSymbol).ToList();
        // Abstractions that front a changed implementation: callers usually go through these.
        targets.AddRange(_edges.Where(e => e.Label is "implemented by" or "overridden by" && e.To.Changed).Select(e => e.From));

        foreach (var target in targets.Distinct().ToList())
        {
            var candidates = new List<(Unit Unit, Document Doc, int Line, string Label, string? Entry, bool Test)>();
            foreach (var site in await RefsAsync(target.Symbol!))
            {
                var (unit, label, node) = await UnitAtAsync(site);
                if (unit is null || unit.Key == target.Key) continue;
                if (_nodes.TryGetValue(unit.Key, out var existing))
                {
                    AddEdge(existing, site.Line, target, label, node);
                    continue;
                }
                candidates.Add((unit, site.Doc, site.Line, label, EntryOf(unit), EntryOf(unit) == "Test" || IsTestProject(site.Doc.Project)));
            }

            var picked = candidates
                .GroupBy(c => c.Unit.Key).Select(g => g.First())
                .OrderBy(c => c.Entry is not null && !c.Test ? 0 : 1)
                .ThenBy(c => c.Test ? 1 : 0)
                .ThenBy(c => Rel(c.Doc.FilePath), StringComparer.Ordinal).ThenBy(c => c.Line)
                .ToList();
            foreach (var c in picked.Where(c => !c.Test).Take(MaxContextCallers)
                         .Concat(picked.Where(c => c.Test).Take(MaxTestCallers)))
            {
                var caller = GetOrAddNode(c.Unit, c.Doc, await c.Doc.GetTextAsync());
                AddEdge(caller, c.Line, target, c.Label);
            }
        }
    }

    // =====================================================================================
    //  5. Back to the entry point
    // =====================================================================================

    private async Task ExtendToEntriesAsync()
    {
        var starts = _nodes.Values.Where(n => !n.Changed && n.Entry is null && !n.IsTest && n.Symbol is IMethodSymbol &&
                                              _edges.Any(e => e.From == n) && !_edges.Any(e => e.To == n))
                                  .OrderBy(n => n.RelPath, StringComparer.Ordinal).ThenBy(n => n.StartLine).ToList();
        foreach (var start in starts)
        {
            var path = await PathToEntryAsync(start.Symbol!, EntrySearchDepth, new HashSet<string> { start.Key });
            if (path is null) continue;
            var callee = start;
            foreach (var (unit, doc, line, label) in path)
            {
                var caller = GetOrAddNode(unit, doc, await doc.GetTextAsync());
                AddEdge(caller, line, callee, label);
                callee = caller;
            }
        }
    }

    private async Task<List<(Unit, Document, int, string)>?> PathToEntryAsync(ISymbol s, int depth, HashSet<string> seen)
    {
        if (depth == 0) return null;
        var callers = new List<(Unit Unit, Document Doc, int Line, string Label)>();
        foreach (var target in new[] { s }.Concat(Abstractions(s)))
            foreach (var site in await RefsAsync(target))
            {
                var (unit, label, _) = await UnitAtAsync(site);
                if (unit is null || seen.Contains(unit.Key)) continue;
                if (EntryOf(unit) == "Test" || IsTestProject(site.Doc.Project)) continue;
                var via = SymbolEqualityComparer.Default.Equals(target, s) ? label : $"{label} via {target.ContainingType.Name}";
                callers.Add((unit, site.Doc, site.Line, via));
            }
        callers = callers.GroupBy(c => c.Unit.Key).Select(g => g.First())
            .OrderBy(c => EntryOf(c.Unit) is null ? 1 : 0)
            .ThenBy(c => Rel(c.Doc.FilePath), StringComparer.Ordinal).ThenBy(c => c.Line).ToList();

        foreach (var c in callers)
            if (EntryOf(c.Unit) is not null) return new() { c };
        foreach (var c in callers.Where(c => c.Unit.Symbol is not null).Take(3))
        {
            var rest = await PathToEntryAsync(c.Unit.Symbol!, depth - 1, new HashSet<string>(seen) { c.Unit.Key });
            if (rest is not null) { rest.Insert(0, c); return rest; }
        }
        return null;
    }

    // =====================================================================================
    //  6. Checks
    // =====================================================================================

    private async Task ChecksAsync(GNode n)
    {
        if (n.Kind == UnitKind.File || n.Doc is null) return;
        var model = await ModelAsync(n.Doc);
        var text = await n.Doc.GetTextAsync();
        var sym = n.Symbol;

        // Compiler diagnostics on lines this PR added.
        foreach (var d in model.GetDiagnostics(n.Span)
                     .Where(d => d.Severity >= DiagnosticSeverity.Warning && !d.IsSuppressed)
                     .Where(d => d.Severity == DiagnosticSeverity.Warning || !_sourceOnly)
                     .OrderBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id, StringComparer.Ordinal))
        {
            var line = LineOf(text, d.Location.SourceSpan.Start);
            if (!n.AddedLines.Contains(line)) continue;
            n.Checks.Add(new(false, $"Compiler {d.Severity.ToString().ToLowerInvariant()} {d.Id} on line {line}: {d.GetMessage(CultureInfo.InvariantCulture)}"));
            n.Anchors.Add(line);
        }

        if (sym is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && (n.SignatureChanged || n.IsNew))
            CheckDependencyInjection(n, ctor);
        else if (n.SignatureChanged && sym is not null)
            await CheckSignatureAsync(n, sym);

        if (n.Kind == UnitKind.Member && n.Decl is not null)
            CheckThrows(n, model, text);

        if (sym is IMethodSymbol or IPropertySymbol && n.Kind == UnitKind.Member)
            await CheckOtherImplementationsAsync(n, sym);

        if (n.IsNew && sym is IMethodSymbol { MethodKind: MethodKind.Ordinary } nm && n.Entry is null && !n.IsTest &&
            nm.ContainingType.TypeKind != TypeKind.Interface && !nm.IsOverride && !Abstractions(nm).Any())
        {
            if ((await RefsAsync(nm)).Count == 0)
                n.Checks.Add(new(false, "Nothing in the solution calls this yet."));
        }

        if (sym is IMethodSymbol { MethodKind: MethodKind.Ordinary } tm && !n.IsTest && tm.ContainingType.TypeKind != TypeKind.Interface)
            await CheckTestsAsync(n, tm);
    }

    private async Task CheckSignatureAsync(GNode n, ISymbol sym)
    {
        var sites = await RefsAsync(sym);
        if (sites.Count == 0) return;
        var untouched = sites.Where(s => !(_diffs.TryGetValue(s.RelPath, out var d) && d.AddedLines.Contains(s.Line))).ToList();
        if (untouched.Count == 0)
        {
            n.Checks.Add(new(true, $"Signature changed, and all {Plural(sites.Count, "call site")} were updated in this PR."));
            return;
        }
        var names = new List<string>();
        foreach (var s in untouched.Take(3))
        {
            var (unit, _, _) = await UnitAtAsync(s);
            names.Add($"`{(unit is null ? System.IO.Path.GetFileName(s.RelPath) : DisplayOf(unit, s.RelPath))}` ({System.IO.Path.GetFileName(s.RelPath)}:{s.Line})");
        }
        var more = untouched.Count > 3 ? $" and {untouched.Count - 3} more" : "";
        n.Checks.Add(new(false, $"Signature changed. {Plural(untouched.Count, "call site")} still use{(untouched.Count == 1 ? "s" : "")} it unchanged: {string.Join(", ", names)}{more}. Check they still behave as intended."));
    }

    private void CheckDependencyInjection(GNode n, IMethodSymbol ctor)
    {
        _di ??= DiRegistry.Build(_sln, this);
        if (!_di.IsRegisteredImplementation(ctor.ContainingType)) return;
        var missing = ctor.Parameters
            .Where(p => !p.HasExplicitDefaultValue && !_di.CanResolve(p.Type))
            .Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).ToList();
        if (missing.Count == 0)
            n.Checks.Add(new(true, ctor.Parameters.Length switch
            {
                1 => "The injected service is registered for dependency injection.",
                2 => "Both injected services are registered for dependency injection.",
                var c => $"All {c} injected services are registered for dependency injection.",
            }));
        else
            foreach (var m in missing)
                n.Checks.Add(new(false, $"`{m}` is injected here but never registered for dependency injection, so resolving `{ctor.ContainingType.Name}` will fail at runtime."));
    }

    private void CheckThrows(GNode n, SemanticModel model, SourceText text)
    {
        var thrown = n.Decl!.DescendantNodes()
            .Select(x => x switch { ThrowStatementSyntax t => t.Expression, ThrowExpressionSyntax t => t.Expression, _ => null })
            .Where(e => e is not null && n.AddedLines.Contains(LineOf(text, e.SpanStart)))
            .Select(e => model.GetTypeInfo(e!).Type?.Name ?? (e as ObjectCreationExpressionSyntax)?.Type.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (thrown.Count == 0) return;

        var callerEdges = CallerEdges(n).ToList();
        var guarded = callerEdges.Where(e => e.Site?.Ancestors().OfType<TryStatementSyntax>().Any(t => t.Catches.Count > 0) == true).ToList();
        var list = string.Join(", ", thrown.Select(t => $"`{t}`"));
        if (callerEdges.Count > 0 && guarded.Count == callerEdges.Count)
            n.Checks.Add(new(true, $"Can now throw {list}. Every caller in this graph catches exceptions around the call."));
        else if (callerEdges.Count > 0)
            n.Checks.Add(new(false, $"Can now throw {list}. {string.Join(", ", callerEdges.Where(e => !guarded.Contains(e)).Select(e => $"`{e.From.Display}`").Distinct())} {(callerEdges.Count - guarded.Count == 1 ? "doesn't" : "don't")} catch {(thrown.Count == 1 ? "it" : "them")}, so check where {(thrown.Count == 1 ? "it surfaces" : "they surface")}."));
        else
            n.Checks.Add(new(false, $"Can now throw {list}."));
    }

    private IEnumerable<GEdge> CallerEdges(GNode n)
    {
        var fronts = _edges.Where(e => e.To == n && e.Label is "implemented by" or "overridden by").Select(e => e.From).ToHashSet();
        return _edges.Where(e => (e.To == n || fronts.Contains(e.To)) && e.Label is not ("implemented by" or "overridden by"))
                     .OrderBy(e => e.From.RelPath, StringComparer.Ordinal).ThenBy(e => e.FromLine);
    }

    private async Task CheckOtherImplementationsAsync(GNode n, ISymbol sym)
    {
        foreach (var abstraction in Abstractions(sym))
        {
            var a = await NodeForAsync(abstraction, create: false);
            if (a is not null && a.IsNew) continue; // a new interface member: the compiler forces every implementation
            var others = new List<ISymbol>();
            foreach (var impl in await ImplementationsAsync(abstraction))
            {
                var t = await NodeForAsync(impl, create: false);
                if (t is null || !t.Changed) others.Add(impl);
            }
            if (others.Count > 0)
                n.Checks.Add(new(false, $"`{abstraction.ContainingType.Name}.{abstraction.Name}` has {Plural(others.Count, "other implementation")} this PR didn't change: {string.Join(", ", others.Take(3).Select(o => $"`{o.ContainingType.Name}`"))}. Check they still match."));
        }
    }

    private async Task CheckTestsAsync(GNode n, IMethodSymbol sym)
    {
        var tests = new SortedSet<string>(StringComparer.Ordinal);
        var changedTests = new SortedSet<string>(StringComparer.Ordinal);
        var frontier = new List<ISymbol> { sym };
        var seen = new HashSet<string>(StringComparer.Ordinal) { KeyOf(sym) };
        for (var depth = 0; depth < TestSearchDepth && frontier.Count > 0; depth++)
        {
            var next = new List<ISymbol>();
            foreach (var s in frontier)
                foreach (var target in new[] { s }.Concat(Abstractions(s)))
                    foreach (var site in await RefsAsync(target))
                    {
                        var (unit, _, _) = await UnitAtAsync(site);
                        if (unit?.Symbol is null || !seen.Add(unit.Key)) continue;
                        if (EntryOf(unit) == "Test")
                        {
                            var name = DisplayOf(unit, site.RelPath);
                            tests.Add(name);
                            if (_diffs.ContainsKey(site.RelPath)) changedTests.Add(name);
                        }
                        else next.Add(unit.Symbol);
                    }
            frontier = next;
        }
        if (changedTests.Count > 0)
            n.Checks.Add(new(true, $"Exercised by {Names(changedTests)}, changed in this PR."));
        else if (tests.Count > 0)
            n.Checks.Add(new(false, $"Existing tests reach this ({Names(tests)}), but none were added or changed for the new behavior."));
        else
            n.Checks.Add(new(false, $"No test reaches this within {TestSearchDepth} calls."));

        static string Names(SortedSet<string> s) =>
            string.Join(", ", s.Take(2).Select(x => $"`{x}`")) + (s.Count > 2 ? $" and {s.Count - 2} more" : "");
    }

    // =====================================================================================
    //  7. Ordering, layout, story
    // =====================================================================================

    private void AssignIds()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in AllNodes())
        {
            var slug = Regex.Replace(n.Display.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length == 0) slug = "node";
            var id = slug;
            for (var i = 2; !used.Add(id); i++) id = $"{slug}-{i}";
            n.Id = id;
        }
    }

    private List<GEdge> OutEdges(GNode n) => _edges.Where(e => e.From == n)
        .OrderBy(e => e.FromLine).ThenBy(e => e.To.RelPath, StringComparer.Ordinal).ThenBy(e => e.To.StartLine).ToList();

    /// <summary>
    /// Depth-first walk from entry points, following calls in source order: the order a reader
    /// would step through the code in a debugger. Entry points that reach the most changed code go first.
    /// </summary>
    private (List<GNode> Preorder, HashSet<GEdge> Forward) Order()
    {
        var reach = new Dictionary<GNode, int>();
        foreach (var n in _nodes.Values)
        {
            var seen = new HashSet<GNode>();
            var stack = new Stack<GNode>(); stack.Push(n);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!seen.Add(c)) continue;
                foreach (var e in _edges.Where(e => e.From == c)) stack.Push(e.To);
            }
            reach[n] = seen.Count(s => s.Changed);
        }
        var hasIncoming = _edges.Select(e => e.To).ToHashSet();
        var roots = AllNodes()
            .OrderBy(n => n.Entry is not null && !n.IsTest ? 0 : !hasIncoming.Contains(n) && !n.IsTest ? 1 : n.IsTest ? 3 : 2)
            .ThenByDescending(n => reach[n])
            .ThenBy(n => n.RelPath, StringComparer.Ordinal).ThenBy(n => n.StartLine)
            .ToList();

        var preorder = new List<GNode>();
        var visited = new HashSet<GNode>();
        var onStack = new HashSet<GNode>();
        var forward = new HashSet<GEdge>();
        void Visit(GNode n)
        {
            visited.Add(n); onStack.Add(n); preorder.Add(n);
            foreach (var e in OutEdges(n))
            {
                if (onStack.Contains(e.To)) continue; // back edge: a cycle, ignored for layout
                forward.Add(e);
                if (!visited.Contains(e.To)) Visit(e.To);
            }
            onStack.Remove(n);
        }
        foreach (var r in roots) if (!visited.Contains(r)) Visit(r);
        return (preorder, forward);
    }

    private void Layout(List<GNode> preorder, HashSet<GEdge> allForward)
    {
        // Columns follow control flow only. Data a change touches (records, properties, fields)
        // is placed one column after its first user instead of stretching the graph.
        var forward = allForward.Where(e => IsCall(e.Label) || e.Label is "implemented by" or "overridden by" or "registers").ToHashSet();
        var data = allForward.Except(forward).ToList();
        // Longest-path layering over the acyclic part of the graph.
        var indeg = preorder.ToDictionary(n => n, n => forward.Count(e => e.To == n));
        var queue = new List<GNode>(preorder.Where(n => indeg[n] == 0));
        var topo = new List<GNode>();
        while (queue.Count > 0)
        {
            var n = queue[0]; queue.RemoveAt(0); topo.Add(n);
            foreach (var e in forward.Where(e => e.From == n).OrderBy(e => preorder.IndexOf(e.To)))
                if (--indeg[e.To] == 0) queue.Add(e.To);
        }
        foreach (var n in topo)
            n.Layer = forward.Where(e => e.To == n).Select(e => e.From.Layer + 1).DefaultIfEmpty(0).Max();
        // Pull callers with nothing upstream next to what they call, so edges stay short.
        foreach (var n in Enumerable.Reverse(topo))
        {
            if (forward.Any(e => e.To == n)) continue;
            var outs = forward.Where(e => e.From == n).Select(e => e.To.Layer).ToList();
            if (outs.Count > 0) n.Layer = Math.Max(0, outs.Min() - 1);
        }
        var dataOnly = preorder.Where(n => !forward.Any(e => e.To == n || e.From == n)).ToHashSet();
        for (var pass = 0; pass < 3; pass++)
            foreach (var n in preorder.Where(dataOnly.Contains))
            {
                // Readers say more about where data matters than the constructor that assigns it.
                var into = data.Where(e => e.To == n).ToList();
                var users = into.Where(e => e.Label != "sets" && !dataOnly.Contains(e.From)).ToList();
                if (users.Count == 0) users = into.Where(e => !dataOnly.Contains(e.From)).ToList();
                if (users.Count == 0) users = into;
                if (users.Count > 0) { n.Layer = users.Min(e => e.From.Layer) + 1; continue; }
                // Nothing uses it (e.g. a constructor that only assigns fields): sit just before what it touches.
                var targets = data.Where(e => e.From == n).Select(e => e.To.Layer).ToList();
                if (targets.Count > 0) n.Layer = Math.Max(0, targets.Min() - 1);
            }
        foreach (var n in preorder) n.Order = preorder.IndexOf(n);
    }

    private List<StepOut> BuildSteps(List<GNode> preorder)
    {
        var changed = preorder.Where(n => n.Changed && n.Kind != UnitKind.File).ToList();
        var outCode = changed.ToDictionary(n => n, n => _edges.Count(e => e.From == n && e.Label is not ("implemented by" or "overridden by" or "reads" or "sets" or "uses")));
        bool Trivial(GNode n) => n.Kind is UnitKind.Member or UnitKind.Type && n.Entry is null && outCode[n] == 0 &&
                                 (n.Adds + n.Dels <= 4 || n.Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor });

        // Small supporting changes (a new property, an interface signature, an injected field)
        // ride along with the step that uses them instead of getting a step of their own.
        var host = new Dictionary<GNode, GNode>();
        foreach (var n in changed.Where(Trivial))
        {
            var ownType = n.Symbol is INamedTypeSymbol ? n.Symbol : n.ContainingType;
            GNode? h =
                // an interface member travels with the implementation that changed
                _edges.Where(e => e.From == n && e.Label is "implemented by" or "overridden by" && e.To.Changed).Select(e => e.To).FirstOrDefault()
                // constructors, fields, properties travel with the main change in their own type
                ?? changed.Where(m => m != n && !Trivial(m) && SymbolEqualityComparer.Default.Equals(m.ContainingType, ownType))
                          .OrderBy(preorder.IndexOf).FirstOrDefault()
                // otherwise with the first change that uses them
                ?? _edges.Where(e => e.To == n && e.From.Changed && e.From.Kind != UnitKind.File && !Trivial(e.From) && e.Label != "registers")
                         .Select(e => e.From).OrderBy(preorder.IndexOf).FirstOrDefault()
                ?? _edges.Where(e => e.To == n && e.From.Changed && e.From.Kind != UnitKind.File).Select(e => e.From).OrderBy(preorder.IndexOf).FirstOrDefault()
                ?? changed.Where(m => m != n && SymbolEqualityComparer.Default.Equals(m.ContainingType, ownType))
                          .OrderBy(preorder.IndexOf).FirstOrDefault();
            if (h is not null) host[n] = h;
        }
        GNode Root(GNode n)
        {
            var seen = new HashSet<GNode>();
            while (host.TryGetValue(n, out var h) && seen.Add(n)) n = h;
            return n;
        }

        var primaries = changed.Where(n => Root(n) == n).ToList();
        var steps = new List<StepOut>();
        var firstWithEntry = true;
        foreach (var p in primaries)
        {
            var members = changed.Where(n => Root(n) == p).OrderBy(n => n == p ? 0 : 1).ThenBy(preorder.IndexOf).ToList();
            var set = new List<GNode>(members);
            foreach (var e in _edges.Where(e => e.From == p || e.To == p).OrderBy(e => preorder.IndexOf(e.From == p ? e.To : e.From)))
            {
                var other = e.From == p ? e.To : e.From;
                if (!set.Contains(other) && other.Kind != UnitKind.File) set.Add(other);
            }
            // Abstractions a rider implements (interface members) are part of how it is reached.
            foreach (var e in _edges.Where(e => members.Contains(e.To) && e.Label is "implemented by" or "overridden by"))
                if (!set.Contains(e.From)) set.Add(e.From);

            var path = PathFromEntry(p);
            if (path is not null && firstWithEntry)
            {
                foreach (var x in path) if (!set.Contains(x)) set.Add(x);
                firstWithEntry = false;
            }

            var body = new List<string> { Describe(p) };
            if (p.Entry is not null && p.Entry != "Test")
                body.Add($"This is where execution starts: `{p.Entry}`.");
            else if (path is not null && path.Count > 0)
            {
                var hops = path.Select(x => $"`{x.Display}`").ToList();
                if (hops.Count > 4) hops = hops.Take(1).Append("…").Concat(hops.TakeLast(2)).ToList();
                body.Add($"Reached from `{path[0].Entry}`: {string.Join(" → ", hops)} → here.");
            }

            var callers = _edges.Where(e => members.Contains(e.To) && !members.Contains(e.From) && IsCall(e.Label) && (path is null || !path.Contains(e.From)))
                .Select(e => e.From).Distinct().ToList();
            if (callers.Count > 0) body.Add($"Called from {List(callers.Select(c => c.Display))}.");
            var callees = _edges.Where(e => members.Contains(e.From) && !members.Contains(e.To) && IsCall(e.Label) && e.Label != "creates")
                .Select(e => e.To).Distinct().ToList();
            if (callees.Count > 0) body.Add($"Calls {List(callees.Select(c => c.Display))}.");
            var creates = _edges.Where(e => members.Contains(e.From) && !members.Contains(e.To) && e.Label == "creates")
                .Select(e => e.To.ContainingType?.Name ?? e.To.Display).Distinct().ToList();
            if (creates.Count > 0) body.Add($"Creates {List(creates)}.");
            var registers = _edges.Where(e => members.Contains(e.From) && e.Label == "registers").Select(e => e.To.ContainingType?.Name ?? e.To.Display).Distinct().ToList();
            if (registers.Count > 0) body.Add($"Registers {List(registers)} for dependency injection.");
            var data = _edges.Where(e => members.Contains(e.From) && !members.Contains(e.To) && e.Label is "reads" or "sets" or "uses" &&
                                         e.To.MemberKind != "field").Select(e => e.To.Display).Distinct().ToList();
            if (data.Count > 0) body.Add($"Uses {List(data)}.");
            var riders = members.Where(m => m != p).ToList();
            if (riders.Count > 0) body.Add($"Also in this step: {string.Join(", ", riders.Select(r => $"`{r.Display}` ({(r.IsNew ? "new" : "changed")} {r.MemberKind})"))}.");

            var checks = new List<CheckOut>(p.Checks);
            foreach (var r in riders)
                checks.AddRange(r.Checks.Select(c => c with { Text = $"`{r.Display}`: {c.Text}" }));

            steps.Add(new StepOut(
                $"{(p.IsNew ? "New" : "Changed")}: {p.Display}",
                p.Id, set.Select(x => x.Id).ToList(), body, checks));
        }

        var files = preorder.Where(n => n.Kind == UnitKind.File).ToList();
        if (files.Count > 0)
            steps.Add(new StepOut("Other changed files", files[0].Id, files.Select(f => f.Id).ToList(),
                new() { $"{Plural(files.Count, "file")} outside the C# code: {List(files.Select(f => f.RelPath))}. These are shown as plain diffs." },
                new()));
        return steps;
    }

    /// <summary>Shortest chain of unchanged callers from an entry point down to this node.</summary>
    private List<GNode>? PathFromEntry(GNode target)
    {
        var prev = new Dictionary<GNode, GNode>();
        var queue = new Queue<GNode>(); queue.Enqueue(target);
        var seen = new HashSet<GNode> { target };
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (c != target && c.Entry is not null && c.Entry != "Test")
            {
                var path = new List<GNode>();
                for (var x = c; x != target; x = prev[x]) path.Add(x);
                return path;
            }
            foreach (var e in _edges.Where(e => e.To == c && (IsCall(e.Label) || e.Label is "implemented by" or "overridden by"))
                         .OrderBy(e => e.From.RelPath, StringComparer.Ordinal).ThenBy(e => e.FromLine))
                if (seen.Add(e.From)) { prev[e.From] = c; queue.Enqueue(e.From); }
        }
        return null;
    }

    private static string Describe(GNode n)
    {
        var what = n.IsNew ? $"New {n.MemberKind}" : $"Changed {n.MemberKind}";
        var size = n.IsNew ? $"+{n.Adds}" : $"+{n.Adds} −{n.Dels}";
        var sig = n.SignatureChanged ? " The signature changed." : "";
        return $"{what} in `{System.IO.Path.GetFileName(n.RelPath)}` ({size}).{sig}";
    }

    private List<string> Overview(List<StepOut> steps)
    {
        var changed = _nodes.Values.Count(n => n.Changed);
        var files = _diffs.Count;
        var lines = new List<string>
        {
            $"{Plural(changed, "changed unit")} across {Plural(files, "file")}, told in {Plural(steps.Count, "step")}.",
        };
        var entries = _nodes.Values.Where(n => n.Entry is not null && n.Entry != "Test")
            .OrderBy(n => n.RelPath, StringComparer.Ordinal).ThenBy(n => n.StartLine).Select(n => n.Entry!).Distinct().ToList();
        if (entries.Count > 0) lines.Add($"Entry points involved: {List(entries)}.");
        lines.Add("Colored cards are code this PR changes. Gray cards are unchanged code, included because the change calls into it or is called from it.");
        return lines;
    }

    // =====================================================================================
    //  Output
    // =====================================================================================

    private List<NodeOut> EmitNodes()
    {
        var list = new List<NodeOut>();
        foreach (var n in AllNodes())
        {
            var dir = n.RelPath.Contains('/') ? n.RelPath[..(n.RelPath.LastIndexOf('/') + 1)] : "";
            var kind = n.Changed ? (n.IsNew ? "new" : "changed") : "context";
            list.Add(new NodeOut(n.Id, kind, n.Display, n.MemberKind, dir, System.IO.Path.GetFileName(n.RelPath), n.StartLine,
                n.Entry, n.IsTest, n.Adds, n.Dels, n.Layer, n.Order, RenderLines(n), ProjectOf(n)));
        }
        return list;
    }

    // Code cards know their Roslyn project; file cards (configs, docs, ...) join the project whose folder contains them.
    private string ProjectOf(GNode n)
    {
        if (n.Doc is not null) return n.Doc.Project.Name;
        var best = (Dir: "", Name: (string?)null);
        foreach (var p in _sln.Projects)
        {
            if (p.FilePath is null || Rel(System.IO.Path.GetDirectoryName(p.FilePath)) is not { } dir) continue;
            dir = dir == "." ? "" : dir.TrimEnd('/') + "/";
            if (n.RelPath.StartsWith(dir, StringComparison.Ordinal) && (best.Name is null || dir.Length > best.Dir.Length)) best = (dir, p.Name);
        }
        return best.Name ?? "Other files";
    }

    private List<EdgeOut> EmitEdges() => _edges
        .OrderBy(e => e.From.Order).ThenBy(e => e.FromLine).ThenBy(e => e.To.Order)
        .Select(e => new EdgeOut($"{e.From.Id}:L{e.FromLine}", e.To.Id, e.Label)).ToList();

    private List<LineOut> RenderLines(GNode n)
    {
        var rows = new List<(int N, string T, string C, bool Keep)>();
        if (n.Kind == UnitKind.File)
        {
            var path = System.IO.Path.Combine(_o.Repo, n.RelPath);
            var content = !n.Diff!.IsDeleted && !n.Diff.IsBinary && File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            n.EndLine = Math.Max(1, content.Length);
            for (var l = 1; l <= content.Length + 1; l++)
            {
                if (n.Deleted.TryGetValue(l, out var dels)) rows.AddRange(dels.Select(d => (d.OldLine, "-", d.Text, true)));
                if (l <= content.Length) rows.Add((l, n.AddedLines.Contains(l) ? "+" : " ", content[l - 1], n.AddedLines.Contains(l)));
            }
            return Fold(rows, radius: 2, cap: 40);
        }

        var text = n.Doc!.GetTextAsync().Result;
        for (var l = n.StartLine; l <= n.EndLine + 1; l++)
        {
            if (n.Deleted.TryGetValue(l, out var dels)) rows.AddRange(dels.Select(d => (d.OldLine, "-", d.Text, true)));
            if (l > n.EndLine) break;
            var code = text.Lines[l - 1].ToString();
            var added = n.AddedLines.Contains(l);
            var keep = added || n.Anchors.Contains(l) || (!n.Changed && l >= n.HeaderStartLine && l <= n.HeaderEndLine) || l == n.StartLine;
            rows.Add((l, added ? "+" : " ", code, keep));
        }
        return Fold(rows, radius: n.Changed ? 2 : 0, cap: n.Changed ? 70 : 14, anchors: n.Anchors);
    }

    private static List<LineOut> Fold(List<(int N, string T, string C, bool Keep)> rows, int radius, int cap, ISet<int>? anchors = null)
    {
        var show = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Keep)
                for (var j = Math.Max(0, i - radius); j <= Math.Min(rows.Count - 1, i + radius); j++) show[j] = true;
        if (rows.Count > 0) show[^1] = true; // closing brace keeps the card readable

        var outRows = new List<(int N, string T, string C)>();
        for (var i = 0; i < rows.Count;)
        {
            if (show[i]) { outRows.Add((rows[i].N, rows[i].T, rows[i].C)); i++; continue; }
            var j = i; while (j < rows.Count && !show[j]) j++;
            if (j - i == 1) outRows.Add((rows[i].N, rows[i].T, rows[i].C));
            else outRows.Add((rows[i].N, "…", $"{j - i} unchanged lines"));
            i = j;
        }
        if (outRows.Count > cap)
        {
            var hidden = outRows.Count - cap + 1;
            outRows = outRows.Take(cap - 1).Append((outRows[cap - 1].N, "…", $"{hidden} more lines")).ToList();
        }

        // Dedent to the shallowest visible line.
        static string Expand(string s) => s.Replace("\t", "    ").TrimEnd();
        var indent = outRows.Where(r => r.T != "…" && Expand(r.C).Length > 0)
            .Select(r => Expand(r.C).Length - Expand(r.C).TrimStart().Length).DefaultIfEmpty(0).Min();
        return outRows.Select(r =>
        {
            var c = r.T == "…" ? r.C : Expand(r.C);
            if (r.T != "…" && c.Length >= indent) c = c[indent..];
            var id = r.T is "+" or " " && anchors?.Contains(r.N) == true ? $"L{r.N}" : null;
            return new LineOut(r.N, r.T, c, id);
        }).ToList();
    }

    // =====================================================================================
    //  Symbol helpers
    // =====================================================================================

    private async Task<SemanticModel> ModelAsync(Document doc)
    {
        var tree = await doc.GetSyntaxTreeAsync() ?? throw new InvalidOperationException("no tree");
        if (!_models.TryGetValue(tree, out var m))
            _models[tree] = m = await doc.GetSemanticModelAsync() ?? throw new InvalidOperationException("no model");
        return m;
    }

    /// <summary>The node for a symbol's declaration, optionally creating an unchanged (context) node.</summary>
    private async Task<GNode?> NodeForAsync(ISymbol symbol, bool create)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences
            .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(r => r.Span.Start).FirstOrDefault();
        if (syntaxRef is null) return null;
        var doc = _sln.GetDocument(syntaxRef.SyntaxTree);
        if (doc is null || Rel(doc.FilePath) is null) return null;
        var model = await ModelAsync(doc);
        var root = await doc.GetSyntaxRootAsync();
        var unit = UnitsAround(root!.FindToken(syntaxRef.Span.Start), model).FirstOrDefault();
        if (unit is null) return null;
        if (_nodes.TryGetValue(unit.Key, out var n)) return n;
        return create ? GetOrAddNode(unit, doc, await doc.GetTextAsync()) : null;
    }

    /// <summary>Best card to stand for a type: its own card, its constructor, or its first changed member.</summary>
    private async Task<GNode?> NodeForTypeAsync(INamedTypeSymbol type, bool createContext = true)
    {
        type = type.OriginalDefinition;
        if (_nodes.TryGetValue(KeyOf(type), out var own)) return own;
        var members = _nodes.Values.Where(n => n.Changed && SymbolEqualityComparer.Default.Equals(n.ContainingType, type))
            .OrderBy(n => n.Symbol switch { IMethodSymbol { MethodKind: MethodKind.Constructor } => 0, IMethodSymbol => 1, _ => 2 })
            .ThenBy(n => n.StartLine).ToList();
        if (members.Count > 0) return members[0];
        if (!createContext) return null;
        var ctor = type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).OrderBy(c => c.Parameters.Length).FirstOrDefault();
        return ctor is not null ? await NodeForAsync(ctor, true) : await NodeForAsync(type, true);
    }

    private async Task<List<RefSite>> RefsAsync(ISymbol symbol)
    {
        var key = KeyOf(symbol);
        if (_refCache.TryGetValue(key, out var cached)) return cached;
        var list = new List<RefSite>();
        foreach (var rs in await SymbolFinder.FindReferencesAsync(symbol, _sln))
        {
            if (!SymbolEqualityComparer.Default.Equals(rs.Definition.OriginalDefinition, symbol.OriginalDefinition)) continue;
            foreach (var loc in rs.Locations)
            {
                if (loc.IsImplicit || !loc.Location.IsInSource) continue;
                var rel = Rel(loc.Document.FilePath);
                if (rel is null) continue;
                var text = await loc.Document.GetTextAsync();
                var pos = loc.Location.SourceSpan.Start;
                list.Add(new RefSite(loc.Document, pos, LineOf(text, pos), rel));
            }
        }
        list = list.GroupBy(r => (r.RelPath, r.Position)).Select(g => g.First())
            .OrderBy(r => r.RelPath, StringComparer.Ordinal).ThenBy(r => r.Position).ToList();
        _refCache[key] = list;
        return list;
    }

    private async Task<(Unit? Unit, string Label, SyntaxNode? Node)> UnitAtAsync(RefSite site)
    {
        var root = await site.Doc.GetSyntaxRootAsync();
        var model = await ModelAsync(site.Doc);
        var token = root!.FindToken(site.Position);
        var node = token.Parent;
        if (node is null || node.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsNameof)) return (null, "", null);
        var unit = UnitsAround(token, model).FirstOrDefault();
        var label = "uses";
        for (var x = node; x is not null; x = x.Parent)
        {
            if (x is InvocationExpressionSyntax inv && inv.Expression.Span.Contains(node.Span)) { label = "calls"; node = inv; break; }
            if (x is BaseObjectCreationExpressionSyntax oc) { label = "creates"; node = oc; break; }
            if (x is StatementSyntax or MemberDeclarationSyntax) break;
        }
        return (unit, label, node);
    }

    internal string? Rel(string? path)
    {
        if (path is null) return null;
        var rel = System.IO.Path.GetRelativePath(_o.Repo, path).Replace('\\', '/');
        if (rel.StartsWith("../") || System.IO.Path.IsPathRooted(rel) || Loader.IsBuildOutput("/" + rel)) return null;
        return rel;
    }

    private static string KeyOf(ISymbol s) => "S:" + (s.OriginalDefinition.GetDocumentationCommentId() ?? s.OriginalDefinition.ToDisplayString());

    private static string SortKey(ISymbol s)
    {
        var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
        return $"{loc?.SourceTree?.FilePath}:{loc?.SourceSpan.Start:D8}:{s.ToDisplayString()}";
    }

    private static ISymbol Normalize(ISymbol s)
    {
        if (s is IMethodSymbol m)
        {
            m = m.ReducedFrom ?? m;
            if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet && m.AssociatedSymbol is not null)
                return m.AssociatedSymbol.OriginalDefinition;
            m = m.PartialImplementationPart ?? m;
            return m.OriginalDefinition;
        }
        return s.OriginalDefinition;
    }

    private static ISymbol? Resolve(SymbolInfo info) =>
        info.Symbol ?? info.CandidateSymbols.OrderBy(c => c.ToDisplayString(), StringComparer.Ordinal).FirstOrDefault();

    internal static bool IsSource(ISymbol s) => s.Locations.Any(l => l.IsInSource);

    private static bool IsNameof(InvocationExpressionSyntax inv) => inv.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" };

    private static int NamePosition(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.SpanStart,
        MemberBindingExpressionSyntax mb => mb.Name.SpanStart,
        _ => inv.SpanStart,
    };

    private static bool IsWrite(IdentifierNameSyntax id)
    {
        SyntaxNode n = id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id ? ma : id;
        return n.Parent switch
        {
            AssignmentExpressionSyntax a => a.Left == n,
            PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => true,
            _ => false,
        };
    }

    private static (int Start, int End) HeaderSpan(Unit u)
    {
        var d = u.Decl;
        if (u.Kind == UnitKind.TopLevel) return (u.Span.Start, u.Span.Start + 1);
        var first = d is MemberDeclarationSyntax { AttributeLists.Count: > 0 } md
            ? md.AttributeLists[^1].GetLastToken().GetNextToken().SpanStart
            : d.SpanStart;
        var end = d switch
        {
            BaseMethodDeclarationSyntax m => m.Body?.SpanStart ?? m.ExpressionBody?.SpanStart ?? m.Span.End,
            PropertyDeclarationSyntax p => p.AccessorList?.SpanStart ?? p.ExpressionBody?.SpanStart ?? p.Span.End,
            IndexerDeclarationSyntax p => p.AccessorList?.SpanStart ?? p.ExpressionBody?.SpanStart ?? p.Span.End,
            TypeDeclarationSyntax t when !t.OpenBraceToken.IsMissing => t.OpenBraceToken.SpanStart,
            EnumDeclarationSyntax e => e.OpenBraceToken.SpanStart,
            _ => d.Span.End,
        };
        return (first, Math.Max(first + 1, end));
    }

    private static string TypeName(ITypeSymbol t) =>
        (t.ContainingType is null ? "" : TypeName(t.ContainingType) + ".") + t.Name +
        (t is INamedTypeSymbol { TypeParameters.Length: > 0 } g ? "<" + string.Join(", ", g.TypeParameters.Select(p => p.Name)) + ">" : "");

    internal static string DisplayOf(Unit u, string relPath) => u.Kind switch
    {
        UnitKind.TopLevel => $"{System.IO.Path.GetFileName(relPath)} (top-level)",
        _ => u.Symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } m => $"{TypeName(m.ContainingType)} constructor",
            IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } m =>
                $"{TypeName(m.ContainingType)} operator {SyntaxFacts.GetText(SyntaxFacts.GetOperatorKind(m.MetadataName))}",
            IMethodSymbol { MethodKind: MethodKind.Conversion } m => $"{TypeName(m.ContainingType)} conversion to {m.ReturnType.Name}",
            IMethodSymbol m => $"{TypeName(m.ContainingType)}.{m.Name}()",
            INamedTypeSymbol t => TypeName(t),
            { } s => $"{TypeName(s.ContainingType)}.{s.Name}",
            _ => System.IO.Path.GetFileName(relPath),
        },
    };

    private static string MemberKindOf(Unit u) => u.Kind switch
    {
        UnitKind.TopLevel => "startup code",
        _ => u.Symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
            IMethodSymbol m when m.ContainingType.TypeKind == TypeKind.Interface => "interface method",
            IMethodSymbol => "method",
            IPropertySymbol p when p.ContainingType.TypeKind == TypeKind.Interface => "interface property",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            INamedTypeSymbol { IsRecord: true } => "record",
            INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
            _ => "code",
        },
    };

    // ---------- entry points ----------

    private static readonly string[] TestAttributes = { "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod" };
    private static readonly string[] HandlerInterfaces = { "IRequestHandler", "INotificationHandler", "IConsumer", "IHandleMessages", "IHandle", "IEventHandler", "ICommandHandler", "IQueryHandler" };

    private static string AttrName(AttributeData a)
    {
        var n = a.AttributeClass?.Name ?? "";
        return n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^9] : n;
    }

    internal string? EntryOf(Unit u)
    {
        if (u.Kind == UnitKind.TopLevel) return "Program start";
        if (u.Symbol is not IMethodSymbol m) return null;
        var attrs = m.GetAttributes().Select(AttrName).ToList();
        if (attrs.Any(a => TestAttributes.Contains(a))) return "Test";
        if (m.Name == "Main" && m.IsStatic) return "Program start";
        if (attrs.Contains("Function") || attrs.Contains("FunctionName")) return "Azure Function";
        if (MinimalRoutes().TryGetValue(KeyOf(m), out var route)) return route;
        if (m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic &&
            !attrs.Contains("NonAction") && IsController(m.ContainingType))
            return HttpRoute(m, attrs);
        if (m.Name == "ExecuteAsync" && m.IsOverride && DerivesFrom(m.ContainingType, "BackgroundService")) return "Background service";
        foreach (var a in Abstractions(m))
        {
            var iface = a.ContainingType.Name;
            if (iface == "IHostedService") return "Background service";
            if (HandlerInterfaces.Contains(iface)) return $"Message handler ({a.ContainingType.TypeArguments.FirstOrDefault()?.Name ?? iface})";
        }
        return null;
    }

    private static bool DerivesFrom(INamedTypeSymbol? t, string name)
    {
        for (var b = t?.BaseType; b is not null; b = b.BaseType) if (b.Name == name) return true;
        return false;
    }

    private static bool IsController(INamedTypeSymbol t)
    {
        if (t.IsAbstract) return false;
        for (var x = t; x is not null; x = x.BaseType)
        {
            if (x.Name is "ControllerBase" or "Controller" && x.ContainingNamespace?.ToDisplayString() == "Microsoft.AspNetCore.Mvc") return true;
            if (x.GetAttributes().Any(a => AttrName(a) is "ApiController" or "Controller")) return true;
        }
        return t.Name.EndsWith("Controller", StringComparison.Ordinal) && t.DeclaredAccessibility == Accessibility.Public && t.BaseType?.Name is "ControllerBase" or "Controller";
    }

    private static string HttpRoute(IMethodSymbol m, List<string> attrNames)
    {
        string? Template(IEnumerable<AttributeData> attrs, Func<string, bool> pick) => attrs
            .Where(a => pick(AttrName(a)))
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string ?? "")
            .FirstOrDefault();
        var verbAttr = m.GetAttributes().FirstOrDefault(a => AttrName(a) is "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch" or "HttpHead" or "HttpOptions");
        var verb = verbAttr is null ? "HTTP" : AttrName(verbAttr)[4..].ToUpperInvariant();
        var methodTemplate = verbAttr?.ConstructorArguments.FirstOrDefault().Value as string ?? Template(m.GetAttributes(), a => a == "Route");
        string? classTemplate = null;
        for (var t = m.ContainingType; t is not null && classTemplate is null; t = t.BaseType)
            classTemplate = Template(t.GetAttributes(), a => a == "Route");
        var controller = m.ContainingType.Name.EndsWith("Controller") ? m.ContainingType.Name[..^10] : m.ContainingType.Name;
        string route;
        if (methodTemplate is not null && (methodTemplate.StartsWith('/') || methodTemplate.StartsWith("~/")))
            route = methodTemplate.TrimStart('~');
        else
            route = "/" + string.Join("/", new[] { classTemplate, methodTemplate }.Where(s => !string.IsNullOrEmpty(s)));
        route = route.Replace("[controller]", controller).Replace("[action]", m.Name);
        return $"{(verb == "HTTP" ? "HTTP" : "HTTP " + verb)} {route}";
    }

    private Dictionary<string, string>? _minimalRoutes;

    /// <summary>Methods wired as minimal API handlers via app.MapGet("/x", Handler).</summary>
    private Dictionary<string, string> MinimalRoutes()
    {
        if (_minimalRoutes is not null) return _minimalRoutes;
        _minimalRoutes = new(StringComparer.Ordinal);
        foreach (var doc in _docs.Values)
        {
            var root = doc.GetSyntaxRootAsync().Result!;
            SemanticModel? model = null;
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (MinimalApiVerb(inv) is not { } verb) continue;
                model ??= ModelAsync(doc).Result;
                foreach (var arg in inv.ArgumentList.Arguments.Skip(1))
                    if (arg.Expression is not LambdaExpressionSyntax && model.GetSymbolInfo(arg.Expression).Symbol is IMethodSymbol h)
                        _minimalRoutes.TryAdd(KeyOf(Normalize(h)), $"HTTP {verb} {RouteLiteral(inv)}");
            }
        }
        return _minimalRoutes;
    }

    private static string? MinimalApiVerb(InvocationExpressionSyntax inv)
    {
        var name = inv.Expression switch { MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text, IdentifierNameSyntax id => id.Identifier.Text, _ => null };
        return name switch { "MapGet" => "GET", "MapPost" => "POST", "MapPut" => "PUT", "MapDelete" => "DELETE", "MapPatch" => "PATCH", _ => null };
    }

    private static string RouteLiteral(InvocationExpressionSyntax inv) =>
        inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : "…";

    /// <summary>Calls made inside a minimal API lambda are labelled with the route they serve.</summary>
    private static string? MinimalApiRoute(InvocationExpressionSyntax inv, SemanticModel _)
    {
        foreach (var lambda in inv.Ancestors().OfType<LambdaExpressionSyntax>())
            if (lambda.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax map } && MinimalApiVerb(map) is { } verb)
                return $"HTTP {verb} {RouteLiteral(map)}";
        return null;
    }

    private static bool IsTestProject(Project p) =>
        Regex.IsMatch(p.Name, @"(^|[.\-_])(Unit|Integration|Functional|Acceptance)?Tests?$", RegexOptions.IgnoreCase);

    // ---------- text helpers ----------

    private static int LineOf(SourceText text, int pos) => text.Lines.GetLineFromPosition(Math.Min(pos, text.Length)).LineNumber + 1;

    private static int? FirstCharPos(SourceText text, int line)
    {
        if (line < 1 || line > text.Lines.Count) return null;
        var l = text.Lines[line - 1];
        for (var p = l.Start; p < l.End; p++) if (!char.IsWhiteSpace(text[p])) return p;
        return null;
    }

    private static bool IsBlank(SourceText text, int line) => FirstCharPos(text, line) is null;

    private static bool IsBraceOnly(SourceText text, int line)
    {
        if (line < 1 || line > text.Lines.Count) return true;
        var s = text.Lines[line - 1].ToString().Trim();
        return s.Length == 0 || s is "{" or "}" or "};";
    }

    private static bool IsCall(string label) => label is "calls" or "creates" || label.StartsWith("calls via") || label.StartsWith("HTTP");

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static string List(IEnumerable<string> items)
    {
        var l = items.Select(i => $"`{i}`").ToList();
        return l.Count switch
        {
            0 => "",
            1 => l[0],
            2 => $"{l[0]} and {l[1]}",
            _ => $"{string.Join(", ", l.Take(l.Count - 1))} and {l[^1]}",
        };
    }
}

/// <summary>Services registered with Microsoft.Extensions.DependencyInjection anywhere in the repo.</summary>
internal sealed class DiRegistry
{
    private readonly HashSet<string> _services = new(StringComparer.Ordinal);
    private readonly HashSet<string> _implementations = new(StringComparer.Ordinal);

    private static readonly HashSet<string> Provided = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.Logging.ILogger", "Microsoft.Extensions.Logging.ILogger<TCategoryName>", "Microsoft.Extensions.Logging.ILoggerFactory",
        "Microsoft.Extensions.Options.IOptions<TOptions>", "Microsoft.Extensions.Options.IOptionsSnapshot<TOptions>", "Microsoft.Extensions.Options.IOptionsMonitor<TOptions>",
        "Microsoft.Extensions.Configuration.IConfiguration", "System.IServiceProvider", "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
        "System.Net.Http.IHttpClientFactory", "Microsoft.Extensions.Hosting.IHostEnvironment", "Microsoft.AspNetCore.Hosting.IWebHostEnvironment",
        "Microsoft.Extensions.Hosting.IHostApplicationLifetime", "System.Net.Http.HttpClient",
    };

    public static bool IsRegistrationMethod(IMethodSymbol m) =>
        Regex.IsMatch(m.Name, @"^(Try)?Add(Keyed)?(Singleton|Scoped|Transient|HostedService|DbContext|DbContextFactory|HttpClient)$");

    public static DiRegistry Build(Solution sln, Analyzer a)
    {
        var r = new DiRegistry();
        foreach (var doc in sln.Projects.SelectMany(p => p.Documents).Where(d => a.Rel(d.FilePath) is not null))
        {
            var root = doc.GetSyntaxRootAsync().Result!;
            SemanticModel? model = null;
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = inv.Expression switch { MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text, _ => "" };
                if (!name.Contains("Add")) continue;
                model ??= doc.GetSemanticModelAsync().Result!;
                if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol m || !IsRegistrationMethod(m)) continue;
                if (m.TypeArguments.Length > 0)
                {
                    r._services.Add(Key(m.TypeArguments[0]));
                    r._implementations.Add(Key(m.TypeArguments[^1]));
                    if (m.TypeArguments.Length > 1) r._services.Add(Key(m.TypeArguments[^1]));
                }
                foreach (var arg in inv.ArgumentList.Arguments)
                {
                    if (arg.Expression is TypeOfExpressionSyntax to && model.GetTypeInfo(to.Type).Type is { } tt)
                    { r._services.Add(Key(tt)); r._implementations.Add(Key(tt)); }
                    else if (m.TypeArguments.Length == 0 && arg.Expression is not LambdaExpressionSyntax && model.GetTypeInfo(arg.Expression).Type is { } at &&
                             at.Name != "IServiceCollection")
                        r._services.Add(Key(at));
                }
            }
        }
        return r;
    }

    private static string Key(ITypeSymbol t) => t.OriginalDefinition.ToDisplayString();

    public bool IsRegisteredImplementation(ITypeSymbol t) => _implementations.Contains(Key(t)) || _services.Contains(Key(t));

    public bool CanResolve(ITypeSymbol t)
    {
        var k = Key(t);
        if (_services.Contains(k) || Provided.Contains(k)) return true;
        if (t is INamedTypeSymbol { Name: "IEnumerable" }) return true;
        return false;
    }
}
