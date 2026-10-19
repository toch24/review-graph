using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace ReviewGraph;

public static class Loader
{
    /// <summary>
    /// Loads the solution through MSBuild (full semantic accuracy, NuGet references resolved).
    /// When that is not possible, falls back to compiling every .cs file in the repo against the
    /// installed .NET and ASP.NET Core reference assemblies. Calls between your own types still
    /// resolve in that mode; only types from NuGet packages go unresolved.
    /// </summary>
    public static async Task<(Solution Solution, string Mode)> LoadAsync(Options o, List<string> warnings)
    {
        if (!o.SourceOnly)
        {
            try
            {
                var sln = await LoadMsBuildAsync(o, warnings);
                if (sln.Projects.Any(p => p.Language == LanguageNames.CSharp && p.Documents.Any()))
                    return (sln, "msbuild");
                warnings.Add("MSBuild loaded no C# files, so the analysis fell back to source-only mode.");
            }
            catch (Exception e)
            {
                warnings.Add($"MSBuild could not load the solution ({e.GetType().Name}: {e.Message}). The analysis fell back to source-only mode.");
            }
        }
        return (LoadSourceOnly(o.Repo), "source-only");
    }

    private static async Task<Solution> LoadMsBuildAsync(Options o, List<string> warnings)
    {
        var ws = MSBuildWorkspace.Create();
        ws.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                warnings.Add("MSBuild: " + e.Diagnostic.Message);
        };

        var target = o.Solution ?? Directory.EnumerateFiles(o.Repo, "*.sln").Order(StringComparer.Ordinal).FirstOrDefault();
        if (target is not null && target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return await ws.OpenSolutionAsync(target);

        var projects = target is not null
            ? new[] { target }
            : Directory.EnumerateFiles(o.Repo, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p)).Order(StringComparer.Ordinal).ToArray();
        foreach (var p in projects)
        {
            var full = Path.GetFullPath(p);
            if (ws.CurrentSolution.Projects.Any(x => string.Equals(x.FilePath, full, StringComparison.Ordinal))) continue;
            await ws.OpenProjectAsync(full);
        }
        return ws.CurrentSolution;
    }

    private static Solution LoadSourceOnly(string repo)
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = Directory.EnumerateFiles(runtimeDir, "*.dll").ToList();

        // ASP.NET Core shared framework sits next to Microsoft.NETCore.App.
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir))!;
        var aspDir = Path.Combine(shared, "Microsoft.AspNetCore.App");
        var hasAsp = false;
        if (Directory.Exists(aspDir))
        {
            var ver = Path.GetFileName(runtimeDir);
            var dir = Directory.Exists(Path.Combine(aspDir, ver))
                ? Path.Combine(aspDir, ver)
                : Directory.EnumerateDirectories(aspDir).Order(StringComparer.Ordinal).LastOrDefault();
            if (dir is not null) { refs.AddRange(Directory.EnumerateFiles(dir, "*.dll")); hasAsp = true; }
        }

        var metadata = refs
            .Where(IsManagedAssembly)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Order(StringComparer.Ordinal)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(pid, VersionStamp.Default, "Sources", "Sources", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: metadata);
        var sln = ws.CurrentSolution.AddProject(info);

        // Mirror <ImplicitUsings>, which most modern projects turn on.
        var usings = new List<string> { "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks" };
        if (hasAsp)
            usings.AddRange(new[] { "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing",
                "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging" });
        sln = sln.AddDocument(DocumentId.CreateNewId(pid), "__ImplicitUsings.g.cs",
            SourceText.From(string.Concat(usings.Select(u => $"global using global::{u};\n"))));

        foreach (var file in Directory.EnumerateFiles(repo, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !IsBuildOutput(f)).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(repo, file);
            sln = sln.AddDocument(DocumentId.CreateNewId(pid), rel, SourceText.From(File.ReadAllText(file)), filePath: Path.GetFullPath(file));
        }
        return sln;
    }

    public static bool IsBuildOutput(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/bin/") || p.Contains("/obj/") || p.Contains("/.git/") || p.Contains("/node_modules/");
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            return pe.HasMetadata;
        }
        catch { return false; }
    }
}
