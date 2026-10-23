# review-graph

Reviewing a pull request is not linear. A PR is not a list of files to read from A to Z. It is a story:
a request comes in, it moves through the code, and it ends up in the data. Reading files in
alphabetical order hides that story.

review-graph draws the story as a graph for C# pull requests.

- Every changed method, property or field is a **card**.
- Every call from one card to another is an **arrow**. The arrow starts at the exact line that makes the call.
- The graph reads left to right, from the **entry point** (an HTTP route, `Main`, a message handler,
  a background service, a test) down to the data it touches.
- A side panel walks you through the story **step by step**, the way you would step through it in a
  debugger. Each step has simple checks (for example: "this method is new and nothing calls it").

You can review the PR without knowing the codebase.

The result is always the same: the same repo, base and head give the exact same `review.html` and
`review.json`.

## What shows in the graph

- **Changed code.** Every method, constructor, property, field and type the PR touched. A card is
  **new** if every line in it was added. Otherwise it is **changed**.
- **Calls that come from changed lines.** Always shown.
- **Calls from unchanged lines, only if they lead to other changed code.** This keeps the graph about the change.
- **Gray context cards.** Unchanged code that helps explain the change:
  - up to 3 callers of each changed method (entry points first, then 1 test)
  - callers walked up to 3 more steps, until an entry point is found
  - the interface that a changed class implements
- **DI registrations.** `AddScoped<IFoo, Foo>()` becomes an arrow to `Foo`.
- **Other files.** Non-C# files that changed show up as plain diff cards.
- **Checks.** Compiler warnings on added lines, changed signatures, missing DI registrations, new
  `throw`s, other implementations the PR did not update, new unused methods, and whether a test
  reaches the code.

## What does not show

- **Unchanged code that is not related to the change.** The graph is not a map of your whole project.
- **Calls hidden from static analysis:** reflection, `dynamic`, string-based dispatch and
  source-generated code.
- **`using` changes.** Added or removed `using` lines get no card, because they don't change what the code does.
- **More than 3 unchanged callers** for one method.
- **Calls into NuGet packages in `--source-only` mode.** Calls between your own types still work.
- **The reason behind the change.** The story text describes the structure. It does not explain why
  the author did it.

Very large PRs make large graphs. Use the step view and the context toggle to cut them down.

## Install

Install it as a .NET tool:

```bash
dotnet tool install -g reviewgraph
```

Update with `dotnet tool update -g reviewgraph`. Check the version with `reviewgraph --version`.
The package targets .NET 8 and .NET 10; `dotnet tool install` picks the one that matches your SDK.

You can also skip the install and run it from source (see below).

## Run it

Check out the PR branch first. The tool reads the working tree.

**With the installed tool:**

```bash
reviewgraph path/to/repo --base main --out review.html --json review.json
```

**From source** (fork or clone this repo, then restore the solution with `dotnet restore`):

```bash
dotnet run --project src/ReviewGraph -c Release -- path/to/repo --base main --out review.html --json review.json
```

Options:

| Option | Meaning |
|---|---|
| `repo` | Repository to analyse. It reads the working tree, so check out the PR branch first. |
| `--base <ref>` | Branch the PR merges into. Default: `main`, then `master`. The diff starts at the merge base. |
| `--head <ref>` | Commit to compare. Default: the working tree, including uncommitted edits. Must be checked out. |
| `--solution <path>` | `.sln` or `.csproj` to load. Default: the first `.sln` in the repo root, else every `.csproj`. |
| `--out` / `--json` | Viewer page (one file) / raw graph JSON. |
| `--title <text>` | PR title. Default: the latest commit subject. |
| `--source-only` | Skip MSBuild and compile the `.cs` files directly against the installed .NET and ASP.NET Core libraries. No restore needed. Calls between your own types still work. Types from NuGet packages do not. |

The viewer page needs no server and no build step. Open it in a browser, or load any `review.json`
into it with **Open JSON**.

**No access to nuget.org?** Build with the Roslyn copy that ships inside the .NET SDK:
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

### Releasing

The version lives in `src/ReviewGraph/ReviewGraph.csproj` (`<Version>`). To publish:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

The `release` workflow packs the tag as version `0.2.0` and pushes it to nuget.org
using NuGet trusted publishing (see below). To pack by hand: `dotnet pack src/ReviewGraph -c Release`.

One-time setup on nuget.org: **Trusted Publishing** → add a policy for this repository
(`toch24/review-graph`) and the workflow file `release.yml`. Then add a `NUGET_USER` repository
variable (Settings → Secrets and variables → Actions → Variables) with your nuget.org username.

## How it works

It uses Roslyn, the C# compiler. So it sees calls the same way the compiler does, even through
overloads, extension methods, generics and `var`.

1. **Diff to cards.** `git diff --unified=0` against the merge base gives the exact lines added and
   removed. Each line goes to the smallest piece of code around it: a method, constructor, property,
   field, a type (only for changes outside its members), or a file's top-level statements. Removed
   lines go to the code around the gap they left.
2. **Arrows.** In each changed card, every call, `new` and use of other changed data is resolved to
   the real code it points at.
3. **Interfaces.** A changed class is linked from the interface or base member it implements, because
   that is what callers really call. If an interface has only one implementation, a call to it is
   followed to that implementation.
4. **Callers.** `SymbolFinder.FindReferencesAsync` finds who calls each changed method. Up to 3
   unchanged callers are added as context: entry points first, then 1 test.
5. **Back to an entry point.** Unchanged callers are walked up 3 more steps until an entry point is
   found:
   - controller actions (with the route built from `[Route]` and `[Http*]`)
   - minimal API handlers (`MapGet` and friends)
   - `Main` and top-level statements
   - `BackgroundService` and `IHostedService`
   - MediatR, MassTransit and NServiceBus style handlers
   - Azure Functions
   - tests
6. **Checks.** Each changed card gets these checks:
   - compiler warnings and errors on added lines (for example CS4014, a task that is not awaited)
   - signature changed: which call sites the PR did not touch
   - constructor changed in a DI class: is every injected type registered?
   - new `throw`s, and whether the callers in the graph catch them
   - other implementations of the same interface member that the PR did not change
   - new methods that nothing calls yet
   - whether a test reaches the code within 3 calls, and whether that test changed in the PR
7. **Story and layout.** The walk starts at the entry points that reach the most changed code. It
   follows calls in source order, like a debugger. That order gives the step numbers. Small helper
   changes (a new property, an interface signature, a constructor that only sets fields) join the
   step that uses them. Columns are call depth. Data cards sit one column after the first card that
   reads them. All sorting is by file path and line, so the output is always the same.

## Known limits

- Reflection, `dynamic`, string-based dispatch and source-generated code are invisible, so those
  connections are missing.
- In `--source-only` mode, members of NuGet types do not resolve.
- The story text is built from templates. It describes the structure, not the intent. An LLM could
  add a narration on top of the JSON without changing the graph.

## Files

- `src/ReviewGraph/GitDiff.cs`: runs git and reads the diff
- `src/ReviewGraph/Loader.cs`: loads the solution with MSBuild, with a source-only fallback
- `src/ReviewGraph/Analyzer.cs`: cards, arrows, entry points, checks, order, layout, steps
- `src/ReviewGraph/Model.cs`: the JSON format
- `src/ReviewGraph/viewer.html`: the viewer, put inside the tool and filled with the JSON
- `samples/sample-shop`: an ASP.NET Core repo with a `feature/coupons` branch to try it on
