using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Build.Locator;

namespace ReviewGraph;

public sealed class Options
{
    public string Repo { get; set; } = Directory.GetCurrentDirectory();
    public string? Base { get; set; }
    public string? Head { get; set; }
    public string? Solution { get; set; }
    public string Out { get; set; } = "review.html";
    public string? Json { get; set; }
    public string? Title { get; set; }
    public bool SourceOnly { get; set; }

    public const string Usage = """
        reviewgraph – turn a C# pull request into a storyline graph for reviewers

        usage: reviewgraph [repo] [options]

          repo                 Git repository to analyse (default: current directory).
                               The working tree is analysed, so check out the PR branch first.
          --base <ref>         Branch or commit the PR merges into (default: main, then master).
          --head <ref>         Commit to compare (default: the working tree, including uncommitted edits).
                               Must be the commit that is checked out.
          --solution <path>    .sln or .csproj to load (default: first .sln in the repo root,
                               otherwise every .csproj).
          --out <file.html>    Self-contained viewer page to write (default: review.html).
          --json <file.json>   Also write the raw graph JSON.
          --title <text>       PR title (default: subject of the latest commit).
          --source-only        Skip MSBuild and compile the .cs files directly. Faster, works
                               without a restore, but types from NuGet packages go unresolved.
        """;

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new UserError($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "-h" or "--help": return null;
                case "--base": o.Base = Next(); break;
                case "--head": o.Head = Next(); break;
                case "--solution": o.Solution = Path.GetFullPath(Next()); break;
                case "--out": o.Out = Next(); break;
                case "--json": o.Json = Next(); break;
                case "--title": o.Title = Next(); break;
                case "--source-only": o.SourceOnly = true; break;
                default:
                    if (args[i].StartsWith('-')) throw new UserError($"Unknown option {args[i]}.");
                    o.Repo = args[i];
                    break;
            }
        }
        o.Repo = Path.GetFullPath(o.Repo);
        return o;
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var o = Options.Parse(args);
            if (o is null) { Console.WriteLine(Options.Usage); return 0; }
            if (!o.SourceOnly) RegisterMsBuild();
            return await RunAsync(o);
        }
        catch (UserError e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    // Must run before any MSBuild type is loaded, so it lives in its own method.
    private static void RegisterMsBuild()
    {
        if (MSBuildLocator.IsRegistered) return;
        var instance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();
        if (instance is not null) MSBuildLocator.RegisterInstance(instance);
    }

    private static async Task<int> RunAsync(Options o)
    {
        var repo = Git.Run(o.Repo, "rev-parse", "--show-toplevel").Trim();
        o.Repo = Path.GetFullPath(repo);

        var baseRef = o.Base ?? (Git.TryRun(o.Repo, "rev-parse", "--verify", "--quiet", "main") is not null ? "main" : "master");
        var headSha = Git.Run(o.Repo, "rev-parse", "HEAD").Trim();
        if (o.Head is not null && Git.Run(o.Repo, "rev-parse", o.Head).Trim() != headSha)
            throw new UserError($"--head {o.Head} is not checked out. Run `git checkout {o.Head}` first, since the working tree is what gets analysed.");
        var mergeBase = Git.Run(o.Repo, "merge-base", baseRef, "HEAD").Trim();

        var diffArgs = new List<string> { "diff", "--unified=0", "--no-color", "--no-ext-diff", "-M", mergeBase };
        if (o.Head is not null) diffArgs.Add(o.Head);
        var diffs = Git.Parse(Git.Run(o.Repo, diffArgs.ToArray()));
        if (o.Head is null)
        {
            // Untracked files are part of the working tree too.
            foreach (var path in Git.Run(o.Repo, "ls-files", "--others", "--exclude-standard").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal))
            {
                var full = Path.Combine(o.Repo, path);
                if (!File.Exists(full)) continue;
                var f = new FileDiff { Path = path, IsNew = true };
                var lines = File.ReadAllLines(full);
                for (var i = 1; i <= lines.Length; i++) { f.AddedLines.Add(i); f.AddedText[i] = lines[i - 1]; }
                diffs.Add(f);
            }
        }
        if (diffs.Count == 0)
            throw new UserError($"No changes between {baseRef} and {(o.Head ?? "the working tree")}.");

        var dirty = o.Head is null && Git.Run(o.Repo, "status", "--porcelain").Trim().Length > 0;
        var warnings = new List<string>();
        Console.Error.WriteLine($"reviewgraph: {diffs.Count} changed files against {baseRef} ({mergeBase[..8]}); loading solution…");
        var (solution, mode) = await Loader.LoadAsync(o, warnings);
        Console.Error.WriteLine($"reviewgraph: analysing ({mode})…");

        var analyzer = new Analyzer(solution, o, diffs, warnings, mode == "source-only");
        var (nodes, edges, steps, overview) = await analyzer.RunAsync();

        var title = o.Title ?? (Git.Run(o.Repo, "rev-list", "--count", $"{mergeBase}..HEAD").Trim() != "0"
            ? Git.Run(o.Repo, "log", "-1", "--format=%s", "HEAD").Trim()
            : "Uncommitted changes");
        var remote = Git.TryRun(o.Repo, "remote", "get-url", "origin");
        var repoName = remote is { Length: > 0 }
            ? System.Text.RegularExpressions.Regex.Replace(remote, @"^.*[:/]([^/:]+/[^/]+?)(\.git)?$", "$1")
            : Path.GetFileName(o.Repo);
        var branch = Git.Run(o.Repo, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        var doc = new ReviewDoc(
            new PrInfo(repoName, title, branch, $"{baseRef} @ {mergeBase[..8]}", headSha[..8] + (dirty ? " + working tree" : ""), mode),
            new StatsOut(diffs.Sum(d => d.Adds), diffs.Sum(d => d.Dels), diffs.Count,
                nodes.Count(n => n.Kind != "context"), nodes.Count(n => n.Kind == "context")),
            nodes, edges, steps,
            warnings.Distinct().ToList(),
            "reviewgraph 0.1");

        var json = JsonSerializer.Serialize(new { doc.Pr, doc.Stats, Overview = overview, doc.Nodes, doc.Edges, doc.Steps, doc.Warnings, doc.Generator }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.Default, // escapes < > & so the JSON is safe inside <script>
        });

        if (o.Json is not null) await File.WriteAllTextAsync(o.Json, json + "\n");
        await File.WriteAllTextAsync(o.Out, RenderHtml(json, title));
        Console.Error.WriteLine($"reviewgraph: {steps.Count} steps, {nodes.Count} cards, {edges.Count} edges → {o.Out}");
        foreach (var w in doc.Warnings) Console.Error.WriteLine("warning: " + w);
        return 0;
    }

    private static string RenderHtml(string json, string title)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("viewer.html")
            ?? throw new InvalidOperationException("viewer.html resource missing");
        using var r = new StreamReader(s);
        var html = r.ReadToEnd();
        const string marker = "/*REVIEW_DATA*/null";
        if (!html.Contains(marker)) throw new InvalidOperationException("viewer.html has no data marker");
        return html.Replace("__PR_TITLE__", System.Net.WebUtility.HtmlEncode(title)).Replace(marker, json);
    }
}
