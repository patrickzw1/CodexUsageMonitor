# Third-party notices

## Fluent UI System Icons

The embedded `FluentSystemIcons-Regular.ttf` asset comes from Microsoft's
[Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons)
project and is used for interface icons. Its SHA-256 is
`9C55AC8E041AA905D2A09D4A7E57A156DECE1DF99CD64952467348DA0E158DB4`,
which was verified against upstream commit
`fb047fb395f45ccf1129f8eaee672c9dfa99152e`. The project is distributed under
the MIT License. The exact upstream `LICENSE` and `NOTICE` are included as
`licenses/Fluent-UI-System-Icons-LICENSE.txt` and
`licenses/Fluent-UI-System-Icons-NOTICE.txt`.

## .NET 8.0.30 self-contained runtime

The Windows x64 package embeds Microsoft.NETCore.App Runtime 8.0.30 and
Microsoft.WindowsDesktop.App Runtime 8.0.30. Their applicable license and
third-party notice materials are included under `licenses/` as:

- `.NET-Runtime-LICENSE.txt`
- `.NET-Runtime-THIRD-PARTY-NOTICES.txt`
- `Windows-Desktop-Runtime-LICENSE.txt`
- `Windows-Desktop-Runtime-THIRD-PARTY-NOTICES.txt`

## Microsoft.Data.Sqlite 8.0.30 and SQLitePCLRaw 2.1.13

The application uses Microsoft.Data.Sqlite from the .NET Entity Framework Core
project. The locked runtime dependency set is:

- Microsoft.Data.Sqlite 8.0.30
- Microsoft.Data.Sqlite.Core 8.0.30
- SQLitePCLRaw.bundle_e_sqlite3 2.1.13
- SQLitePCLRaw.core 2.1.13
- SQLitePCLRaw.lib.e_sqlite3 2.1.13
- SQLitePCLRaw.provider.e_sqlite3 2.1.13
- System.Memory 4.5.3

Microsoft.Data.Sqlite and System.Memory are distributed under the MIT License.
SQLitePCLRaw is distributed under Apache License 2.0; the full license is
included as `licenses/Apache-2.0.txt`. The bundled SQLite library is in the
public domain.

The Release ZIP also contains `sbom.cdx.json`, a CycloneDX release/runtime
inventory generated from the two application lock files under `src/` plus the
self-contained runtime and embedded font. Test-only dependencies are excluded
because they are not shipped in the portable package.
