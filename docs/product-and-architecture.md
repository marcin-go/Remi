# Remi product and architecture brief

## Decision: local-first Blazor, not Blazor hosted in WinForms

Remi should begin as a small **Blazor Web App** running locally from a self-contained portable folder. It needs no installer, service, registry setup or per-user AppData location. It feels like a desktop application to its initial owner, but it is already the same application that can later be hosted for colleagues.

Do not make WinForms the primary host. A WinForms `BlazorWebView` can reuse Razor components, but it creates a Windows-only host layer, lifecycle differences and a second deployment path. Removing it later would still leave work to redo around storage, authentication and file access.

The boundary that preserves optionality is:

```text
Blazor UI
    │
Application use cases + reporting policy
    │
Domain records and validation
    │
Storage / template / evidence adapters
```

The current adapter stores the whole register in local SQLite tables, with original evidence files, approved workbook templates, application-protection keys and Serilog rolling logs under the data folder beside the executable. The register retains each evidence file's original relative source-data path and SHA-256 checksum, while the archive stores a flat content-addressed copy under data/evidence so the physical layout does not mirror source folders and a later revision does not replace an earlier original. The customer-URN reference index also stays in that portable data folder: it is rebuilt from the dated ODS linked by the stable GOV.UK guidance page and retains that exact ODS as evidence. A hosted deployment can replace the SQLite adapter and add authentication without replacing the UI or reporting rules.

## Reporting workflow

1. Create or select the framework and reporting month.
2. Select the existing contract when recording an accounting invoice, confirm its MI designation, then enter the invoice facts for that reporting month.
3. Review the generated MI reporting card, which presents contract and invoice fields in the framework spreadsheet's order.
4. Generate and validate the return before submission.
5. Upload the current approved template to the GCA reporting portal.
6. Record the portal confirmation in Remi and mark the return as submitted.
7. Record a nil return explicitly where required—never infer one merely because no workbook is present.

The ledger's `fully invoiced` and `partially invoiced` labels become a calculated view. Remi compares the value of reported invoices with the contract value until a structured annual charge schedule is recorded; it then uses the schedule total instead.

## Imported source-data baseline

The supplied workbooks contain:

| Framework | Historical MI workbooks | Contracts | Invoices |
| --- | ---: | ---: | ---: |
| G-Cloud 13 (RM1557.13) | 18 | 9 | 16 |
| G-Cloud 14 (RM1557.14) | 14 | 18 | 19 |
| Vertical Application Solutions (RM6259) | 20 | 15 | 30 |
| **Total** | **52** | **42** | **65** |

The migration preflight intentionally reports two errors without changing source data:

- `WYC_202507_GMS` under VAS has an end date of 17 May 2025 and a start date of 18 May 2025.
- G-Cloud 13 invoice `866` uses reference `BRG_202306_SNN`; the supplied contract reference is `BRD_202306_SNN`.

These are exactly the sort of exceptions Remi should make visible. They should be resolved through a reviewed correction, with the original imported value retained in an audit trail.

## Core data model

| Record | Key information |
| --- | --- |
| Framework | Agreement number, current reporting authority, template/version policy and deadline configuration |
| Contract | Framework, supplier reference, customer/URN, dates, lot, service/order attributes, value and first reporting month |
| Invoice | Framework, supplier reference, invoice number/date, service fields and ex-VAT value |
| Charge schedule item | Contract year, description, expected amount/date; supports several positions per year for instalment-accurate completion |
| Monthly return | Framework/month, draft/submitted/nil state, timestamp, portal reference and original workbook name |
| Evidence | Immutable original MI workbooks, order forms, pricing/dates documents, screenshots and guidance; source path, checksum and optional contract link |
| Audit event | Append-only actor, time, action, summary and correction reason |

The model deliberately retains the framework-specific fields instead of flattening everything into free text. G-Cloud needs service group and Digital Marketplace Service ID; VAS needs product/service and order-channel attributes.

## Current validation

- supplier reference, customer and positive contract value are required
- contract end date cannot precede start date
- supplier reference must be unique within a framework
- every imported invoice must match a contract in its framework
- an imported invoice cannot be duplicated
- a return with activity cannot be marked as a nil return
- errors belonging to a reporting period block recording it as submitted

Deadlines are **not** hard-coded as a legal rule. They should be stored per agreement/template and confirmed against the current GCA guidance during the template-export delivery.

## Delivered intake, template and review slice

1. Contract and invoice entry is available alongside the workbook import, with supporting contract evidence archived against the supplier reference.
2. Charge schedules retain multiple annual positions and feed the progress calculation.
3. Each framework can have one active, versioned official workbook template. Registration requires an official guidance URL and archives the exact workbook.
4. Generated `.xlsx` returns are copied from the registered template, then validated after only the Contracts and Invoices Raised table rows are replaced.
5. Material actions append audit events; a reviewer can mark a return as requiring correction with an explicit reason.
6. The Maintenance section plans a source-data migration, validates it with an in-memory register, and requires explicit review before it imports contracts, invoices and original evidence into SQLite.
7. Remi deliberately does not perform in-place upgrades of earlier prototype databases. Maintenance can validate a source folder and, after a separate destructive confirmation, rebuild the complete local register and evidence archive from that source.
8. Maintenance can refresh the customer-URN directory. Contract intake then offers local organisation/URN suggestions, while the downloaded source ODS, URL and checksum remain reviewable evidence.

## Next delivery slice

1. Add field-level record amendments with before/after values and a reviewer resolution step.
2. Record the formal GCA submission deadline and template-specific validation policy for each registered version.
3. Add automated coverage for representative G-Cloud and VAS template exports.

## Path to colleague access

When the workflow and template export are proven locally:

1. Host `Remi.Web` as an internal web app.
2. Replace SQLite with PostgreSQL or SQL Server when concurrent colleague access requires it.
3. Store evidence in a controlled file/blob store.
4. Add Microsoft Entra ID and roles: preparer, reviewer, administrator.
5. Retain the local mode for individual/offline preparation if useful.

No WinForms removal project is required because no WinForms-specific domain or UI code is introduced.
