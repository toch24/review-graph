# reviewgraph

Turns a C# pull request into a storyline graph a reviewer can read without knowing the codebase.
Changed code becomes cards, unchanged code that explains it becomes gray context cards, and edges
start at the exact line that makes the call. A side panel walks through the change step by step,
from the entry point (HTTP route, `Main`, message handler, background service, test) down to the
data it touches, with rule-based checks for each step.

The output is deterministic: the same repo, base and head always produce byte-identical
`review.html` and `review.json`.

## Run it

```bash
# from the PR branch, with the solution restored (dotnet restore)
dotnet run --project src/ReviewGraph -c Release -- path/to/repo --base main --out review.html --json review.json
```

| Option | Meaning |
|---|---|
| `repo` | Repository to analyse. The working tree is analysed, so check out the PR branch first. |
| `--base <ref>` | Branch the PR merges into. Defaults to `main`, then `master`. The diff starts at the merge base. |
| `--head <ref>` | Commit to compare. Defaults to the working tree, uncommitted edits included. Must be checked out. |
| `--solution <path>` | `.sln` or `.csproj` to load. Defaults to the first `.sln` in the repo root, else every `.csproj`. |
| `--out` / `--json` | Self-contained viewer page / raw graph JSON. |
| `--title <text>` | PR title. Defaults to the latest commit subject. |
| `--source-only` | Skip MSBuild and compile the `.cs` files directly against the installed .NET and ASP.NET Core reference assemblies. No restore needed. Calls between your own types still resolve; types from NuGet packages don't. |

The viewer page has no server and no build step. Open it in a browser, or load any `review.json`
into it with **Open JSON**.

**No access to nuget.org?** Build against the Roslyn copy that ships inside the .NET SDK:
`dotnet build -c Release -p:RoslynDir=$(dirname $(which dotnet))/sdk/<version>/DotnetTools/dotnet-format`

### In CI (GitHub Actions)

```yaml
- uses: actions/checkout@v4
  with: { fetch-depth: 0 }
- uses: actions/setup-dotnet@v4
  with: { dotnet-version: '8.0.x' }
- run: dotnet restore
- run: dotnet run --project tools/reviewgraph/src/ReviewGraph -c Release -- . --base origin/${{ github.base_ref }} --out review.html
- uses: actions/upload-artifact@v4
  with: { name: review-graph, path: review.html }
```

## How it works

Everything runs on Roslyn's semantic model, which is the compiler's own understanding of the code, so
calls resolve through overloads, extension methods, generics and `var` the way the compiler sees them.

1. **Diff → units.** `git diff --unified=0` against the merge base gives exact added and removed
   lines. Each one is mapped to the innermost *unit* around it: a method, constructor, property,
   field, a type (only for changes outside its members), or a file's top-level statements. A unit is
   **new** when every non-blank line in it was added, otherwise **changed**. Removed lines go to the
   unit that surrounds the gap they left. Non-C# files become plain diff cards.
2. **Outgoing edges.** Inside each changed unit, invocations, `new` expressions and references to
   other changed data are resolved to symbols. Edges from *changed* lines are always kept. Edges from
   unchanged lines are kept only when they point at other changed code, so the graph stays about the
   change. DI registrations (`AddScoped<IFoo, Foo>()`) become `registers` edges to `Foo`.
3. **Interfaces and overrides.** A changed implementation is linked from the interface or base
   member it implements (`implemented by`), because that is what callers actually call. A call that
   lands on an interface with exactly one implementation is followed to it.
4. **Callers.** `SymbolFinder.FindReferencesAsync` finds who calls each changed method, directly or
   through its interface. Up to 3 unchanged callers are added as context, entry points first, then
   1 test.
5. **Back to an entry point.** Unchanged callers are walked up to 3 more hops until an entry point
   is found: controller actions (with the route built from `[Route]` and `[Http*]` templates),
   minimal API handlers (`MapGet` and friends), `Main` and top-level statements, `BackgroundService`,
   `IHostedService`, MediatR/MassTransit/NServiceBus-style handlers, Azure Functions and tests.
6. **Checks.** Each changed unit gets deterministic review checks:
   - compiler warnings and errors on added lines (e.g. CS4014, an un-awaited task)
   - signature changed: which call sites were not touched by the PR
   - constructor changed in a DI-registered class: is every injected type registered?
   - new `throw`s, and whether callers in the graph catch them
   - other implementations of the same interface member the PR did not change
   - new methods nothing calls yet
   - whether any test reaches the code within 3 calls, and whether that test changed in the PR
7. **Story and layout.** A depth-first walk starts at the entry points that reach the most changed
   code and follows calls in source order, the way you'd step through it in a debugger. That order
   numbers the steps. Small supporting changes (a new property, an interface signature, a constructor
   that only assigns fields) ride along with the step that uses them. Columns are call depth
   (longest path over the acyclic part of the call graph); data cards sit one column after their
   first reader. Every sort uses ordinal string comparison on file path and line, which is what
   makes the output reproducible.

## Known limits

- Reflection, `dynamic`, string-based dispatch and source-generated code are invisible to static
  analysis, so those connections will be missing.
- In `--source-only` mode, members of types from NuGet packages don't resolve. Edges between your
  own code still work.
- Story text is template-based. It describes structure accurately but doesn't explain intent. An
  LLM narration pass could be layered on top of the JSON without touching the graph itself.
- Very large PRs produce large graphs. Use the step view and the context toggle.

## Files

- `src/ReviewGraph/GitDiff.cs`: git invocation and zero-context diff parsing
- `src/ReviewGraph/Loader.cs`: MSBuildWorkspace loading with a source-only fallback
- `src/ReviewGraph/Analyzer.cs`: units, edges, entry points, checks, ordering, layout, steps
- `src/ReviewGraph/Model.cs`: the JSON contract
- `src/ReviewGraph/viewer.html`: the viewer, embedded into the tool and filled with the JSON
- `samples/sample-shop`: an ASP.NET Core repo with a `feature/coupons` branch to try it on
