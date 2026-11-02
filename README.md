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
package-terms scan --fail-on affected   # exit code 1 in CI when an affected package is found
package-terms --list                    # show the built-in dataset
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
| 1 | A finding reached the `--fail-on` level |
| 2 | Invalid arguments or no projects found |

### GitHub Actions

```yaml
- run: dotnet tool install -g PackageTerms
- run: package-terms scan --fail-on affected
```

## Built-in dataset

| Package | Change | Covered versions | Last free version |
|---|---|---|---|
| Polly, Polly.Core | Open Source Maintenance Fee (direct references only) | 9.0.0 and later | 8.x |
| MediatR | Commercial (RPL-1.5 + commercial licence) | 13.0.0 and later | 12.5.0 |
| AutoMapper | Commercial (RPL-1.5 + commercial licence) | 15.0.0 and later | 14.0.0 |
| MassTransit, MassTransit.* | Commercial (Massient licence) | 9.0.0 and later | 8.5.12 |
| FluentAssertions | Commercial for commercial use (Xceed) | 8.0.0 and later | 7.2.2 |

Every entry links to the project's own announcement or licence. The dataset lives in [`src/PackageTerms/Data/terms.json`](src/PackageTerms/Data/terms.json). If a package is missing or a detail is out of date, please open an issue or a pull request with a link to the official source.

## Disclaimer

PackageTerms gives information, not legal advice. Terms can change, and they can depend on your organisation's revenue and how you use a package. Always check the linked source and your own licence agreements.

## License

MIT
