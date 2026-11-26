using System.Text.Json;
using NuGet.Versioning;

namespace PackageTerms;

/// <summary>Reads obj/project.assets.json, the file NuGet restore writes for every SDK-style project.</summary>
public static class AssetsReader
{
    public static IReadOnlyList<ProjectGraph> Read(string assetsPath, string projectPath)
    {
        using FileStream stream = File.OpenRead(assetsPath);
        using JsonDocument doc = JsonDocument.Parse(stream);
        return Read(doc.RootElement, projectPath);
    }

    public static IReadOnlyList<ProjectGraph> Read(JsonElement root, string projectPath)
    {
        var graphs = new List<ProjectGraph>();
        if (!root.TryGetProperty("targets", out JsonElement targets) ||
            !root.TryGetProperty("project", out JsonElement project) ||
            !project.TryGetProperty("frameworks", out JsonElement frameworks))
        {
            return graphs;
        }

        // Targets with a "/" are runtime-specific copies of the same graph.
        var targetKeys = targets.EnumerateObject().Select(t => t.Name).Where(n => !n.Contains('/')).ToList();
        var frameworkList = frameworks.EnumerateObject().ToList();
        Dictionary<string, string> packageDirs = PackageDirectories(root);

        for (int i = 0; i < targetKeys.Count; i++)
        {
            string targetKey = targetKeys[i];

            // Newer assets files key targets by alias ("net9.0"); older ones by full name. Fall back to order.
            JsonProperty framework = frameworkList.FirstOrDefault(f => f.Name == targetKey);
            if (framework.Value.ValueKind == JsonValueKind.Undefined)
            {
                if (i >= frameworkList.Count)
                {
                    continue;
                }

                framework = frameworkList[i];
            }

            var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (framework.Value.TryGetProperty("dependencies", out JsonElement deps))
            {
                foreach (JsonProperty dep in deps.EnumerateObject())
                {
                    bool autoReferenced = dep.Value.TryGetProperty("autoReferenced", out JsonElement a) && a.GetBoolean();
                    if (!autoReferenced)
                    {
                        direct.Add(dep.Name);
                    }
                }
            }

            graphs.Add(new ProjectGraph(projectPath, framework.Name, ReadTarget(targets.GetProperty(targetKey), direct, packageDirs)));
        }

        return graphs;
    }

    /// <summary>
    /// Maps "Id/Version" to the package's folder on disk, using "libraries" (relative paths) and "packageFolders"
    /// (the global packages folder first, then any fallback folders).
    /// </summary>
    private static Dictionary<string, string> PackageDirectories(JsonElement root)
    {
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("libraries", out JsonElement libraries) ||
            !root.TryGetProperty("packageFolders", out JsonElement folders))
        {
            return dirs;
        }

        var folderList = folders.EnumerateObject().Select(f => f.Name).ToList();
        foreach (JsonProperty lib in libraries.EnumerateObject())
        {
            if (lib.Value.TryGetProperty("type", out JsonElement t) && t.GetString() == "package" &&
                lib.Value.TryGetProperty("path", out JsonElement p) && p.GetString() is { Length: > 0 } relative &&
                folderList.Select(f => Path.Combine(f, relative)).FirstOrDefault(System.IO.Directory.Exists) is { } dir)
            {
                dirs[lib.Name] = dir;
            }
        }

        return dirs;
    }

    private static List<ResolvedPackage> ReadTarget(JsonElement target, HashSet<string> direct, Dictionary<string, string> packageDirs)
    {
        var versions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty lib in target.EnumerateObject())
        {
            string? type = lib.Value.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            if (type is not ("package" or "project"))
            {
                continue;
            }

            int slash = lib.Name.IndexOf('/');
            string id = lib.Name[..slash];
            if (type == "project")
            {
                // Referenced projects are roots too: their packages reach this project through them.
                projects.Add(id);
            }
            else
            {
                versions[id] = NuGetVersion.Parse(lib.Name[(slash + 1)..]);
            }

            var list = new List<string>();
            if (lib.Value.TryGetProperty("dependencies", out JsonElement deps))
            {
                list.AddRange(deps.EnumerateObject().Select(d => d.Name));
            }

            children[id] = list;
        }

        Dictionary<string, List<string>> paths = ShortestPathsFromDirect(direct, projects, children);

        return versions
            .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(v => new ResolvedPackage(
                v.Key,
                v.Value,
                direct.Contains(v.Key),
                paths.TryGetValue(v.Key, out List<string>? via) ? via : [],
                packageDirs.GetValueOrDefault($"{v.Key}/{v.Value.OriginalVersion ?? v.Value.ToNormalizedString()}")
                    ?? packageDirs.GetValueOrDefault($"{v.Key}/{v.Value.ToNormalizedString()}")))
            .ToList();
    }

    /// <summary>For each transitive package, finds the shortest chain of packages from a direct reference.</summary>
    private static Dictionary<string, List<string>> ShortestPathsFromDirect(
        HashSet<string> direct, HashSet<string> projects, Dictionary<string, List<string>> children)
    {
        var paths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        // Direct packages first, so a package reachable both ways gets the shorter, package-only path.
        foreach (string root in direct.Concat(projects).Where(children.ContainsKey))
        {
            if (paths.TryAdd(root, []))
            {
                queue.Enqueue(root);
            }
        }

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            foreach (string child in children[current])
            {
                if (!paths.ContainsKey(child) && children.ContainsKey(child))
                {
                    paths[child] = [.. paths[current], projects.Contains(current) ? $"{current} (project)" : current];
                    queue.Enqueue(child);
                }
            }
        }

        return paths;
    }
}
