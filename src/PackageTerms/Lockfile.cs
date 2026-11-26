using System.Text;
using System.Text.Json;
using NuGet.Versioning;

namespace PackageTerms;

/// <summary>One resolved package version as recorded in packages.licenses.json.</summary>
/// <param name="Terms">The dataset entry id when this version is covered by changed terms that apply to this solution.</param>
public sealed record LockedPackage(string Id, NuGetVersion Version, PackageLicense License, string? Terms);

public enum LockChangeKind
{
    /// <summary>A new version declares a licence the lockfile has never recorded for this package.</summary>
    LicenseChanged,

    /// <summary>The licence label is the same, but the version is now covered by changed terms (for example Polly's fee).</summary>
    TermsChanged,

    /// <summary>A package the lockfile doesn't know yet. Needs review only when it's covered by changed terms.</summary>
    Added,

    /// <summary>A package that's no longer resolved.</summary>
    Removed,

    /// <summary>A different version with a licence and terms already in the lockfile.</summary>
    Updated,
}

public sealed record LockChange(
    LockChangeKind Kind,
    string Id,
    IReadOnlyList<string> OldVersions,
    IReadOnlyList<string> NewVersions,
    IReadOnlyList<string> OldLicenses,
    IReadOnlyList<string> NewLicenses,
    string? OldTerms,
    string? NewTerms)
{
    /// <summary>Changes that mean someone should review the new terms before merging.</summary>
    public bool NeedsReview =>
        Kind is LockChangeKind.LicenseChanged or LockChangeKind.TermsChanged ||
        (Kind == LockChangeKind.Added && NewTerms is not null);
}

/// <summary>
/// packages.licenses.json: the licence each resolved package version declares, plus any changed terms from the dataset.
/// Commit it, then run "package-terms lock --check" in CI to catch upgrades that change a licence.
/// </summary>
public sealed class Lockfile
{
    public const string DefaultFileName = "packages.licenses.json";
    public const int FormatVersion = 1;

    public IReadOnlyList<LockedPackage> Packages { get; }

