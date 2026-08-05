# Remi

Remi is a local-first reporting workspace for framework contracts and invoices. It replaces a free-text monthly ledger with a structured register, validation checks and a durable record of each MI return.

The first delivery is a Blazor web application designed to run as a portable Windows folder. It does not require an installer or a per-user profile: copy the published folder wherever you keep portable software, then run Start Remi.cmd. This is intentionally not a Blazor control hosted by WinForms: the same UI and application code can later be deployed securely for colleagues without a rewrite.

## What works now

- Dashboard for G-Cloud 13, G-Cloud 14, G-Cloud 15 and Vertical Application Solutions (VAS)
- Contract and invoice progress based on reported invoice value
- Import of the existing CCS/GCA MI `.xlsx` template structure
- Duplicate, missing-contract, invalid-date and value validation
- Direct contract and invoice entry with annual multi-line charge schedules
- A generated, downloadable MI reporting card in the field order of the selected framework template
- Monthly return status: draft, submitted or nil return
- Versioned, approved MI workbook registration and review-copy export that preserves the workbook structure
- Append-only audit trail for data intake, template approval, export, submissions and correction requests
- A no-write migration validator for a whole `source-data` folder
- A Maintenance page that plans, validates and runs reviewed source-data imports
- Portable SQLite tables for the whole register, plus evidence and application-key storage under the data folder beside the executable
- An evidence archive that retains imported MI workbooks, contract documents, screenshots and guidance
- Source path and SHA-256 checksum recorded for each archived evidence file
- Customer organisation/URN suggestions from the current official GCA list, with the exact dated ODS retained locally as evidence
- A local SQLite database with contracts and invoices kept as structured rows
- Structured Serilog logs in `data\logs`, retained as rolling local files

The application does not alter an imported workbook. A reviewer registers the approved official template and its guidance URL, then Remi generates a review copy by updating only its Contracts and Invoices Raised tables.

## Run it locally

```powershell
dotnet run --project .\src\Remi.Web
```

Open the local address displayed in the terminal. Development data is written to the project's build-output folder, not to your source workbooks.

## Create the portable Windows folder

Run `publish.bat` from the repository root. It creates `publish\Remi\Remi.exe` as a Windows x64 self-contained executable, so the recipient does not need a separate .NET installation. Run it again whenever you want to rebuild the portable folder.

Copy the resulting publish\Remi folder to a normal writable location (not Program Files) and run Start Remi.cmd. It launches Remi at a local-only address and opens your default browser. Closing its command window stops Remi. No installer, service or registry configuration is used.

In **Maintenance**, use **Refresh customer URN list** before registering a contract when you want current customer suggestions. Remi resolves the dated ODS link from the stable [GOV.UK customer-URN guidance](https://www.gov.uk/guidance/current-crown-commercial-service-suppliers-what-you-need-to-know#customer-unique-reference-number-urn-list), keeps the downloaded ODS in the local evidence archive, and records the source page, resolved URL, download time and checksum. No customer data is sent from Remi.

## Validate and migrate the existing history

First use the read-only preflight:

```powershell
dotnet run --project .\src\Remi.Migration -- --source "D:\Projects\Remi\source-data" --validate
```

Once the findings have been reviewed, rerun without --validate and point it at the specific portable Remi folder you want to populate:

    dotnet run --project .\src\Remi.Migration -- --source "D:\Projects\Remi\source-data" --data "D:\Portable Apps\Remi\data\remi-data.db"

The data folder will be created automatically. The migration retains every file beneath source-data except MI Reporting Ledger.xlsx: the 52 MI workbooks are imported as structured records and retained as originals; PDFs, screenshots and guidance are retained as evidence too. Remi records each archived file's original relative source path and SHA-256 checksum in its register, while storing the physical file as a flat hash-named copy under data/evidence. The preflight command does not need the --data argument because it does not write data.

The portable app also exposes this workflow under **Maintenance**: use the browser-native folder chooser to select source data, create a source inventory, run the same no-write validation, review any findings, then explicitly confirm the import into SQLite. The selected files are temporarily staged with their folder hierarchy before processing.

Remi is intentionally a clean-slate application: it does not upgrade earlier prototype data files in place. If a schema reset is required, validate the original source folder and use **Rebuild all data from source** in Maintenance. This replaces the local SQLite register and evidence archive after a second validation pass. The equivalent command-line operation is:

```powershell
dotnet run --project .\src\Remi.Migration -- --source "D:\Projects\Remi\source-data" --data "D:\Portable Apps\Remi\data\remi-data.db" --repopulate
```

## Design

See [the product and architecture brief](docs/product-and-architecture.md) for the chosen desktop/web approach, the reporting workflow, migration findings, data model and next delivery slice.
