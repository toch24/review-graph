namespace ReviewGraph;

// ---------- JSON contract consumed by viewer.html ----------

public sealed record ReviewDoc(
    PrInfo Pr,
    StatsOut Stats,
    List<NodeOut> Nodes,
    List<EdgeOut> Edges,
    List<StepOut> Steps,
    List<string> Warnings,
    string Generator);

public sealed record PrInfo(string Repo, string Title, string Branch, string Base, string Head, string Analysis);

public sealed record StatsOut(int Adds, int Dels, int FilesChanged, int ChangedNodes, int ContextNodes);

public sealed record NodeOut(
    string Id,
    string Kind,          // new | changed | context
    string Sym,           // display name, e.g. CheckoutService.PlaceOrderAsync()
    string Member,        // method | constructor | property | field | type | interface member | top-level | file ...
    string Path,          // directory part, with trailing slash
    string File,          // file name
    int Line,             // first line of the member (head side)
    string? Entry,        // e.g. "HTTP POST /api/Checkout" when this is where execution starts
    bool Test,
    int Adds,
    int Dels,
    int Layer,
    int Order,
    List<LineOut> Lines,
    string? Project = null);   // owning project (csproj) name; the viewer groups cards by it

/// <param name="T">"+" added, "-" removed, " " unchanged, "…" folded run of unchanged lines</param>
/// <param name="Id">Anchor id ("L42") when an edge starts or ends on this row</param>
public sealed record LineOut(int N, string T, string C, string? Id = null);

public sealed record EdgeOut(string From, string To, string Label);

public sealed record CheckOut(bool Ok, string Text);

public sealed record StepOut(string Title, string Primary, List<string> Nodes, List<string> Body, List<CheckOut> Checks);
