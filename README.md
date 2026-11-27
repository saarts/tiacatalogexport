# TIA Portal V20 Hardware Catalog Exporter

An x64 Windows Forms and command-line application for exporting hardware catalog rows from a local Siemens TIA Portal V20 Openness installation. It preserves the exact strings returned for `TypeIdentifier` and `TypeIdentifierNormalized`.

The application builds as .NET Framework 4.8. The portable tests exercise catalog mapping with a test double and parse the exported files. Live Openness integration, empty-search catalog coverage and CPU compatibility semantics still require validation on a Windows host with V20 installed. The application reports unverified coverage and marks CPU compatibility as unsupported.

## Build and run

Install Windows 11, Visual Studio 2022 with the **.NET desktop development** workload and .NET Framework 4.8 targeting pack, and Siemens TIA Portal V20 with Openness. Install the .NET 8 SDK to run the portable tests. Add your Windows account to the Siemens TIA Openness group if required and start a fresh Windows session after changing group membership.

Open `HardwareCatalogExporter.sln` and build Release, or run these commands from the repository root in a Visual Studio Developer PowerShell.

```powershell
msbuild .\src\HardwareCatalogExporter.csproj -restore -p:Configuration=Release
dotnet test .\tests\HardwareCatalogExporter.Tests.csproj -c Release
```

The executable and its NuGet runtime dependencies are under `src\bin\Release\net48`. The exporter loads Siemens assemblies directly from the local installation. The default directory is `C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20`. Set `TIA_V20_API_PATH` at runtime or pass `-p:SiemensApiPath=...` at build time to use another local V20 API directory. The loader also checks the `net48` subdirectory. Keep the Siemens assemblies in their installation directory.

```powershell
$exe = ".\src\bin\Release\net48\HardwareCatalogExporter.exe"
& $exe --inspect-api
& $exe
```

With no arguments, the exporter opens the GUI. Attach to a listed process or start one with or without its interface. Read and approve Siemens authorization prompts in the interactive session. Click **Extract catalog** with **API search** blank to try the broadest query available in the app. Use the grid filters and click column headers to sort. Select rows and use **Copy exact identifiers** or Ctrl+C. Export the full query result, filtered rows or selected rows. Leaving **Latest catalog version** unchecked preserves every row in the selected scope.

## Command line

```powershell
& $exe --list-processes
& $exe --attach --all --format json --output catalog.json
& $exe --attach --pid 1234 --family s7-1200 --format yaml --output s7-1200.yaml
& $exe --attach --family s7-1200-g2 --format csv --delimiter comma --output s7-1200-g2.csv
& $exe --headless --all --format json --output-dir exports --output catalog.json --verbose
& $exe --help
```

When multiple processes are visible, pass `--pid` to choose one. The family filter is a partial-text match, so `s7-1200` matches both first-generation and G2 family labels. Use the GUI generation filter for a generation-specific export. `--search` passes text to the API, while `--family`, `--article` and `--category` filter the returned rows locally.

| Code | Outcome |
| --- | --- |
| 0 | Successful operation |
| 1 | Export written with warnings. Live catalog exports currently return this code because coverage is unverified. |
| 2 | Operation failed, including unsupported CPU compatibility requests |

## Data and export behavior

- CSV uses UTF-8 with BOM, defaults to semicolon and uses quoted fields for delimiters, quotes and line breaks. CSV represents missing values as empty fields. Import identifier and article columns as text in spreadsheet software to preserve their exact values.
- JSON includes source information, counts, warnings and one object per row. YAML groups rows by classified family and article number, then lists each version with its metadata. Both formats use null for unavailable values. Rows without article numbers stay separate.
- Catalog versions are sorted numerically for recognized forms such as `V2.10.1`. Latest selection preserves all rows tied at the highest numeric version and all rows for any article with a missing or unfamiliar version. Catalog versions, firmware versions and hardware revisions are separate fields.
- Classification uses catalog labels and paths and falls back to selected order-number patterns. Unclassified values appear as **Unknown** in the grid. All rows returned by the query are eligible for export, including unclassified rows and duplicate article/version combinations.

See the [Windows verification procedure](docs/windows-validation.md) for the remaining integration checks and the evidence needed to establish catalog coverage.
