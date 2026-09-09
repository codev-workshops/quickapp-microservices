using System.Text;

namespace Identity.Migration.E2E.Tests.Harness;

public enum Outcome { Passed, Failed, Skipped }

public sealed record ResultEntry(string Id, string Title, Outcome Outcome, string Details);

public sealed record GateEntry(string Phase, string Tool, int ExitCode, int Expected, string Note);

/// <summary>Collects pass/fail per phase and edge case plus the db-diff / drain-outbox exit codes (no-data-loss gates).</summary>
public sealed class TestReport(string variant, string outputPath)
{
    private readonly List<ResultEntry> results = [];
    private readonly List<GateEntry> gates = [];
    private readonly List<string> log = [];
    private readonly Lock sync = new();

    public IReadOnlyList<GateEntry> Gates => gates;

    public void Record(string id, string title, Outcome outcome, string details = "")
    {
        lock (sync)
        {
            results.RemoveAll(r => r.Id == id);
            results.Add(new ResultEntry(id, title, outcome, details));
        }
    }

    public void Gate(string phase, string tool, int exitCode, int expected, string note = "")
    {
        lock (sync)
        {
            gates.Add(new GateEntry(phase, tool, exitCode, expected, note));
            log.Add($"[GATE] {phase} {tool} exit={exitCode} (expected {expected}) {note}");
        }
    }

    public void Log(string line)
    {
        lock (sync) log.Add(line);
    }

    public string Render()
    {
        lock (sync)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Identity strangler migration saga - E2E report");
            sb.AppendLine();
            sb.AppendLine($"Variant: {variant}");
            sb.AppendLine($"Generated: {DateTime.UtcNow:u}");
            sb.AppendLine();
            sb.AppendLine("## Results per phase / edge case");
            sb.AppendLine();
            sb.AppendLine("| Id | Title | Result | Details |");
            sb.AppendLine("|----|-------|--------|---------|");
            foreach (var r in results.OrderBy(r => SortKey(r.Id), StringComparer.Ordinal))
                sb.AppendLine($"| {r.Id} | {r.Title} | {r.Outcome.ToString().ToUpperInvariant()} | {Escape(r.Details)} |");

            sb.AppendLine();
            sb.AppendLine("## No-data-loss gates (exit codes)");
            sb.AppendLine();
            sb.AppendLine("| Phase | Tool | Exit code | Expected | Note |");
            sb.AppendLine("|-------|------|-----------|----------|------|");
            foreach (var g in gates)
                sb.AppendLine($"| {g.Phase} | {g.Tool} | {g.ExitCode} | {g.Expected} | {Escape(g.Note)} |");

            var passed = results.Count(r => r.Outcome == Outcome.Passed);
            var failed = results.Count(r => r.Outcome == Outcome.Failed);
            var skipped = results.Count(r => r.Outcome == Outcome.Skipped);
            sb.AppendLine();
            sb.AppendLine($"Summary: {passed} passed, {failed} failed, {skipped} skipped.");

            if (log.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Log");
                sb.AppendLine();
                sb.AppendLine("```");
                foreach (var line in log) sb.AppendLine(line);
                sb.AppendLine("```");
            }

            return sb.ToString();
        }
    }

    public string Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, Render());
        return outputPath;
    }

    // P1a..P7c first, then E1..E7, then Z.
    private static string SortKey(string id) => id[0] switch
    {
        'P' => "0" + id,
        'E' => "1" + id,
        _ => "2" + id
    };

    private static string Escape(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
}
