using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ReviewGraph;

/// <summary>A removed line, remembered with its line number on the base side.</summary>
public sealed record DeletedLine(int OldLine, string Text);

/// <summary>Everything the diff says about one file, in head-side line numbers.</summary>
public sealed class FileDiff
{
    public required string Path { get; init; }          // repo-relative, forward slashes, head side
    public string? OldPath { get; set; }
    public bool IsNew { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsBinary { get; set; }
    public SortedSet<int> AddedLines { get; } = new();
    public Dictionary<int, string> AddedText { get; } = new();

    /// <summary>Deleted lines keyed by the head-side line they sat in front of.</summary>
    public SortedDictionary<int, List<DeletedLine>> DeletedBefore { get; } = new();

    public int Adds => AddedLines.Count;
    public int Dels => DeletedBefore.Values.Sum(v => v.Count);
}

public sealed class UserError(string message) : Exception(message);

public static partial class Git
{
    public static string Run(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=off");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new UserError("Could not start git.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new UserError($"git {string.Join(' ', args)} failed: {stderr.Result.Trim()}");
        return stdout.Result;
    }

    public static string? TryRun(string repo, params string[] args)
    {
        try { return Run(repo, args).Trim(); } catch (UserError) { return null; }
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    /// <summary>
    /// Parses `git diff --unified=0`. Zero context keeps the parse exact: every '+' line is an
    /// added head-side line, every '-' line is removed, and nothing else needs guessing.
    /// </summary>
    public static List<FileDiff> Parse(string diff)
    {
        var files = new List<FileDiff>();
        FileDiff? cur = null;
        string? minusPath = null;
        int oldLine = 0, newLine = 0, insertAt = 0;

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("diff --git "))
            {
                cur = null; minusPath = null;
                continue;
            }
            if (line.StartsWith("--- "))
            {
                minusPath = line == "--- /dev/null" ? null : StripPrefix(line[4..]);
                continue;
            }
            if (line.StartsWith("+++ "))
            {
                var plus = line == "+++ /dev/null" ? null : StripPrefix(line[4..]);
                cur = new FileDiff
                {
                    Path = plus ?? minusPath ?? "",
                    OldPath = minusPath,
                    IsNew = minusPath is null,
                    IsDeleted = plus is null,
                };
                files.Add(cur);
                continue;
            }
            if (line.StartsWith("Binary files "))
            {
                var m = Regex.Match(line, @"^Binary files (?:a/)?(.+?) and (?:b/)?(.+?) differ$");
                if (m.Success)
                    files.Add(new FileDiff { Path = m.Groups[2].Value == "/dev/null" ? m.Groups[1].Value : m.Groups[2].Value, IsBinary = true });
                continue;
            }
            if (cur is null) continue;

            var h = HunkHeader().Match(line);
            if (h.Success)
            {
                oldLine = int.Parse(h.Groups[1].Value);
                newLine = int.Parse(h.Groups[3].Value);
                var newCount = h.Groups[4].Success ? int.Parse(h.Groups[4].Value) : 1;
                // With zero context, a pure deletion reports the head line *before* the gap.
                insertAt = newCount == 0 ? newLine + 1 : newLine;
                continue;
            }
            if (line.StartsWith('-'))
            {
                if (!cur.DeletedBefore.TryGetValue(insertAt, out var list))
                    cur.DeletedBefore[insertAt] = list = new List<DeletedLine>();
                list.Add(new DeletedLine(oldLine++, line[1..]));
            }
            else if (line.StartsWith('+'))
            {
                cur.AddedLines.Add(newLine);
                cur.AddedText[newLine] = line[1..];
                newLine++;
            }
        }
        return files.Where(f => f.Path.Length > 0).ToList();
    }

    private static string StripPrefix(string p)
    {
        p = p.TrimEnd('\t');
        return p.StartsWith("a/") || p.StartsWith("b/") ? p[2..] : p;
    }
}
