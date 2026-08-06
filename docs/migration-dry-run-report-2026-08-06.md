# Remi migration dry-run report — 6 August 2026

## Outcome

The read-only migration preflight completed successfully against `D:\Projects\Remi\source-data`. It reported no validation errors and six warnings. All six warnings relate to uplift markers in payment positions in `MI Reporting Ledger.xlsx`; no other document issue was identified.

The portable datastore at `D:\Projects\Remi\publish\Remi\data` was cleaned before the preflight. The preflight was run with `--validate` and without a `--data` argument, so it did not create or update a Remi datastore.

## Dry-run summary

| Measure | Result |
|---|---:|
| Migratable source files reviewed (ledger excluded) | 149 |
| Recognised MI workbooks | 52 |
| Other evidence files | 97 |
| Contracts reconstructed | 42 |
| Invoices reconstructed | 65 |
| Supplied returns reconstructed as submitted | 52 |
| Missing historical cycles inferred as NIL returns | 41 |
| Ledger payment positions recovered | 140 |
| Existing contracts/invoices skipped | 0 / 0 |
| Validation errors | 0 |
| Validation warnings | 6 |

## Identified document issues

All findings have code `LedgerPaymentUpliftToConfirm` and severity `Warning`. For each entry, Remi retained the stated base value but requires the uplift to be confirmed before relying on the migrated payment schedule.

| # | Document | Worksheet and cell | Contract reference | Issue |
|---:|---|---|---|---|
| 1 | `MI Reporting Ledger.xlsx` | `G-Cloud 14!B7` | `KIR_202504_LLC` | Payment position includes an uplift marker; confirm the uplift. |
| 2 | `MI Reporting Ledger.xlsx` | `G-Cloud 14!B12` | `NWA_202509_GMS` | Payment position includes an uplift marker; confirm the uplift. |
| 3 | `MI Reporting Ledger.xlsx` | `G-Cloud 14!B19` | `COL_202604_LLC` | Payment position includes an uplift marker; confirm the uplift. |
| 4 | `MI Reporting Ledger.xlsx` | `G-Cloud 14!B20` | `RCL_202604_GMS` | Payment position includes an uplift marker; confirm the uplift. |
| 5 | `MI Reporting Ledger.xlsx` | `VAS!B12` | `HAV_202311_GIS` | Payment position includes an uplift marker; confirm the uplift. |
| 6 | `MI Reporting Ledger.xlsx` | `VAS!B35` | `HAV_202510_GIS` | Payment position includes an uplift marker; confirm the uplift. |

## Recommended resolution

Check the six marked payment positions against their supporting contract and pricing documents. Record the confirmed uplifted amounts or dates in Remi during the reviewed migration/import process. The warnings do not prevent a migration, but leaving them unresolved would make those payment schedules depend on unconfirmed values.

## Command used

```powershell
dotnet run --project .\src\Remi.Migration --configuration Release --no-build -- --source "D:\Projects\Remi\source-data" --validate
```

The command completed with exit code `0` and printed `Validation complete (no Remi data was written).`

## Build and clean-start verification

- The full `Remi.sln` Release build succeeded with 0 warnings and 0 errors.
- The supported portable build-and-start script completed successfully.
- The portable `Remi.exe` is listening locally at `http://127.0.0.1:5243` and `/home` returned HTTP `200` with the Remi page title.
- Remi created a fresh SQLite schema after launch. All 11 register tables contain zero rows, including `contracts`, `invoices`, `evidence`, `monthly_returns`, `mi_templates`, and `audit_events`.
