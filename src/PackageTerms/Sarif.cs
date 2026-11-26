using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PackageTerms;

/// <summary>
/// SARIF 2.1.0 output, for GitHub code scanning and Azure DevOps. Each finding points at the project file,
/// on the PackageReference line when the package is referenced directly.
/// </summary>
public static partial class Sarif
{
    private sealed record Rule(string Id, string Name, string ShortDescription, string FullDescription, string Level);

    private static readonly Rule Commercial = new("PT001", "CommercialLicence",
        "Package version needs a commercial licence",
        "The resolved version of this package is covered by a commercial licence or a licence that restricts commercial use.", "error");

    private static readonly Rule MaintenanceFee = new("PT002", "MaintenanceFee",
        "Package version carries a maintenance fee",
        "The resolved version of this package is covered by a maintenance fee for commercial users.", "error");

    private static readonly Rule UpgradeRisk = new("PT003", "UpgradeRisk",
        "Upgrading this package crosses into changed terms",
        "The referenced version is still free, but a newer major version is covered by changed terms. Check before upgrading.", "warning");

    private static readonly Rule TransitiveExempt = new("PT004", "TransitiveExempt",
        "Package is covered, but transitive references are exempt",
        "The package's new terms cover direct references only, and this reference is transitive.", "note");

    private static readonly Rule LicenceChanged = new("PT101", "LicenceChanged",
        "Package licence changed",
        "A package version declares a licence that packages.licenses.json has never recorded for this package.", "error");

    private static readonly Rule TermsChanged = new("PT102", "TermsChanged",
        "Package terms changed",
        "A package version is now covered by changed terms (for example a maintenance fee), even though its licence label is the same.", "error");

    private static readonly Rule NewPackageWithTerms = new("PT103", "NewPackageWithChangedTerms",
        "New package covered by changed terms",
        "A package that isn't in packages.licenses.json yet is covered by changed terms.", "error");

    private static readonly Rule[] ScanRules = [Commercial, MaintenanceFee, UpgradeRisk, TransitiveExempt];
    private static readonly Rule[] LockRules = [LicenceChanged, TermsChanged, NewPackageWithTerms];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void WriteScan(IReadOnlyList<Finding> findings, TextWriter output, string baseDirectory)
    {
        var results = new List<object>();
        foreach (Finding f in findings)
        {
            Rule rule = f.Status switch
            {
                FindingStatus.Affected => f.Entry.Kind == "maintenance-fee" ? MaintenanceFee : Commercial,
                FindingStatus.UpgradeRisk => UpgradeRisk,
                _ => TransitiveExempt,
            };

            string reference = f.IsDirect ? "direct" : f.Via.Count > 0 ? $"transitive via {string.Join(" → ", f.Via)}" : "transitive";
            string text = f.Status switch
            {
                FindingStatus.Affected => $"{f.PackageId} {f.Version} ({reference}): {f.Entry.Summary} Last free version: {f.LastFreeVersion}. Source: {f.Entry.Source}",
                FindingStatus.UpgradeRisk => $"{f.PackageId} {f.Version} is still free, but {f.PackageId} {f.AffectedVersionsText} is covered by changed terms (from {f.Entry.EffectiveDate}). Source: {f.Entry.Source}",
                _ => $"{f.PackageId} {f.Version} ({reference}): {f.Entry.Name}'s new terms cover direct references only, so this reference is exempt. Source: {f.Entry.Source}",
            };

            results.Add(new
            {
                ruleId = rule.Id,
                level = rule.Level,
                message = new { text },
                locations = f.ProjectPaths.Select(p => Location(p, f.IsDirect ? f.PackageId : null, baseDirectory)).ToArray(),
                partialFingerprints = new Dictionary<string, string> { ["packageTerms/v1"] = $"{rule.Id}:{f.PackageId.ToLowerInvariant()}:{f.Version.ToNormalizedString()}" },
                properties = new
                {
                    package = f.PackageId,
                    version = f.Version.ToNormalizedString(),
                    direct = f.IsDirect,
                    entry = f.Entry.Id,
                    lastFreeVersion = f.LastFreeVersion,
                    source = f.Entry.Source,
                },
            });
        }

        Write(output, ScanRules, results);
    }

