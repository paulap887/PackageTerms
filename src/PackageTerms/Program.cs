using System.Reflection;
using PackageTerms;

const string Usage = """
    package-terms: find NuGet dependencies that went commercial or added a maintenance fee.

    Usage:
      package-terms scan [path] [options]

    Arguments:
      path                 A .sln, .slnx or project file, or a folder (default: current folder)

    Options:
      --format <format>    console (default), markdown or json
      --fail-on <level>    Exit with code 1 when findings reach this level: affected, upgrade-risk or none (default)
      --no-restore         Don't run "dotnet restore" for projects that haven't been restored
      --list               List the packages and terms in the built-in dataset
      --version            Show the tool version
      -h, --help           Show this help
    """;

string? path = null;
string format = "console";
string failOn = "none";
bool restore = true;

var queue = new Queue<string>(args);
if (queue.Count > 0 && queue.Peek() == "scan")
{
    queue.Dequeue();
}

while (queue.Count > 0)
{
    string arg = queue.Dequeue();
    switch (arg)
    {
        case "-h" or "--help":
            Console.WriteLine(Usage);
            return 0;
        case "--version":
            Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]);
            return 0;
        case "--list":
            ListDataset(TermsDatabase.LoadEmbedded());
            return 0;
        case "--format" when queue.Count > 0:
            format = queue.Dequeue().ToLowerInvariant();
            break;
        case "--fail-on" when queue.Count > 0:
            failOn = queue.Dequeue().ToLowerInvariant();
            break;
        case "--no-restore":
            restore = false;
            break;
        case var p when !p.StartsWith('-') && path is null:
            path = p;
            break;
        default:
            Console.Error.WriteLine($"Unknown option '{arg}'.\n\n{Usage}");
            return 2;
    }
}

if (format is not ("console" or "markdown" or "json") || failOn is not ("affected" or "upgrade-risk" or "none"))
{
    Console.Error.WriteLine($"Invalid --format or --fail-on value.\n\n{Usage}");
    return 2;
}

try
{
    TermsDatabase db = TermsDatabase.LoadEmbedded();
    IReadOnlyList<string> projects = ProjectDiscovery.FindProjects(path ?? ".");
    if (projects.Count == 0)
    {
        Console.Error.WriteLine("No project files found.");
        return 2;
    }

    // Progress and warnings go to stderr so markdown and json output stay clean.
    var graphs = new List<ProjectGraph>();
    foreach (string project in projects)
    {
        string? assets = ProjectDiscovery.FindAssets(project, restore, Console.Error);
        if (assets is null)
        {
            Console.Error.WriteLine($"Skipped {Path.GetFileName(project)}: no restore output (obj/project.assets.json). Run \"dotnet restore\" first.");
            continue;
        }

        graphs.AddRange(AssetsReader.Read(assets, project));
    }

    IReadOnlyList<Finding> findings = Evaluator.Evaluate(graphs, db);
    int scanned = graphs.Select(g => g.ProjectPath).Distinct().Count();

    switch (format)
    {
        case "markdown":
            Reporters.WriteMarkdown(findings, scanned, Console.Out);
            break;
        case "json":
            Reporters.WriteJson(findings, scanned, Console.Out);
            break;
        default:
            Reporters.WriteConsole(findings, scanned, Console.Out, color: !Console.IsOutputRedirected);
            break;
    }

    bool fail = failOn switch
    {
        "affected" => findings.Any(f => f.Status == FindingStatus.Affected),
        "upgrade-risk" => findings.Any(f => f.Status is FindingStatus.Affected or FindingStatus.UpgradeRisk),
        _ => false,
    };
    return fail ? 1 : 0;
}
catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or InvalidDataException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static void ListDataset(TermsDatabase db)
{
    Console.WriteLine($"Built-in dataset, updated {db.Updated}:");
    Console.WriteLine();
    foreach (TermsEntry e in db.Entries)
    {
        string packages = string.Join(", ", e.Packages.Concat(e.PackagePrefixes.Select(p => p + "*")));
        Console.WriteLine($"{e.Name} ({packages})");
        Console.WriteLine($"  {e.Kind}, versions {e.AffectedVersionsText}, from {e.EffectiveDate}; last free: {e.LastFreeVersion}");
        Console.WriteLine($"  {e.Source}");
    }
}
