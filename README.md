# Remi

Remi is a local-first reporting workspace for framework contracts and invoices. It replaces a free-text monthly ledger with a structured register, validation checks and a durable record of each MI return.

The first delivery is a Blazor web application designed to run as a portable Windows folder. It does not require an installer or a per-user profile: copy the published folder wherever you keep portable software, then run Start Remi.cmd. This is intentionally not a Blazor control hosted by WinForms: the same UI and application code can later be deployed securely for colleagues without a rewrite.

## What works now

- Dashboard for G-Cloud 13, G-Cloud 14 and Vertical Application Solutions (VAS)
- Contract and invoice progress based on reported invoice value
- Import of the existing CCS/GCA MI `.xlsx` template structure
- Duplicate, missing-contract, invalid-date and value validation
- Monthly return status: draft, submitted or nil return
- A no-write migration validator for a whole `source-data` folder
- Portable JSON, evidence and application-key storage under the data folder beside the executable
- An evidence archive that retains imported MI workbooks, contract documents, screenshots and guidance
- Source path and SHA-256 checksum recorded for each archived evidence file
- A remi-data.json.previous copy of the previous saved state

The application does not alter an imported workbook. Exporting an approved GCA template is deliberately the next delivery, after the current official template and its validation rules are captured.

## Run it locally

```powershell
dotnet run --project .\src\Remi.Web
```

Open the local address displayed in the terminal. Development data is written to the project's build-output folder, not to your source workbooks.

## Create the portable Windows folder

    dotnet publish .\src\Remi.Web -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\Remi

Copy the resulting publish\Remi folder to a normal writable location (not Program Files) and run Start Remi.cmd. It launches Remi at a local-only address and opens your default browser. Closing its command window stops Remi. No installer, service or registry configuration is used.

## Validate and migrate the existing history

First use the read-only preflight:

```powershell
dotnet run --project .\src\Remi.Migration -- --source "D:\Projects\Remi\source-data" --validate
```

Once the findings have been reviewed, rerun without --validate and point it at the specific portable Remi folder you want to populate:

    dotnet run --project .\src\Remi.Migration -- --source "D:\Projects\Remi\source-data" --data "D:\Portable Apps\Remi\data\remi-data.json"

The data folder will be created automatically. The migration retains every file beneath source-data except MI Reporting Ledger.xlsx: the 52 MI workbooks are imported as structured records and retained as originals; PDFs, screenshots and guidance are retained as evidence too. Each archived file has its original relative source path and SHA-256 checksum recorded in Remi. The preflight command does not need the --data argument because it does not write data.

## Design

See [the product and architecture brief](docs/product-and-architecture.md) for the chosen desktop/web approach, the reporting workflow, migration findings, data model and next delivery slice.
