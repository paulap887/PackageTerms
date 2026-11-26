using System.Text.Json;
using PackageTerms;

namespace PackageTerms.Tests;

public class EvaluatorTests
{
    private static readonly TermsDatabase Db = TermsDatabase.LoadEmbedded();

    /// <summary>Builds a minimal project.assets.json: direct packages plus a package graph for one framework.</summary>
    private static IReadOnlyList<ProjectGraph> Assets(string[] direct, params (string Lib, string[] Deps)[] libs)
    {
        var targets = libs.ToDictionary(
            l => l.Lib,
            l => (object)new { type = "package", dependencies = l.Deps.ToDictionary(d => d, _ => "[1.0.0, )") });
        var json = JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, object> { ["net9.0"] = targets },
            project = new
            {
                frameworks = new Dictionary<string, object>
                {
                    ["net9.0"] = new { dependencies = direct.ToDictionary(d => d, _ => new { target = "Package", version = "[1.0.0, )" }) },
                },
            },
        });
        using JsonDocument doc = JsonDocument.Parse(json);
        return AssetsReader.Read(doc.RootElement, "/src/App/App.csproj");
    }

    [Fact]
    public void Commercial_version_referenced_directly_is_affected()
    {
        var graphs = Assets(["MediatR"], ("MediatR/13.1.0", []));

        Finding f = Assert.Single(Evaluator.Evaluate(graphs, Db));

        Assert.Equal(FindingStatus.Affected, f.Status);
        Assert.Equal("12.5.0", f.Entry.LastFreeVersion);
    }

    [Fact]
    public void Commercial_version_referenced_transitively_is_still_affected()
    {
        var graphs = Assets(["Contoso.Messaging"], ("Contoso.Messaging/1.0.0", ["MediatR"]), ("MediatR/13.0.0", []));

        Finding f = Assert.Single(Evaluator.Evaluate(graphs, Db));

        Assert.Equal(FindingStatus.Affected, f.Status);
        Assert.False(f.IsDirect);
        Assert.Equal(["Contoso.Messaging"], f.Via);
    }

    [Fact]
    public void Last_free_version_referenced_directly_is_an_upgrade_risk()
    {
        var graphs = Assets(["AutoMapper"], ("AutoMapper/14.0.0", []));

        Assert.Equal(FindingStatus.UpgradeRisk, Assert.Single(Evaluator.Evaluate(graphs, Db)).Status);
    }

    [Fact]
    public void Free_version_referenced_transitively_is_not_reported()
    {
        var graphs = Assets(["Contoso.Mapping"], ("Contoso.Mapping/1.0.0", ["AutoMapper"]), ("AutoMapper/14.0.0", []));

        Assert.Empty(Evaluator.Evaluate(graphs, Db));
    }

    [Fact]
    public void Polly_9_referenced_directly_is_affected_by_the_fee()
    {
        var graphs = Assets(["Polly"], ("Polly/9.0.0", []));

        Finding f = Assert.Single(Evaluator.Evaluate(graphs, Db));

        Assert.Equal(FindingStatus.Affected, f.Status);
        Assert.Equal("maintenance-fee", f.Entry.Kind);
    }

    [Fact]
    public void Polly_through_Microsoft_resilience_is_exempt()
    {
        var graphs = Assets(
            ["Microsoft.Extensions.Http.Resilience"],
            ("Microsoft.Extensions.Http.Resilience/9.10.0", ["Microsoft.Extensions.Resilience"]),
            ("Microsoft.Extensions.Resilience/9.10.0", ["Polly.Core"]),
            ("Polly.Core/9.1.0", []));

        Finding f = Assert.Single(Evaluator.Evaluate(graphs, Db));

        Assert.Equal(FindingStatus.TransitiveExempt, f.Status);
        Assert.Equal(["Microsoft.Extensions.Http.Resilience", "Microsoft.Extensions.Resilience"], f.Via);
    }

    [Fact]
    public void Polly_Core_under_a_direct_Polly_reference_is_reported_once()
    {
        var graphs = Assets(["Polly"], ("Polly/8.9.0", ["Polly.Core"]), ("Polly.Core/8.9.0", []));

        Finding f = Assert.Single(Evaluator.Evaluate(graphs, Db));

        Assert.Equal("Polly", f.PackageId);
        Assert.Equal(FindingStatus.UpgradeRisk, f.Status);
    }

    [Fact]
    public void MassTransit_transport_packages_match_by_prefix()
    {
        var graphs = Assets(["MassTransit.RabbitMQ"], ("MassTransit.RabbitMQ/9.2.5", []));

        Assert.Equal(FindingStatus.Affected, Assert.Single(Evaluator.Evaluate(graphs, Db)).Status);
    }

    [Fact]
    public void Unrelated_packages_are_ignored()
    {
        var graphs = Assets(["Newtonsoft.Json", "FluentAssertions.Json"], ("Newtonsoft.Json/13.0.3", []), ("FluentAssertions.Json/8.0.0", []));

        Assert.Empty(Evaluator.Evaluate(graphs, Db));
    }

    [Fact]
    public void Every_dataset_entry_is_complete()
    {
        Assert.NotEmpty(Db.Entries);
        foreach (TermsEntry e in Db.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Summary), e.Id);
            Assert.StartsWith("https://", e.Source);
            Assert.True(e.Range.HasLowerBound, e.Id);
            Assert.Contains(e.Kind, new[] { "commercial", "maintenance-fee" });
            Assert.NotEmpty(e.Packages.Concat(e.PackagePrefixes));
            Assert.False(string.IsNullOrWhiteSpace(e.LastFreeVersion), e.Id);
            foreach ((string package, PackageVersionOverride o) in e.PackageVersions)
            {
                Assert.True(e.Matches(package), $"{e.Id}: {package} isn't one of the entry's packages");
                Assert.True(o.Range.HasLowerBound, $"{e.Id}: {package}");
                Assert.False(string.IsNullOrWhiteSpace(o.LastFreeVersion), $"{e.Id}: {package}");
            }
        }

        var ids = Db.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Package_versions_override_the_entry_range()
    {
        var affected = Assets(["SixLabors.Fonts"], ("SixLabors.Fonts/2.0.0", []));
        var stillFree = Assets(["SixLabors.Fonts"], ("SixLabors.Fonts/1.0.1", []));

        Finding f = Assert.Single(Evaluator.Evaluate(affected, Db));
        Assert.Equal(FindingStatus.Affected, f.Status);
        Assert.Equal("1.0.1", f.LastFreeVersion);
        Assert.Equal(FindingStatus.UpgradeRisk, Assert.Single(Evaluator.Evaluate(stillFree, Db)).Status);
    }

    [Fact]
    public void ImageSharp_through_another_Six_Labors_package_is_not_exempt()
    {
        var graphs = Assets(
            ["SixLabors.ImageSharp.Web"],
            ("SixLabors.ImageSharp.Web/3.1.0", ["SixLabors.ImageSharp"]),
            ("SixLabors.ImageSharp/3.1.5", []));

        Finding f = Assert.Single(Evaluator.Evaluate(graphs, Db));

        Assert.Equal("SixLabors.ImageSharp.Web", f.PackageId);
        Assert.Equal(FindingStatus.Affected, f.Status);
    }

    [Fact]
    public void ImageSharp_through_an_unrelated_package_is_exempt()
    {
        var graphs = Assets(["Contoso.Thumbnails"], ("Contoso.Thumbnails/1.0.0", ["SixLabors.ImageSharp"]), ("SixLabors.ImageSharp/3.1.5", []));

        Assert.Equal(FindingStatus.TransitiveExempt, Assert.Single(Evaluator.Evaluate(graphs, Db)).Status);
    }

    [Fact]
    public void Verify_through_a_test_adapter_carries_the_fee()
    {
        var graphs = Assets(["Verify.XunitV3"], ("Verify.XunitV3/33.7.3", ["Verify"]), ("Verify/33.7.3", []));

        Finding f = Assert.Single(Evaluator.Evaluate(graphs, Db));

        Assert.Equal("Verify", f.PackageId);
        Assert.Equal(FindingStatus.Affected, f.Status);
        Assert.Equal("maintenance-fee", f.Entry.Kind);
    }

    [Fact]
    public void Every_Duende_IdentityServer_version_is_commercial()
    {
        var graphs = Assets(["Duende.IdentityServer.EntityFramework"], ("Duende.IdentityServer.EntityFramework/7.0.4", []));

        Assert.Equal(FindingStatus.Affected, Assert.Single(Evaluator.Evaluate(graphs, Db)).Status);
    }

    [Fact]
    public void Json_everything_packages_use_their_own_major_versions()
    {
        var graphs = Assets(["JsonPath.Net", "JsonPointer.Net"], ("JsonPath.Net/3.0.0", []), ("JsonPointer.Net/6.0.1", []));

        var findings = Evaluator.Evaluate(graphs, Db);

        Assert.Equal(FindingStatus.Affected, findings.Single(f => f.PackageId == "JsonPath.Net").Status);
        Assert.Equal(FindingStatus.UpgradeRisk, findings.Single(f => f.PackageId == "JsonPointer.Net").Status);
    }
}
