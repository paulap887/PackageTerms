using NuGet.Versioning;

namespace PackageTerms;

/// <summary>A package resolved by NuGet restore for one target framework of one project.</summary>
public sealed record ResolvedPackage(string Id, NuGetVersion Version, bool IsDirect, IReadOnlyList<string> Via);

/// <summary>The restore result for one project and target framework.</summary>
public sealed record ProjectGraph(string ProjectPath, string TargetFramework, IReadOnlyList<ResolvedPackage> Packages);

public enum FindingStatus
{
    /// <summary>The resolved version is covered by the new terms and the terms apply to this kind of reference.</summary>
    Affected,

    /// <summary>The resolved version is still free, but a direct reference would be affected after a major upgrade.</summary>
    UpgradeRisk,

    /// <summary>The package is covered, but the terms don't apply to transitive references.</summary>
    TransitiveExempt,
}

public sealed record Finding(
    TermsEntry Entry,
    FindingStatus Status,
    string PackageId,
    NuGetVersion Version,
    bool IsDirect,
    IReadOnlyList<string> Via,
    IReadOnlyList<string> Projects);
