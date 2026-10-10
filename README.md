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
- Portable SQLite tables for the whole register, plus evidence and application-key storage under the data folder beside the executable
- An evidence archive that retains imported MI workbooks, contract documents, screenshots and guidance
- Source path and SHA-256 checksum recorded for each archived evidence file
- Customer organisation/URN suggestions from the current official GCA list, with the exact dated ODS retained locally as evidence
- A local SQLite database with contracts and invoices kept as structured rows
- Structured Serilog logs in `data\logs`, retained as rolling local files

The application does not alter an imported workbook. A reviewer registers the approved official template and its guidance URL, then Remi generates a review copy by updating only its Contracts and Invoices Raised tables.

## Payment schedule reconciliation

Open an existing registered invoice and use **Overview → Payment schedule reconciliation**. Choose a payment position from the invoice's contract, enter the amount excluding VAT, then select **Allocate**. An invoice can cover several positions, and several invoices can cover one position. Amounts cannot exceed the invoice's available value or the position's scheduled value. Use negative allocations for credit notes against previously allocated positions.

Fully reconciled positions disappear from **Past expected dates · needs reconciliation**. Partial allocations leave the outstanding balance visible. Removing an allocation or allocating a credit note can reopen a position. Registering an invoice alone does not create a match; Remi never guesses historical allocations or changes expected dates to clear the list. For an optional extension position, choose its confirmed **Extension agreement** when allocating the invoice. An existing invoice extension link is retained; otherwise the chosen agreement is linked explicitly. Only the selected position enters committed billing views, while unrelated optional years remain optional.

Schema version 10 adds allocation storage without changing existing register rows or creating matches. Before an upgraded portable build first opens operational data, stop the published instance and create and verify a recoverable backup of its complete `publish\Remi\data` folder, including evidence, reference data and application keys. The migration's additional SQLite backup is not a substitute for that complete backup.

## Non-framework agreements

Choose **Contracts → Non-framework agreement**, or follow the link from contract registration. Register signed, email-confirmed or verbal agreements; verbal agreements require notes describing the parties and terms. Evidence can be attached when available. Customer URN and framework MI fields are optional or omitted.

Choose a fixed term, open-ended term or annual rolling term. Rolling agreements continue automatically from one start-date anniversary to the next. Remi shows the next anniversary and its 90-calendar-day customer notice deadline on the agreement and Home; the Contracts status filter includes rolling notice deadlines. Record both the notice received date and the effective termination date in the agreement editor. Rolling termination must fall on an anniversary with at least 90 days' notice. Clearing both dates withdraws the notice. Future annual positions are cancelled for billing, while their review history is retained; allocated future periods must be reconciled before termination can be recorded.

Record the initial annual charge and, where applicable, the agreed CPI measure/reference period plus X percentage points. Each **Annual renewals and CPI reviews** action records the actual CPI rate and its source/period, compounds the previous reviewed charge by `1 + (CPI + X) / 100`, rounds to pennies, and creates that year's payment position. Reviews must be recorded in order and cover no more than the current or next annual period. Future CPI is not guessed, and future reviewed prices do not replace the current annual charge early. Missing annual prices are marked for review. Agreements without an uplift can record a zero-rate annual review. Original terms and reviewed history are retained, and the original start date/annual charge cannot change once invoices, allocations or reviews exist.

Register these invoices from **Invoices → Register** by selecting the non-framework agreement. They retain payment tracking, 30-day due dates, evidence and schedule reconciliation. Non-framework contracts, reviews and invoices never create framework monthly returns, enter MI exports/reporting cards, or appear in the monthly framework inventory. Portfolio totals show their latest reviewed annual charges separately from framework lifetime contract values.

Schema version 12 adds separate agreement-terms and annual-review tables without changing existing register rows, invoice payment statuses, evidence or application keys. Back up and verify the complete published data folder before the updated portable instance first opens it.

## Invoice payment tracking