    public Lockfile(IEnumerable<LockedPackage> packages)
    {
        Packages = packages
            .DistinctBy(p => (p.Id.ToLowerInvariant(), p.Version.ToNormalizedString()))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version)
            .ToList();
    }

    /// <summary>Builds a lockfile from restore output, reading each package's .nuspec once.</summary>
    public static Lockfile Create(IReadOnlyList<ProjectGraph> graphs, TermsDatabase db, Func<string?, PackageLicense>? readLicense = null)
    {
        readLicense ??= LicenseReader.Read;

        // Only versions that are actually affected in this solution carry terms; exempt transitive references don't.
        var terms = Evaluator.Evaluate(graphs, db)
            .Where(f => f.Status == FindingStatus.Affected)
            .GroupBy(f => (f.PackageId.ToLowerInvariant(), f.Version.ToNormalizedString()))
            .ToDictionary(g => g.Key, g => g.First().Entry.Id);

        var packages = graphs
            .SelectMany(g => g.Packages)
            .GroupBy(p => (p.Id.ToLowerInvariant(), p.Version.ToNormalizedString()))
            .Select(g =>
            {
                ResolvedPackage p = g.FirstOrDefault(x => x.Directory is not null) ?? g.First();
                return new LockedPackage(p.Id, p.Version, readLicense(p.Directory), terms.GetValueOrDefault(g.Key));
            });

        return new Lockfile(packages);
    }

    public static Lockfile Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    public static Lockfile Load(Stream json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("lockfileVersion", out JsonElement v) || v.GetInt32() != FormatVersion ||
            !root.TryGetProperty("packages", out JsonElement packages))
        {
            throw new InvalidDataException($"Unsupported {DefaultFileName}: expected \"lockfileVersion\": {FormatVersion} and a \"packages\" object.");
        }

        var list = new List<LockedPackage>();
        foreach (JsonProperty id in packages.EnumerateObject())
        {
            foreach (JsonProperty version in id.Value.EnumerateObject())
            {
                string license = version.Value.TryGetProperty("license", out JsonElement l) ? l.GetString() ?? "" : "";
                string? sha = version.Value.TryGetProperty("licenseSha256", out JsonElement s) ? s.GetString() : null;
                string? terms = version.Value.TryGetProperty("terms", out JsonElement t) ? t.GetString() : null;
                list.Add(new LockedPackage(id.Name, NuGetVersion.Parse(version.Name), ParseLicense(license, sha), terms));
            }
        }

        return new Lockfile(list);
    }

    /// <summary>Writes stable, sorted JSON with LF line endings, so the file diffs cleanly in pull requests.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("lockfileVersion", FormatVersion);
            writer.WriteStartObject("packages");
            foreach (IGrouping<string, LockedPackage> id in Packages.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject(id.Key);
                foreach (LockedPackage p in id)
                {
                    writer.WriteStartObject(p.Version.ToNormalizedString());
                    writer.WriteString("license", p.License.ToString());
                    if (p.License.Sha256 is not null)
                    {
                        writer.WriteString("licenseSha256", p.License.Sha256);
                    }

                    if (p.Terms is not null)
                    {
                        writer.WriteString("terms", p.Terms);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    /// <summary>
    /// Compares a fresh lockfile with the committed one, package by package. A new version only counts as a licence
    /// change when its licence isn't one already recorded for that package, so ordinary upgrades stay quiet.
    /// </summary>
    public static IReadOnlyList<LockChange> Compare(Lockfile committed, Lockfile current)
    {
        var oldById = committed.Packages.ToLookup(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var newById = current.Packages.ToLookup(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var ids = oldById.Select(g => g.Key).Concat(newById.Select(g => g.Key)).Distinct(StringComparer.OrdinalIgnoreCase);

        var changes = new List<LockChange>();
        foreach (string id in ids)
        {
            var before = oldById[id].ToList();
            var after = newById[id].ToList();
            string displayId = after.FirstOrDefault()?.Id ?? before[0].Id;

            LockChange Change(LockChangeKind kind) => new(
                kind,
                displayId,
                before.Select(p => p.Version.ToNormalizedString()).ToList(),
                after.Select(p => p.Version.ToNormalizedString()).ToList(),
                before.Select(p => p.License.ToString()).Distinct().ToList(),
                after.Select(p => p.License.ToString()).Distinct().ToList(),
                before.Select(p => p.Terms).FirstOrDefault(t => t is not null),
                after.Select(p => p.Terms).FirstOrDefault(t => t is not null));

            if (before.Count == 0)
            {
                changes.Add(Change(LockChangeKind.Added));
                continue;
            }

            if (after.Count == 0)
            {
                changes.Add(Change(LockChangeKind.Removed));
                continue;
            }

            // An unknown licence means the package folder wasn't available; that's not evidence of a change.
            var knownLicenses = before.Select(p => p.License).ToHashSet();
            bool licenseChanged = after.Any(p => !p.License.IsUnknown && !knownLicenses.Contains(p.License));
            var knownTerms = before.Select(p => p.Terms).ToHashSet();
            bool termsChanged = after.Any(p => p.Terms is not null && !knownTerms.Contains(p.Terms));
            bool versionsChanged = !before.Select(p => p.Version).ToHashSet().SetEquals(after.Select(p => p.Version));

            if (licenseChanged)
            {
                changes.Add(Change(LockChangeKind.LicenseChanged));
            }
            else if (termsChanged)
            {
                changes.Add(Change(LockChangeKind.TermsChanged));
            }
            else if (versionsChanged)
            {
                changes.Add(Change(LockChangeKind.Updated));
            }
        }

        return changes
            .OrderBy(c => c.Kind)
            .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PackageLicense ParseLicense(string text, string? sha) => text switch
    {
        "" or "unknown" => PackageLicense.Unknown,
        _ when text.StartsWith("file:", StringComparison.Ordinal) => new PackageLicense("file", text[5..], sha),
        _ when text.StartsWith("url:", StringComparison.Ordinal) => new PackageLicense("url", text[4..]),
        _ => new PackageLicense("expression", text),
    };
}