    public static void WriteLockChanges(IReadOnlyList<LockChange> changes, string lockfilePath, TextWriter output, string baseDirectory)
    {
        var results = new List<object>();
        foreach (LockChange c in changes.Where(c => c.NeedsReview))
        {
            (Rule rule, string text) = c.Kind switch
            {
                LockChangeKind.LicenseChanged => (LicenceChanged,
                    $"{c.Id} {string.Join(", ", c.OldVersions)} → {string.Join(", ", c.NewVersions)}: licence {string.Join(", ", c.OldLicenses)} → {string.Join(", ", c.NewLicenses)}" +
                    (c.NewTerms is not null && c.NewTerms != c.OldTerms ? $"; now covered by changed terms ({c.NewTerms})." : ".")),
                LockChangeKind.TermsChanged => (TermsChanged,
                    $"{c.Id} {string.Join(", ", c.OldVersions)} → {string.Join(", ", c.NewVersions)}: licence unchanged ({string.Join(", ", c.NewLicenses)}), but now covered by changed terms ({c.NewTerms})."),
                _ => (NewPackageWithTerms,
                    $"New package {c.Id} {string.Join(", ", c.NewVersions)} ({string.Join(", ", c.NewLicenses)}) is covered by changed terms ({c.NewTerms})."),
            };

            results.Add(new
            {
                ruleId = rule.Id,
                level = rule.Level,
                message = new { text },
                locations = new[] { Location(lockfilePath, null, baseDirectory) },
                partialFingerprints = new Dictionary<string, string> { ["packageTerms/v1"] = $"{rule.Id}:{c.Id.ToLowerInvariant()}:{string.Join(",", c.NewVersions)}" },
                properties = new { package = c.Id, oldVersions = c.OldVersions, newVersions = c.NewVersions, oldLicenses = c.OldLicenses, newLicenses = c.NewLicenses, c.OldTerms, c.NewTerms },
            });
        }

        Write(output, LockRules, results);
    }

    private static void Write(TextWriter output, Rule[] rules, List<object> results)
    {
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        var log = new Dictionary<string, object>
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = "PackageTerms",
                            version,
                            semanticVersion = version,
                            informationUri = "https://github.com/paulap887/PackageTerms",
                            rules = rules.Select(r => new
                            {
                                id = r.Id,
                                name = r.Name,
                                shortDescription = new { text = r.ShortDescription },
                                fullDescription = new { text = r.FullDescription + " This is information, not legal advice." },
                                helpUri = "https://github.com/paulap887/PackageTerms#readme",
                                defaultConfiguration = new { level = r.Level },
                            }),
                        },
                    },
                    results,
                },
            },
        };
        output.WriteLine(JsonSerializer.Serialize(log, JsonOptions));
    }

    private static object Location(string path, string? packageId, string baseDirectory)
    {
        string relative = Path.GetRelativePath(baseDirectory, path).Replace('\\', '/');
        bool inside = !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        int? line = packageId is null ? null : FindReferenceLine(path, packageId);

        return new
        {
            physicalLocation = new
            {
                artifactLocation = inside
                    ? (object)new { uri = Uri.EscapeDataString(relative).Replace("%2F", "/"), uriBaseId = "%SRCROOT%" }
                    : new { uri = new Uri(path).AbsoluteUri },
                region = line is { } l ? new { startLine = l } : null,
            },
        };
    }

    /// <summary>The 1-based line of the PackageReference for this package in a project file, if there is one.</summary>
    public static int? FindReferenceLine(string projectPath, string packageId)
    {
        if (!File.Exists(projectPath))
        {
            return null;
        }

        string[] lines = File.ReadAllLines(projectPath);
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = IncludeAttribute().Match(lines[i]);
            if (m.Success && string.Equals(m.Groups["id"].Value, packageId, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return null;
    }

    [GeneratedRegex(@"<PackageReference\b[^>]*\b(?:Include|Update)\s*=\s*""(?<id>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex IncludeAttribute();
}
