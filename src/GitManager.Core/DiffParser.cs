using System.Text.RegularExpressions;

namespace GitManager.Core;

public enum DiffKind { Context, Addition, Deletion, Hunk, Information }
public sealed record DiffLine(DiffKind Kind, int? OldNumber, int? NewNumber, string Text);
public sealed record DiffPair(DiffLine? Left, DiffLine? Right, bool IsHeader = false);
public sealed record DiffDocument(string Patch, IReadOnlyList<DiffLine> Lines, IReadOnlyList<DiffPair> Pairs,
    int Additions, int Deletions);

public static partial class DiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkPattern();

    public static DiffDocument Parse(string patch)
    {
        var lines = new List<DiffLine>();
        var oldNumber = 0;
        var newNumber = 0;
        var inHunk = false;
        var added = 0;
        var removed = 0;
        foreach (var raw in patch.Replace("\r\n", "\n").Split('\n'))
        {
            var match = HunkPattern().Match(raw);
            if (match.Success)
            {
                oldNumber = int.Parse(match.Groups[1].Value);
                newNumber = int.Parse(match.Groups[2].Value);
                inHunk = true;
                lines.Add(new DiffLine(DiffKind.Hunk, null, null, raw));
            }
            else if (raw.StartsWith("diff --git ", StringComparison.Ordinal)) inHunk = false;
            else if (inHunk && raw.Length > 0)
            {
                switch (raw[0])
                {
                    case '+': lines.Add(new DiffLine(DiffKind.Addition, null, newNumber++, raw[1..])); added++; break;
                    case '-': lines.Add(new DiffLine(DiffKind.Deletion, oldNumber++, null, raw[1..])); removed++; break;
                    case ' ': lines.Add(new DiffLine(DiffKind.Context, oldNumber++, newNumber++, raw[1..])); break;
                    default: lines.Add(new DiffLine(DiffKind.Information, null, null, raw)); break;
                }
            }
            else if (raw.StartsWith("Binary files ", StringComparison.Ordinal) || raw.StartsWith("Submodule ", StringComparison.Ordinal)
                || raw.StartsWith("rename ", StringComparison.Ordinal) || raw.StartsWith("old mode ", StringComparison.Ordinal)
                || raw.StartsWith("new mode ", StringComparison.Ordinal) || raw.StartsWith("similarity index ", StringComparison.Ordinal))
                lines.Add(new DiffLine(DiffKind.Information, null, null, raw));
        }
        var pairs = new List<DiffPair>();
        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i];
            if (line.Kind is DiffKind.Addition or DiffKind.Deletion)
            {
                var deletions = new List<DiffLine>();
                var additions = new List<DiffLine>();
                while (i < lines.Count && lines[i].Kind is DiffKind.Addition or DiffKind.Deletion)
                {
                    if (lines[i].Kind == DiffKind.Deletion) deletions.Add(lines[i]); else additions.Add(lines[i]);
                    i++;
                }
                for (var j = 0; j < Math.Max(deletions.Count, additions.Count); j++)
                    pairs.Add(new DiffPair(j < deletions.Count ? deletions[j] : null, j < additions.Count ? additions[j] : null));
            }
            else
            {
                pairs.Add(new DiffPair(line, line, line.Kind is DiffKind.Hunk or DiffKind.Information));
                i++;
            }
        }
        return new DiffDocument(patch, lines, pairs, added, removed);
    }
}
