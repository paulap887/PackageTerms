using System.Text.Json;
using NuGet.Versioning;
using PackageTerms;

namespace PackageTerms.Tests;

public sealed class SarifTests : IDisposable
{
    private static readonly TermsDatabase Db = TermsDatabase.LoadEmbedded();
    private readonly string _root = Directory.CreateTempSubdirectory("packageterms-sarif-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Project()
    {
        string dir = Path.Combine(_root, "src", "Shop.Api");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Shop.Api.csproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Polly" Version="9.0.0" />
                <PackageReference Include="mediatr" />
              </ItemGroup>
            </Project>
            """);
        return path;
    }

    private static ResolvedPackage Package(string id, string version, bool direct, params string[] via) =>
        new(id, NuGetVersion.Parse(version), direct, via);

    private static JsonElement Results(string sarif) =>
        JsonDocument.Parse(sarif).RootElement.GetProperty("runs")[0].GetProperty("results");

    [Fact]
    public void Finds_the_PackageReference_line_ignoring_case()
    {
        string project = Project();

        Assert.Equal(3, Sarif.FindReferenceLine(project, "Polly"));
        Assert.Equal(4, Sarif.FindReferenceLine(project, "MediatR"));
        Assert.Null(Sarif.FindReferenceLine(project, "AutoMapper"));
    }

    [Fact]
    public void Scan_results_point_at_the_reference_with_rule_and_level()
    {
        string project = Project();
        var graphs = new[]
        {
            new ProjectGraph(project, "net9.0", [Package("Polly", "9.0.0", true), Package("MediatR", "13.1.0", true)]),
        };
        var output = new StringWriter();

        Sarif.WriteScan(Evaluator.Evaluate(graphs, Db), output, _root);

        JsonElement root = JsonDocument.Parse(output.ToString()).RootElement;
        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        var results = Results(output.ToString()).EnumerateArray().ToList();
        JsonElement polly = results.Single(r => r.GetProperty("ruleId").GetString() == "PT002");
        Assert.Equal("error", polly.GetProperty("level").GetString());
        JsonElement location = polly.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.Equal("src/Shop.Api/Shop.Api.csproj", location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal("%SRCROOT%", location.GetProperty("artifactLocation").GetProperty("uriBaseId").GetString());
        Assert.Equal(3, location.GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.Contains(results, r => r.GetProperty("ruleId").GetString() == "PT001");
    }

    [Fact]
    public void Transitive_results_have_no_line()
    {
        string project = Project();
        var graphs = new[]
        {
            new ProjectGraph(project, "net9.0",
            [
                Package("Microsoft.Extensions.Http.Resilience", "9.10.0", true),
                Package("Polly.Core", "9.1.0", false, "Microsoft.Extensions.Http.Resilience"),
            ]),
        };
        var output = new StringWriter();

        Sarif.WriteScan(Evaluator.Evaluate(graphs, Db), output, _root);

        JsonElement result = Assert.Single(Results(output.ToString()).EnumerateArray());
        Assert.Equal("PT004", result.GetProperty("ruleId").GetString());
        Assert.Equal("note", result.GetProperty("level").GetString());
        Assert.False(result.GetProperty("locations")[0].GetProperty("physicalLocation").TryGetProperty("region", out _));
    }

    [Fact]
    public void Lock_check_reports_only_changes_that_need_review()
    {
        var before = new Lockfile([new LockedPackage("MediatR", NuGetVersion.Parse("12.4.1"), new PackageLicense("expression", "Apache-2.0"), null)]);
        var after = new Lockfile(
        [
            new LockedPackage("MediatR", NuGetVersion.Parse("13.1.0"), new PackageLicense("file", "LICENSE.md", "ab"), "mediatr-commercial"),
            new LockedPackage("Newtonsoft.Json", NuGetVersion.Parse("13.0.3"), new PackageLicense("expression", "MIT"), null),
        ]);
        var output = new StringWriter();

        Sarif.WriteLockChanges(Lockfile.Compare(before, after), Path.Combine(_root, "packages.licenses.json"), output, _root);

        JsonElement result = Assert.Single(Results(output.ToString()).EnumerateArray());
        Assert.Equal("PT101", result.GetProperty("ruleId").GetString());
        Assert.Equal("packages.licenses.json",
            result.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
    }
}
