# PackageTerms

**Find the NuGet packages in your .NET solution that went commercial or added a maintenance fee, before they surprise you.**

Several popular .NET libraries have changed their terms: MediatR and AutoMapper went commercial, MassTransit v9 and FluentAssertions v8 need paid licences for commercial use, and Polly adds a maintenance fee for its next major version. Licence scanners that only read a package's licence label can miss these changes. Polly's licence, for example, stays BSD-3-Clause. They also don't tell you which version changed, whether the change applies to you, or what to do about it.

`package-terms` scans your solution and tells you, for each affected package:

- **Whether you're affected**: the exact version you resolve, and whether you reference it directly or it comes in through another package or project
- **What changed and when**, with a link to the official announcement
- **What to do**: the last free version and free alternatives

```
$ package-terms scan Shop.sln
Scanned 2 project(s).

[COMMERCIAL] MediatR 13.1.0 (direct)
  From v13, MediatR is dual-licensed under RPL-1.5 and the Lucky Penny Software commercial licence. Earlier versions keep their original Apache-2.0 terms.
  Last free version: 12.5.0
  Alternative: Mediator.SourceGenerator: Source-generated mediator by Martin Othamar (MIT).
  Projects: Shop.Api.csproj
  Source: https://www.jimmybogard.com/automapper-and-mediatr-commercial-editions-launch-today/

[UPGRADE RISK] Polly 8.9.0 (direct)
  Your version is still free, but Polly 9.0.0 and later carries a maintenance fee (from 2026-11-16). Check the terms before upgrading.
  ...
```

If Polly only reaches you through Microsoft's resilience packages, you'll see that no action is needed:

```
[NO ACTION] Polly.Core 8.4.2 (transitive via Microsoft.Extensions.Http.Resilience → Microsoft.Extensions.Resilience → Polly.Extensions)
  Polly's new terms cover direct references only, so this transitive reference is exempt.
```

## Install

```bash
dotnet tool install -g PackageTerms
```

Requires the .NET 8 SDK or later.

## Usage

```bash
package-terms scan                      # solution or projects in the current folder
package-terms scan MySolution.sln       # .sln, .slnx or a project file
package-terms scan --format markdown    # for pull-request comments
package-terms scan --format json        # for scripts
package-terms scan --format sarif       # for GitHub code scanning and Azure DevOps
package-terms scan --fail-on affected   # exit code 1 in CI when an affected package is found
package-terms --list                    # show the built-in dataset
package-terms lock                      # write packages.licenses.json
package-terms lock --check              # exit code 1 in CI when a licence or terms changed
```

`package-terms` reads the restore output (`obj/project.assets.json`), so it sees exactly the versions NuGet resolved, including Central Package Management, `Directory.Build.props` and transitive dependencies. Projects that haven't been restored are restored automatically. Pass `--no-restore` to skip that.

### Statuses

| Status | Meaning |
|---|---|
| **COMMERCIAL** / **FEE APPLIES** | The version you resolve is covered by the new terms. |
| **UPGRADE RISK** | You reference a version that's still free, but a newer major version is covered. Check before upgrading. |
| **NO ACTION** | The package is covered, but its terms apply to direct references only and yours is transitive. |

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Scan completed (and nothing reached the `--fail-on` level) |
| 1 | A finding reached the `--fail-on` level, or `lock --check` found a licence or terms change |
| 2 | Invalid arguments, no projects found, or no lockfile for `lock --check` |

### Licence lockfile

The dataset covers packages whose terms are known to have changed. To catch the next one, commit a licence lockfile:

```bash
package-terms lock
```

This writes `packages.licenses.json` next to your solution: every package version NuGet resolved, the licence its `.nuspec` declares (an SPDX expression, a licence file with a SHA-256 of its text, or a licence URL), and any changed terms from the dataset. It's sorted and stable, so it diffs cleanly in pull requests.

In CI, `package-terms lock --check` compares the resolved packages with the committed file and exits with code 1 when:

- **a package's licence changed**: an upgrade declares a licence the lockfile has never recorded for that package, or its licence file was reworded
- **a package's terms changed**: the licence label is the same, but the new version is covered by changed terms (Polly 9's maintenance fee keeps BSD-3-Clause)
- **a new package is covered by changed terms**

Upgrades that keep the same licence, new packages with ordinary licences, and removed packages are listed but don't fail the check. After reviewing a change, run `package-terms lock` again and commit the file.

```
$ package-terms lock --check
[LICENCE CHANGED] MediatR 12.4.1 → 13.1.0
  Licence: Apache-2.0 → file:LICENSE.md
  Now covered by changed terms: mediatr-commercial (run "package-terms scan" for details)
[NEW PACKAGE] Microsoft.Extensions.Logging.Abstractions 8.0.0
  Licence: MIT

1 change(s) need review. Check the new terms, then run "package-terms lock" and commit packages.licenses.json.
```

