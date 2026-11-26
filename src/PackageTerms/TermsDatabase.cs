using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace PackageTerms;

public sealed class TermsAlternative
{
    public string Package { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>Version details for one package of an entry whose packages changed terms at different versions.</summary>
public sealed class PackageVersionOverride
{
    public string AffectedVersions { get; init; } = "";
    public string LastFreeVersion { get; init; } = "";

    [JsonIgnore]
    public VersionRange Range => _range ??= VersionRange.Parse(AffectedVersions);
    private VersionRange? _range;
}

public sealed class TermsEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] Packages { get; init; } = [];
    public string[] PackagePrefixes { get; init; } = [];

    /// <summary>"commercial" or "maintenance-fee".</summary>
    public string Kind { get; init; } = "";

    /// <summary>NuGet version range covered by the new terms, for example "[13.0.0-0, )".</summary>
    public string AffectedVersions { get; init; } = "";
    public string EffectiveDate { get; init; } = "";
    public string LastFreeVersion { get; init; } = "";

    /// <summary>"direct" when only direct references are covered, otherwise "any".</summary>
    public string AppliesTo { get; init; } = "any";
    public string Summary { get; init; } = "";
    public string WhoPays { get; init; } = "";
    public string Source { get; init; } = "";
    public TermsAlternative[] Alternatives { get; init; } = [];

    /// <summary>
    /// Optional per-package versions, for families where each package changed terms at its own major version
    /// (for example SixLabors.ImageSharp 3.0 but SixLabors.Fonts 2.0). Packages not listed use the entry's values.
    /// </summary>
    public Dictionary<string, PackageVersionOverride> PackageVersions { get; init; } = [];

    [JsonIgnore]
    public VersionRange Range => _range ??= VersionRange.Parse(AffectedVersions);
    private VersionRange? _range;

    /// <summary>Readable form of <see cref="AffectedVersions"/>, for example "15.0.0 and later".</summary>
    public string AffectedVersionsText => Describe(Range, AffectedVersions);

    public VersionRange RangeFor(string packageId) => Override(packageId)?.Range ?? Range;

    public string LastFreeVersionFor(string packageId) => Override(packageId)?.LastFreeVersion ?? LastFreeVersion;

    public string AffectedVersionsFor(string packageId) => Override(packageId)?.AffectedVersions ?? AffectedVersions;

    public string AffectedVersionsTextFor(string packageId) =>
        Override(packageId) is { } o ? Describe(o.Range, o.AffectedVersions) : AffectedVersionsText;

    private PackageVersionOverride? Override(string packageId) =>
        PackageVersions.FirstOrDefault(p => string.Equals(p.Key, packageId, StringComparison.OrdinalIgnoreCase)).Value;

    private static string Describe(VersionRange range, string text) => range switch
    {
        { HasLowerBound: true, HasUpperBound: false } r when r.MinVersion!.Major == 0 && r.MinVersion.Minor == 0 && r.MinVersion.Patch == 0 => "all versions",
        { HasLowerBound: true, HasUpperBound: false } r => $"{r.MinVersion!.Version.ToString(3)} and later",
        { HasLowerBound: true, HasUpperBound: true } r => $"{r.MinVersion!.Version.ToString(3)} to {r.MaxVersion!.Version.ToString(3)}",
        _ => text,
    };

    public bool DirectOnly => string.Equals(AppliesTo, "direct", StringComparison.OrdinalIgnoreCase);

    public bool Matches(string packageId) =>
        Packages.Any(p => string.Equals(p, packageId, StringComparison.OrdinalIgnoreCase)) ||
        PackagePrefixes.Any(p => packageId.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

public sealed class TermsDatabase
{
    public int SchemaVersion { get; init; }
    public string Updated { get; init; } = "";
    public TermsEntry[] Entries { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static TermsDatabase LoadEmbedded()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PackageTerms.terms.json")
            ?? throw new InvalidOperationException("Embedded terms dataset is missing.");
        return Load(stream);
    }

    public static TermsDatabase Load(Stream json) =>
        JsonSerializer.Deserialize<TermsDatabase>(json, JsonOptions)
            ?? throw new InvalidDataException("The terms dataset is empty.");

    public TermsEntry? Find(string packageId) => Entries.FirstOrDefault(e => e.Matches(packageId));
}
