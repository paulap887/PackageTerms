using NuGet.Versioning;
using PackageTerms;

namespace PackageTerms.Tests;

public sealed class LockfileTests : IDisposable
{
    private static readonly TermsDatabase Db = TermsDatabase.LoadEmbedded();
    private readonly string _root = Directory.CreateTempSubdirectory("packageterms-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Creates an extracted package folder with a .nuspec, like the global packages folder has.</summary>
    private string Package(string id, string version, string licenseXml, string? licenseFile = null)
    {
        string dir = Path.Combine(_root, id.ToLowerInvariant(), version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{id.ToLowerInvariant()}.nuspec"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                {licenseXml}
              </metadata>
            </package>
            """);
        if (licenseFile is not null)
        {
            File.WriteAllText(Path.Combine(dir, "LICENSE.md"), licenseFile);
        }

        return dir;
    }

    private static ProjectGraph Graph(params ResolvedPackage[] packages) =>
        new("/src/App/App.csproj", "net9.0", packages);

    private static ResolvedPackage Direct(string id, string version, string? dir = null) =>
        new(id, NuGetVersion.Parse(version), IsDirect: true, Via: [], dir);

    private static LockedPackage Locked(string id, string version, string license, string? terms = null) =>
        new(id, NuGetVersion.Parse(version), new PackageLicense("expression", license), terms);

    [Fact]
    public void Reads_a_license_expression()
    {
        string dir = Package("Polly", "8.9.0", """<license type="expression">BSD-3-Clause</license><licenseUrl>https://licenses.nuget.org/BSD-3-Clause</licenseUrl>""");

        Assert.Equal(new PackageLicense("expression", "BSD-3-Clause"), LicenseReader.Read(dir));
    }

    [Fact]
    public void Reads_a_license_file_with_a_hash_that_ignores_line_endings()
    {
        string a = Package("MediatR", "13.1.0", """<license type="file">LICENSE.md</license><licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl>""", "RPL-1.5\r\nterms\r\n");
        string b = Package("MediatR", "13.2.0", """<license type="file">LICENSE.md</license>""", "RPL-1.5\nterms\n");

        PackageLicense license = LicenseReader.Read(a);

        Assert.Equal("file:LICENSE.md", license.ToString());
        Assert.Equal(64, license.Sha256!.Length);
        Assert.Equal(license, LicenseReader.Read(b));
    }

    [Fact]
    public void Falls_back_to_license_url_and_then_unknown()
    {
        string url = Package("Old", "1.0.0", "<licenseUrl>https://example.com/LICENSE.txt</licenseUrl>");
        string none = Package("Bare", "1.0.0", "");

        Assert.Equal("url:https://example.com/LICENSE.txt", LicenseReader.Read(url).ToString());
        Assert.True(LicenseReader.Read(none).IsUnknown);
        Assert.True(LicenseReader.Read(Path.Combine(_root, "missing")).IsUnknown);
    }

    [Fact]
    public void Create_records_licences_and_affected_terms_once_per_version()
    {
        string mediatr = Package("MediatR", "13.1.0", """<license type="file">LICENSE.md</license>""", "RPL");
        string json = Package("Newtonsoft.Json", "13.0.3", """<license type="expression">MIT</license>""");
        var graphs = new[]
        {
            Graph(Direct("MediatR", "13.1.0", mediatr), Direct("Newtonsoft.Json", "13.0.3", json)),
            Graph(Direct("Newtonsoft.Json", "13.0.3", json)) with { TargetFramework = "net8.0" },
        };

        Lockfile lockfile = Lockfile.Create(graphs, Db);

        Assert.Equal(2, lockfile.Packages.Count);
        Assert.Equal("mediatr-commercial", lockfile.Packages[0].Terms);
        Assert.Null(lockfile.Packages[1].Terms);
        Assert.Equal("MIT", lockfile.Packages[1].License.ToString());
    }

    [Fact]
    public void Json_round_trips_and_is_stable()
    {
        var lockfile = new Lockfile(
        [
            Locked("Polly", "9.0.0", "BSD-3-Clause", "polly"),
            new LockedPackage("MediatR", NuGetVersion.Parse("13.1.0"), new PackageLicense("file", "LICENSE.md", "ab12"), "mediatr"),
            Locked("AutoMapper", "14.0.0", "MIT"),
        ]);

        string json = lockfile.ToJson();
        Lockfile loaded = Lockfile.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

        Assert.Equal(json, loaded.ToJson());
        Assert.DoesNotContain("\r", json);
        Assert.True(json.IndexOf("AutoMapper", StringComparison.Ordinal) < json.IndexOf("MediatR", StringComparison.Ordinal));
        Assert.Contains("\"license\": \"file:LICENSE.md\"", json);
        Assert.Contains("\"licenseSha256\": \"ab12\"", json);
        Assert.Empty(Lockfile.Compare(lockfile, loaded));
    }

    [Fact]
    public void Upgrade_to_a_new_licence_is_a_licence_change()
    {
        var before = new Lockfile([Locked("MediatR", "12.4.1", "Apache-2.0")]);
        var after = new Lockfile([new LockedPackage("MediatR", NuGetVersion.Parse("13.1.0"), new PackageLicense("file", "LICENSE.md", "ab12"), "mediatr")]);

        LockChange change = Assert.Single(Lockfile.Compare(before, after));

        Assert.Equal(LockChangeKind.LicenseChanged, change.Kind);
        Assert.True(change.NeedsReview);
        Assert.Equal(["Apache-2.0"], change.OldLicenses);
        Assert.Equal(["file:LICENSE.md"], change.NewLicenses);
        Assert.Equal("mediatr", change.NewTerms);
    }

    [Fact]
    public void Reworded_licence_file_is_a_licence_change()
    {
        var before = new Lockfile([new LockedPackage("Lib", NuGetVersion.Parse("1.0.0"), new PackageLicense("file", "LICENSE.md", "aaaa"), null)]);
        var after = new Lockfile([new LockedPackage("Lib", NuGetVersion.Parse("1.1.0"), new PackageLicense("file", "LICENSE.md", "bbbb"), null)]);

        Assert.Equal(LockChangeKind.LicenseChanged, Assert.Single(Lockfile.Compare(before, after)).Kind);
    }

    [Fact]
    public void Same_licence_with_new_terms_is_a_terms_change()
    {
        var before = new Lockfile([Locked("Polly", "8.9.0", "BSD-3-Clause")]);
        var after = new Lockfile([Locked("Polly", "9.0.0", "BSD-3-Clause", "polly")]);

        LockChange change = Assert.Single(Lockfile.Compare(before, after));

        Assert.Equal(LockChangeKind.TermsChanged, change.Kind);
        Assert.True(change.NeedsReview);
    }

    [Fact]
    public void Upgrade_with_the_same_licence_does_not_need_review()
    {
        var before = new Lockfile([Locked("Newtonsoft.Json", "13.0.1", "MIT")]);
        var after = new Lockfile([Locked("Newtonsoft.Json", "13.0.3", "MIT")]);

        LockChange change = Assert.Single(Lockfile.Compare(before, after));

        Assert.Equal(LockChangeKind.Updated, change.Kind);
        Assert.False(change.NeedsReview);
    }

    [Fact]
    public void New_packages_need_review_only_when_covered_by_changed_terms()
    {
        var before = new Lockfile([Locked("Old.Lib", "1.0.0", "MIT")]);
        var after = new Lockfile([Locked("AutoMapper", "15.0.0", "RPL-1.5", "automapper-commercial"), Locked("New.Lib", "2.0.0", "MIT")]);

        var changes = Lockfile.Compare(before, after);

        Assert.Equal([LockChangeKind.Added, LockChangeKind.Added, LockChangeKind.Removed], changes.Select(c => c.Kind));
        Assert.Equal([true, false, false], changes.Select(c => c.NeedsReview));
    }

    [Fact]
    public void Unknown_licence_is_not_reported_as_a_change()
    {
        var before = new Lockfile([Locked("Lib", "1.0.0", "MIT")]);
        var after = new Lockfile([new LockedPackage("Lib", NuGetVersion.Parse("1.0.0"), PackageLicense.Unknown, null)]);

        Assert.Empty(Lockfile.Compare(before, after));
    }
}