Use `--lockfile <file>` for a different location, `--format markdown` for a pull-request comment, and `--format sarif` for code scanning.

### GitHub Actions

```yaml
- run: dotnet tool install -g PackageTerms
- run: package-terms scan --fail-on affected
- run: package-terms lock --check
```

### GitHub code scanning (SARIF)

`--format sarif` writes [SARIF 2.1.0](https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html), so findings show up as code scanning alerts and pull-request annotations. A direct reference points at its `PackageReference` line in the project file; `lock --check` results point at `packages.licenses.json`. Run it from the repository root so paths resolve.

```yaml
permissions:
  security-events: write
steps:
  - uses: actions/checkout@v5
  - uses: actions/setup-dotnet@v5
  - run: dotnet tool install -g PackageTerms
  - run: package-terms scan --format sarif > package-terms.sarif
  - uses: github/codeql-action/upload-sarif@v4
    with:
      sarif_file: package-terms.sarif
      category: package-terms
```

| Rule | Level | Meaning |
|---|---|---|
| PT001 | error | Commercial licence applies to the resolved version |
| PT002 | error | Maintenance fee applies to the resolved version |
| PT003 | warning | Upgrade risk: a newer major version is covered |
| PT004 | note | Covered package, but this transitive reference is exempt |
| PT101 | error | `lock --check`: a package's licence changed |
| PT102 | error | `lock --check`: a package's terms changed |
| PT103 | error | `lock --check`: a new package is covered by changed terms |

## Built-in dataset

| Package | Change | Covered versions | Last free version |
|---|---|---|---|
| Polly, Polly.Core | Open Source Maintenance Fee (direct references only) | 9.0.0 and later | 8.x |
| MediatR | Commercial (RPL-1.5 + commercial licence) | 13.0.0 and later | 12.5.0 |
| AutoMapper | Commercial (RPL-1.5 + commercial licence) | 15.0.0 and later | 14.0.0 |
| MassTransit, MassTransit.* | Commercial (Massient licence) | 9.0.0 and later | 8.5.12 |
| FluentAssertions | Commercial for commercial use (Xceed) | 8.0.0 and later | 7.2.2 |
| QuestPDF | Commercial above US$1M revenue (direct references only) | 2023.4.0 and later | 2022.12.15 |
| SixLabors.ImageSharp, .Drawing, .Fonts, .Web | Six Labors Split License: commercial above US$1M revenue (direct references only) | ImageSharp 3+, Drawing 2+, Fonts 2+, Web 3+ | 2.1.13, 1.0.0, 1.0.1, 2.0.2 |
| EPPlus | Polyform Noncommercial + commercial licence | 5.0.0 and later | 4.5.3.3 |
| Extended.Wpf.Toolkit | Non-commercial use only (Xceed Community License) | 4.0.0 and later | 3.8.2 |
| Blazorise, Blazorise.* | Commercial for organisations (Blazorise SLA) | 0.9.4.4 and later | 0.9.4.3 |
| NBomber | Commercial for organisations (NBomber Business License) | 5.0.0 and later | 4.1.2 |
| Duende.IdentityServer* | Commercial (free Community Edition for small organisations) | All versions | None (IdentityServer4 is unsupported) |
| WixToolset.* | Open Source Maintenance Fee | 6.0.0 and later | 5.0.2 |
| JsonSchema.Net, JsonPath.Net, JsonPointer.Net and other json-everything packages | Open Source Maintenance Fee | February 2026 majors (JsonSchema.Net 9+, JsonPath.Net 3+, …) | JsonSchema.Net 8.0.5, … |
| Verify | Open Source Maintenance Fee | 33.0.0 and later | 32.0.1 |
| ThisAssembly, ThisAssembly.* | Open Source Maintenance Fee | 2.1.0 and later | 2.0.14 |
| NPOI | Open Source Maintenance Fee | 2.8.0 and later | 2.7.6 |

Every entry links to the project's own announcement or licence, and each version boundary is checked against the licence NuGet records for every published version. Run `package-terms --list` for the per-package versions. The dataset lives in [`src/PackageTerms/Data/terms.json`](src/PackageTerms/Data/terms.json). If a package is missing or a detail is out of date, please open an issue or a pull request with a link to the official source.

## Disclaimer

PackageTerms gives information, not legal advice. Terms can change, and they can depend on your organisation's revenue and how you use a package. Always check the linked source and your own licence agreements.

## License

MIT