Open a registered invoice and use **Overview → Payment tracking** to record its payment status and **Date paid**. **Mark unpaid / clear date paid** removes an assumed or previously recorded payment date. Payment corrections are audited and do not alter reporting or payment-schedule allocations. New positive invoices start unpaid; invoices whose payment status is unknown remain separate from unpaid invoices.

Due dates are calculated as invoice date plus 30 calendar days. An unpaid positive invoice becomes overdue on the following day. **Invoices → Payments** provides unpaid, overdue, unknown, paid and assumed-paid views across all reporting periods, with customer/reference search, framework filters and a CSV download of the same results. Values exclude VAT; credit notes are excluded from these views, so report totals are not net customer account balances.

Schema version 11 adds payment tracking. Its one-time migration marks existing dated invoices paid with an **assumed** date of invoice date plus 15 calendar days, as requested for the initial rollout. Undated invoices remain unknown. Each assumed date is audited; it can be corrected or cleared, and is never reapplied on restart. Before the upgraded published instance opens the register, create and verify a recoverable backup of the complete published data folder.

## Run it locally

```powershell
dotnet run --project .\src\Remi.Web
```

Open the local address displayed in the terminal. Development data is written to the project's build-output folder, not to your source workbooks.

## Create the portable Windows folder

Run `publish.bat` from the repository root. It creates `publish\Remi\Remi.exe` as a Windows x64 self-contained executable, so the recipient does not need a separate .NET installation. Run it again whenever you want to rebuild the portable folder.

Copy the resulting publish\Remi folder to a normal writable location (not Program Files) and run Start Remi.cmd. It launches Remi at a local-only address in Microsoft Edge. Closing its command window stops Remi. No installer, service or registry configuration is used.

In **Settings**, use **Refresh customer URN list** before registering a contract when you want current customer suggestions. Remi resolves the dated ODS link from the stable [GOV.UK customer-URN guidance](https://www.gov.uk/guidance/current-crown-commercial-service-suppliers-what-you-need-to-know#customer-unique-reference-number-urn-list), keeps the downloaded ODS in the local evidence archive, and records the source page, resolved URL, download time and checksum. Organisation names, eight-digit URNs and GCA-listed addresses are indexed locally so either customer field can find and populate the same record. No customer data is sent from Remi.

## Validate and migrate the existing history

First use the read-only preflight:

```powershell
dotnet run --project .\src\Remi.Migration -- --source "D:\Projects\Remi\source-data" --validate
```

Once the findings have been reviewed, rerun without --validate and point it at the specific portable Remi folder you want to populate:

    dotnet run --project .\src\Remi.Migration -- --source "D:\Projects\Remi\source-data" --data "D:\Portable Apps\Remi\data\remi-data.db"

The data folder will be created automatically. The migration retains every file beneath source-data except MI Reporting Ledger.xlsx: the 52 MI workbooks are imported as structured records and retained as originals; PDFs, screenshots and guidance are retained as evidence too. Every supplied historical MI workbook is recorded as submitted. For the frameworks represented by that history, a month that has no workbook is recorded as a NIL return. Remi records each archived file's original relative source path and SHA-256 checksum in its register, while storing the physical file as a flat hash-named copy under data/evidence. The preflight command does not need the --data argument because it does not write data.

Remi is intentionally a clean-slate application: it does not upgrade earlier prototype data files in place. If a schema reset is required, validate the original source folder and run the full repopulation command. This replaces the local SQLite register and evidence archive after a second validation pass:

```powershell
dotnet run --project .\src\Remi.Migration -- --source "D:\Projects\Remi\source-data" --data "D:\Portable Apps\Remi\data\remi-data.db" --repopulate
```

## Design

See [the product and architecture brief](docs/product-and-architecture.md) for the chosen desktop/web approach, reporting workflow and data model. Use [the Remi interface blueprint](docs/design-blueprint.md) as the definitive design contract for every UI feature and mode.
