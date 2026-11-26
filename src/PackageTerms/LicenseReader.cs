using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace PackageTerms;

/// <summary>
/// The licence a package declares in its .nuspec.
/// </summary>
/// <param name="Type">"expression" (an SPDX expression), "file" (a licence file in the package), "url" (the deprecated licenseUrl) or "unknown".</param>
/// <param name="Value">The SPDX expression, the file's path inside the package, or the URL.</param>
/// <param name="Sha256">For a licence file, the SHA-256 of its text (line endings normalised), so a reworded licence is caught.</param>
public sealed record PackageLicense(string Type, string Value, string? Sha256 = null)
{
    public static readonly PackageLicense Unknown = new("unknown", "");

    public bool IsUnknown => Type == "unknown";

    /// <summary>Short form used in the lockfile and reports, for example "MIT", "file:LICENSE.md" or "url:https://…".</summary>
    public override string ToString() => Type switch
    {
        "expression" => Value,
        "unknown" => "unknown",
        _ => $"{Type}:{Value}",
    };
}

public static class LicenseReader
{
    // NuGet sets licenseUrl to this placeholder when a package uses <license>; it says nothing on its own.
    private const string DeprecatedLicenseUrl = "https://aka.ms/deprecateLicenseUrl";

    /// <summary>Reads the licence from the .nuspec in an extracted package folder.</summary>
    public static PackageLicense Read(string? packageDirectory)
    {
        if (packageDirectory is null || !Directory.Exists(packageDirectory))
        {
            return PackageLicense.Unknown;
        }

        string? nuspec = Directory.EnumerateFiles(packageDirectory, "*.nuspec").FirstOrDefault();
        if (nuspec is null)
        {
            return PackageLicense.Unknown;
        }

        XElement? metadata;
        try
        {
            metadata = XDocument.Load(nuspec).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        }
        catch (System.Xml.XmlException)
        {
            return PackageLicense.Unknown;
        }

        if (metadata is null)
        {
            return PackageLicense.Unknown;
        }

        XElement? license = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        string? type = license?.Attribute("type")?.Value;
        string value = license?.Value.Trim() ?? "";

        if (type == "expression" && value.Length > 0)
        {
            return new PackageLicense("expression", value);
        }

        if (type == "file" && value.Length > 0)
        {
            string path = Path.Combine(packageDirectory, value.Replace('\\', Path.DirectorySeparatorChar));
            return new PackageLicense("file", value.Replace('\\', '/'), File.Exists(path) ? HashText(path) : null);
        }

        string url = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "licenseUrl")?.Value.Trim() ?? "";
        return url.Length > 0 && url != DeprecatedLicenseUrl ? new PackageLicense("url", url) : PackageLicense.Unknown;
    }

    private static string HashText(string path)
    {
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
