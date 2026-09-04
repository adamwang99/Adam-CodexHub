# Third-Party Notices

Adam CodexHub is licensed under the MIT License. It also uses third-party software under separate licenses. The list below covers the direct production dependencies and major transitive components known for the current release configuration.

## Distributed production components

| Component | Version family | License | Source |
| --- | --- | --- | --- |
| .NET, ASP.NET Core and Windows Desktop Runtime | 8.0 | MIT and component-specific notices | [dotnet/runtime](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.Hosting and transitive extensions | 8.0 | MIT | [dotnet/runtime](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.Http and transitive extensions | 8.0 | MIT | [dotnet/runtime](https://github.com/dotnet/runtime) |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core | 8.0 | MIT | [dotnet/efcore](https://github.com/dotnet/efcore) |
| System.Security.Cryptography.ProtectedData | 8.0 | MIT | [dotnet/runtime](https://github.com/dotnet/runtime) |
| SQLitePCLRaw | 2.1.6 | Apache-2.0 | [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) |
| SQLite | bundled through SQLitePCLRaw | Public domain | [sqlite.org](https://www.sqlite.org/copyright.html) |
| Tomlyn | 0.17.0 | BSD-2-Clause | [xoofx/Tomlyn](https://github.com/xoofx/Tomlyn) |

The self-contained release package includes `DOTNET-THIRD-PARTY-NOTICES.txt`, copied from the .NET SDK used for the build; `THIRD-PARTY-PACKAGES.txt`, generated from the application and CLI dependency graphs; and `SBOM.cdx.json`, a CycloneDX 1.5 software bill of materials generated from the published `.deps.json` files.

The Apache-2.0 and BSD-2-Clause texts used by direct production dependencies are included under `licenses/`.

## Development and test components

The repository uses Microsoft.NET.Test.Sdk under MIT terms and xUnit.net packages under Apache-2.0 terms. These test packages are not intentionally included in the end-user release ZIP.

## Scope and updates

Transitive dependencies can change when package or runtime versions change. Release maintainers should regenerate the package inventory and review this file before each release. This notice does not replace the license and notice files embedded in individual packages or the .NET runtime notice file.
