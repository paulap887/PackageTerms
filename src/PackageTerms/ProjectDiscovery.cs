using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PackageTerms;

public static partial class ProjectDiscovery
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>Accepts a .sln, .slnx or project file, or a directory containing them.</summary>
    public static IReadOnlyList<string> FindProjects(string input)
    {
        string path = Path.GetFullPath(input);

        if (Directory.Exists(path))
        {
            string[] solutions = Directory.GetFiles(path, "*.sln").Concat(Directory.GetFiles(path, "*.slnx")).ToArray();
            if (solutions.Length == 1)
            {
                return FromSolution(solutions[0]);
            }

            return Directory.EnumerateFiles(path, "*.*proj", SearchOption.AllDirectories)
                .Where(IsProjectFile)
                .Where(p => !IsUnderBuildOutput(path, p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Couldn't find '{input}'.");
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".sln" or ".slnx" => FromSolution(path),
            _ when IsProjectFile(path) => [path],
            _ => throw new ArgumentException($"'{input}' isn't a solution or project file."),
        };
    }

    private static IReadOnlyList<string> FromSolution(string solutionPath)
    {
        string dir = Path.GetDirectoryName(solutionPath)!;
        IEnumerable<string> relative = Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? XDocument.Load(solutionPath).Descendants("Project").Select(p => (string?)p.Attribute("Path") ?? "")
            : SlnProjectLine().Matches(File.ReadAllText(solutionPath)).Select(m => m.Groups["path"].Value);

        return relative
            .Select(r => Path.GetFullPath(Path.Combine(dir, r.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(IsProjectFile)
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Returns obj/project.assets.json for the project, running "dotnet restore" first if it's missing.</summary>
    public static string? FindAssets(string projectPath, bool allowRestore, TextWriter log)
    {
        string assets = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
        if (File.Exists(assets) || !allowRestore)
        {
            return File.Exists(assets) ? assets : null;
        }

        log.WriteLine($"Restoring {Path.GetFileName(projectPath)}…");
        using var restore = Process.Start(new ProcessStartInfo("dotnet", ["restore", projectPath, "--verbosity", "quiet"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        restore.StandardOutput.ReadToEnd();
        restore.StandardError.ReadToEnd();
        restore.WaitForExit();

        return File.Exists(assets) ? assets : null;
    }

    private static bool IsProjectFile(string path) =>
        ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsUnderBuildOutput(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or "node_modules" || part.StartsWith('.'));

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();
}
