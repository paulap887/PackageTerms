using System.Reflection;
using PackageTerms;

const string Usage = """
    package-terms: find NuGet dependencies that went commercial or added a maintenance fee.

    Usage:
      package-terms scan [path] [options]    Report packages whose terms changed
      package-terms lock [path] [options]    Write packages.licenses.json, the licence of every resolved package
      package-terms lock --check [path]      Compare with packages.licenses.json; exit 1 if a licence or terms changed

    Arguments:
      path                 A .sln, .slnx or project file, or a folder (default: current folder)

    Options:
      --format <format>    console (default), markdown, json or sarif
      --fail-on <level>    scan: exit with code 1 when findings reach this level: affected, upgrade-risk or none (default)
      --check              lock: don't write; exit 1 if a package's licence or terms changed
      --lockfile <file>    lock: lockfile path (default: packages.licenses.json next to the solution or project)
      --no-restore         Don't run "dotnet restore" for projects that haven't been restored
      --list               List the packages and terms in the built-in dataset
      --version            Show the tool version
      -h, --help           Show this help
    """;

string? path = null;
string format = "console";
string failOn = "none";
bool restore = true;
bool check = false;
string? lockfilePath = null;
string command = "scan";

var queue = new Queue<string>(args);
if (queue.Count > 0 && queue.Peek() is "scan" or "lock")
{
    command = queue.Dequeue();
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
        case "--check" when command == "lock":
            check = true;
            break;
        case "--lockfile" when command == "lock" && queue.Count > 0:
            lockfilePath = queue.Dequeue();
            break;
        case var p when !p.StartsWith('-') && path is null:
            path = p;
            break;
        default:
            Console.Error.WriteLine($"Unknown option '{arg}'.\n\n{Usage}");
            return 2;
    }
}

if (format is not ("console" or "markdown" or "json" or "sarif") || failOn is not ("affected" or "upgrade-risk" or "none"))
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

    if (command == "lock")
    {
        return Lock(graphs, db, lockfilePath ?? DefaultLockfilePath(path ?? "."), check, format);
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
        case "sarif":
            Sarif.WriteScan(findings, Console.Out, Environment.CurrentDirectory);
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
catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or InvalidDataException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static int Lock(IReadOnlyList<ProjectGraph> graphs, TermsDatabase db, string lockfilePath, bool check, string format)
{
    Lockfile current = Lockfile.Create(graphs, db);
    string name = Path.GetFileName(lockfilePath);
    Lockfile? committed = File.Exists(lockfilePath) ? Lockfile.Load(lockfilePath) : null;

    foreach (LockedPackage p in current.Packages.Where(p => p.License.IsUnknown))
    {
        Console.Error.WriteLine($"Warning: couldn't read the licence of {p.Id} {p.Version} (package folder not found). Run \"dotnet restore\" first.");
    }

    if (check)
    {
        if (committed is null)
        {
            Console.Error.WriteLine($"{lockfilePath} doesn't exist. Run \"package-terms lock\" and commit it first.");
            return 2;
        }

        IReadOnlyList<LockChange> changes = Lockfile.Compare(committed, current);
        switch (format)
        {
            case "markdown":
                Reporters.WriteLockChangesMarkdown(changes, name, Console.Out);
                break;
            case "json":
                Reporters.WriteLockChangesJson(changes, Console.Out);
                break;
            case "sarif":
                Sarif.WriteLockChanges(changes, lockfilePath, Console.Out, Environment.CurrentDirectory);
                break;
            default:
                Reporters.WriteLockChangesConsole(changes, name, Console.Out, color: !Console.IsOutputRedirected);
                break;
        }

        return changes.Any(c => c.NeedsReview) ? 1 : 0;
    }

    if (committed is not null)
    {
        // Show what this update changes, so the person running it sees licence changes before committing.
        IReadOnlyList<LockChange> changes = Lockfile.Compare(committed, current);
        if (changes.Count > 0)
        {
            Reporters.WriteLockChangesConsole(changes, name, Console.Error, color: !Console.IsErrorRedirected, updating: true);
            Console.Error.WriteLine();
        }
    }

    File.WriteAllText(lockfilePath, current.ToJson());
    Console.WriteLine($"Wrote {lockfilePath} ({current.Packages.Count} package versions).");
    return 0;
}

static string DefaultLockfilePath(string input)
{
    string full = Path.GetFullPath(input);
    string dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
    return Path.Combine(dir, Lockfile.DefaultFileName);
}

static void ListDataset(TermsDatabase db)
{
    Console.WriteLine($"Built-in dataset, updated {db.Updated}:");
    Console.WriteLine();
    foreach (TermsEntry e in db.Entries)
    {
        string packages = string.Join(", ", e.Packages.Concat(e.PackagePrefixes.Select(p => p + "*")));
        Console.WriteLine($"{e.Name} ({packages})");
        Console.WriteLine($"  {e.Kind}, {e.AffectedVersionsText}, from {e.EffectiveDate}; last free: {e.LastFreeVersion}");
        foreach ((string package, PackageVersionOverride o) in e.PackageVersions)
        {
            Console.WriteLine($"  {package}: {e.AffectedVersionsTextFor(package)}; last free: {o.LastFreeVersion}");
        }
        Console.WriteLine($"  {e.Source}");
    }
}
