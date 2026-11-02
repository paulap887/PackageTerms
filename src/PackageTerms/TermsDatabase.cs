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

    [JsonIgnore]
    public VersionRange Range => _range ??= VersionRange.Parse(AffectedVersions);
    private VersionRange? _range;

    /// <summary>Readable form of <see cref="AffectedVersions"/>, for example "15.0.0 and later".</summary>
    public string AffectedVersionsText =>
        Range switch
        {
            { HasLowerBound: true, HasUpperBound: false } r => $"{r.MinVersion!.Version.ToString(3)} and later",
            { HasLowerBound: true, HasUpperBound: true } r => $"{r.MinVersion!.Version.ToString(3)} to {r.MaxVersion!.Version.ToString(3)}",
            _ => AffectedVersions,
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
