using System.Text.Json;

namespace PackageTerms;

public static class Reporters
{
    public static void WriteConsole(IReadOnlyList<Finding> findings, int projectCount, TextWriter output, bool color)
    {
        output.WriteLine($"Scanned {projectCount} project(s).");
        output.WriteLine();

        if (findings.Count == 0)
        {
            output.WriteLine("No packages with changed terms found.");
            return;
        }

        foreach (Finding f in findings)
        {
            (string label, ConsoleColor colour) = f.Status switch
            {
                FindingStatus.Affected => (f.Entry.Kind == "maintenance-fee" ? "FEE APPLIES" : "COMMERCIAL", ConsoleColor.Red),
                FindingStatus.UpgradeRisk => ("UPGRADE RISK", ConsoleColor.Yellow),
                _ => ("NO ACTION", ConsoleColor.Green),
            };

            Write(output, color, colour, $"[{label}] ");
            output.WriteLine($"{f.PackageId} {f.Version} ({Reference(f)})");
            output.WriteLine($"  {Explain(f)}");
            if (f.Status != FindingStatus.TransitiveExempt)
            {
                output.WriteLine($"  Who pays: {f.Entry.WhoPays}");
                output.WriteLine($"  Last free version: {f.Entry.LastFreeVersion}");
                foreach (TermsAlternative alt in f.Entry.Alternatives)
                {
                    output.WriteLine($"  Alternative: {alt.Package}: {alt.Note}");
                }
            }

            output.WriteLine($"  Projects: {string.Join(", ", f.Projects)}");
            output.WriteLine($"  Source: {f.Entry.Source}");
            output.WriteLine();
        }

        output.WriteLine("This is information, not legal advice. Check each source and your own licence agreements.");
    }

    public static void WriteMarkdown(IReadOnlyList<Finding> findings, int projectCount, TextWriter output)
    {
        output.WriteLine("## PackageTerms report");
        output.WriteLine();
        output.WriteLine($"Scanned {projectCount} project(s).");
        output.WriteLine();
        if (findings.Count == 0)
        {
            output.WriteLine("No packages with changed terms found.");
            return;
        }

        output.WriteLine("| Status | Package | Reference | What changed | Last free version |");
        output.WriteLine("|---|---|---|---|---|");
        foreach (Finding f in findings)
        {
            string status = f.Status switch
            {
                FindingStatus.Affected => "🔴 Affected",
                FindingStatus.UpgradeRisk => "🟡 Upgrade risk",
                _ => "🟢 No action",
            };
            output.WriteLine($"| {status} | {f.PackageId} {f.Version} | {Reference(f)} | {Explain(f)} [Source]({f.Entry.Source}) | {f.Entry.LastFreeVersion} |");
        }

        output.WriteLine();
        output.WriteLine("_This is information, not legal advice._");
    }

    public static void WriteJson(IReadOnlyList<Finding> findings, int projectCount, TextWriter output)
    {
        var report = new
        {
            projectsScanned = projectCount,
            findings = findings.Select(f => new
            {
                status = f.Status.ToString(),
                package = f.PackageId,
                version = f.Version.ToNormalizedString(),
                direct = f.IsDirect,
                via = f.Via,
                projects = f.Projects,
                entry = f.Entry.Id,
                kind = f.Entry.Kind,
                affectedVersions = f.Entry.AffectedVersions,
                effectiveDate = f.Entry.EffectiveDate,
                lastFreeVersion = f.Entry.LastFreeVersion,
                summary = f.Entry.Summary,
                whoPays = f.Entry.WhoPays,
                source = f.Entry.Source,
                alternatives = f.Entry.Alternatives,
            }),
        };
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static string Reference(Finding f) =>
        f.IsDirect ? "direct" : f.Via.Count > 0 ? $"transitive via {string.Join(" → ", f.Via)}" : "transitive";

    private static string Explain(Finding f) => f.Status switch
    {
        FindingStatus.Affected => f.Entry.Summary,
        FindingStatus.UpgradeRisk =>
            $"Your version is still free, but {f.Entry.Name} {f.Entry.AffectedVersionsText} {(f.Entry.Kind == "maintenance-fee" ? "carries" : "is")} {(f.Entry.Kind == "maintenance-fee" ? "a maintenance fee" : "commercial")} (from {f.Entry.EffectiveDate}). Check the terms before upgrading.",
        _ => $"{f.Entry.Name}'s new terms cover direct references only, so this transitive reference is exempt.",
    };

    private static void Write(TextWriter output, bool color, ConsoleColor colour, string text)
    {
        if (color)
        {
            Console.ForegroundColor = colour;
            output.Write(text);
            Console.ResetColor();
        }
        else
        {
            output.Write(text);
        }
    }
}
