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
                output.WriteLine($"  Last free version: {f.LastFreeVersion}");
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
            output.WriteLine($"| {status} | {f.PackageId} {f.Version} | {Reference(f)} | {Explain(f)} [Source]({f.Entry.Source}) | {f.LastFreeVersion} |");
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
                affectedVersions = f.AffectedVersions,
                effectiveDate = f.Entry.EffectiveDate,
                lastFreeVersion = f.LastFreeVersion,
                summary = f.Entry.Summary,
                whoPays = f.Entry.WhoPays,
                source = f.Entry.Source,
                alternatives = f.Entry.Alternatives,
            }),
        };
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    public static void WriteLockChangesConsole(IReadOnlyList<LockChange> changes, string lockfileName, TextWriter output, bool color, bool updating = false)
    {
        if (changes.Count == 0)
        {
            output.WriteLine($"No changes: every resolved package matches {lockfileName}.");
            return;
        }

        foreach (LockChange c in changes)
        {
            (string label, ConsoleColor colour) = c.Kind switch
            {
                LockChangeKind.LicenseChanged => ("LICENCE CHANGED", ConsoleColor.Red),
                LockChangeKind.TermsChanged => ("TERMS CHANGED", ConsoleColor.Red),
                LockChangeKind.Added => ("NEW PACKAGE", c.NeedsReview ? ConsoleColor.Red : ConsoleColor.DarkGray),
                LockChangeKind.Removed => ("REMOVED", ConsoleColor.DarkGray),
                _ => ("UPDATED", ConsoleColor.DarkGray),
            };

            Write(output, color, colour, $"[{label}] ");
            output.WriteLine($"{c.Id} {Versions(c)}");
            foreach (string line in Details(c))
            {
                output.WriteLine($"  {line}");
            }
        }

        output.WriteLine();
        int review = changes.Count(c => c.NeedsReview);
        if (updating)
        {
            output.WriteLine(review == 0
                ? $"No licence or terms changes. Commit {lockfileName} with your package changes."
                : $"{review} change(s) need review. Check the new terms before you commit {lockfileName}.");
            return;
        }

        output.WriteLine(review == 0
            ? $"Nothing needs review. Run \"package-terms lock\" to bring {lockfileName} up to date."
            : $"{review} change(s) need review. Check the new terms, then run \"package-terms lock\" and commit {lockfileName}.");
        output.WriteLine("This is information, not legal advice.");
    }

    public static void WriteLockChangesMarkdown(IReadOnlyList<LockChange> changes, string lockfileName, TextWriter output)
    {
        output.WriteLine("## PackageTerms licence check");
        output.WriteLine();
        if (changes.Count == 0)
        {
            output.WriteLine($"No changes: every resolved package matches `{lockfileName}`.");
            return;
        }

        output.WriteLine("| Change | Package | Versions | Licence | Terms |");
        output.WriteLine("|---|---|---|---|---|");
        foreach (LockChange c in changes)
        {
            string kind = c.Kind switch
            {
                LockChangeKind.LicenseChanged => "🔴 Licence changed",
                LockChangeKind.TermsChanged => "🔴 Terms changed",
                LockChangeKind.Added => c.NeedsReview ? "🔴 New package" : "⚪ New package",
                LockChangeKind.Removed => "⚪ Removed",
                _ => "⚪ Updated",
            };
            output.WriteLine($"| {kind} | {c.Id} | {Versions(c)} | {Arrow(c.OldLicenses, c.NewLicenses)} | {Arrow(c.OldTerms, c.NewTerms)} |");
        }

        output.WriteLine();
        output.WriteLine("_This is information, not legal advice._");
    }

    public static void WriteLockChangesJson(IReadOnlyList<LockChange> changes, TextWriter output)
    {
        var report = new
        {
            needsReview = changes.Count(c => c.NeedsReview),
            changes = changes.Select(c => new
            {
                kind = c.Kind.ToString(),
                package = c.Id,
                c.NeedsReview,
                c.OldVersions,
                c.NewVersions,
                c.OldLicenses,
                c.NewLicenses,
                c.OldTerms,
                c.NewTerms,
            }),
        };
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static string Versions(LockChange c) => c.Kind switch
    {
        LockChangeKind.Added => string.Join(", ", c.NewVersions),
        LockChangeKind.Removed => string.Join(", ", c.OldVersions),
        _ => $"{string.Join(", ", c.OldVersions)} → {string.Join(", ", c.NewVersions)}",
    };

    private static IEnumerable<string> Details(LockChange c)
    {
        switch (c.Kind)
        {
            case LockChangeKind.LicenseChanged:
                yield return $"Licence: {string.Join(", ", c.OldLicenses)} → {string.Join(", ", c.NewLicenses)}";
                if (c.NewTerms is not null && c.NewTerms != c.OldTerms)
                {
                    yield return $"Now covered by changed terms: {c.NewTerms} (run \"package-terms scan\" for details)";
                }

                break;
            case LockChangeKind.TermsChanged:
                yield return $"Licence unchanged ({string.Join(", ", c.NewLicenses)}), but this version is covered by changed terms: {c.NewTerms} (run \"package-terms scan\" for details)";
                break;
            case LockChangeKind.Added:
                yield return $"Licence: {string.Join(", ", c.NewLicenses)}{(c.NewTerms is null ? "" : $"; covered by changed terms: {c.NewTerms}")}";
                break;
        }
    }

    private static string Arrow(IReadOnlyList<string> before, IReadOnlyList<string> after) =>
        Arrow(before.Count == 0 ? null : string.Join(", ", before), after.Count == 0 ? null : string.Join(", ", after));

    private static string Arrow(string? before, string? after) =>
        before == after || before is null ? after ?? "" : $"{before} → {after ?? "none"}";

    private static string Reference(Finding f) =>
        f.IsDirect ? "direct" : f.Via.Count > 0 ? $"transitive via {string.Join(" → ", f.Via)}" : "transitive";

    private static string Explain(Finding f) => f.Status switch
    {
        FindingStatus.Affected => f.Entry.Summary,
        FindingStatus.UpgradeRisk =>
            $"Your version is still free, but {f.PackageId} {f.AffectedVersionsText} {(f.Entry.Kind == "maintenance-fee" ? "carries" : "is")} {(f.Entry.Kind == "maintenance-fee" ? "a maintenance fee" : "commercial")} (from {f.Entry.EffectiveDate}). Check the terms before upgrading.",
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
