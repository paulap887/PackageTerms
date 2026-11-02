namespace PackageTerms;

public static class Evaluator
{
    /// <summary>
    /// Matches every resolved package against the dataset and merges results across projects and target frameworks,
    /// so each package version is reported once with the list of projects that use it.
    /// </summary>
    public static IReadOnlyList<Finding> Evaluate(IEnumerable<ProjectGraph> graphs, TermsDatabase db)
    {
        var findings = new Dictionary<(string Id, string Version, bool Direct, FindingStatus Status), Finding>();

        foreach (ProjectGraph graph in graphs)
        {
            foreach (ResolvedPackage package in graph.Packages)
            {
                TermsEntry? entry = db.Find(package.Id);
                if (entry is null || Classify(entry, package) is not { } status)
                {
                    continue;
                }

                var key = (package.Id.ToLowerInvariant(), package.Version.ToNormalizedString(), package.IsDirect, status);
                string project = Path.GetFileName(graph.ProjectPath);
                if (findings.TryGetValue(key, out Finding? existing))
                {
                    if (!existing.Projects.Contains(project))
                    {
                        findings[key] = existing with { Projects = [.. existing.Projects, project] };
                    }
                }
                else
                {
                    findings[key] = new Finding(entry, status, package.Id, package.Version, package.IsDirect, package.Via, [project]);
                }
            }
        }

        // An exemption for a transitive reference is misleading if another scanned project references the package directly.
        var directIds = new HashSet<string>(
            graphs.SelectMany(g => g.Packages).Where(p => p.IsDirect).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

        return findings.Values
            .Where(f => f.Status != FindingStatus.TransitiveExempt || !directIds.Contains(f.PackageId))
            .OrderBy(f => f.Status)
            .ThenBy(f => f.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(f => f.Version)
            .ToList();
    }

    public static FindingStatus? Classify(TermsEntry entry, ResolvedPackage package)
    {
        if (entry.DirectOnly && !package.IsDirect)
        {
            // Terms that cover direct references only. Report the exemption, unless the package only arrives
            // through another package of the same family that's already referenced directly (e.g. Polly → Polly.Core).
            bool viaSameFamily = package.Via.Any(entry.Matches);
            return viaSameFamily ? null : FindingStatus.TransitiveExempt;
        }

        if (entry.Range.Satisfies(package.Version))
        {
            return FindingStatus.Affected;
        }

        // A free version referenced directly: the next major upgrade would cross into the new terms.
        bool upgradeCrosses = entry.Range.MinVersion is { } min && package.Version < min;
        return package.IsDirect && upgradeCrosses ? FindingStatus.UpgradeRisk : null;
    }
}
