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

The current adapter stores JSON, original evidence files and application-protection keys under the data folder beside the executable; each data save keeps a one-generation remi-data.json.previous copy. Evidence filenames retain the original relative source-data path and a SHA-256 checksum, while the archive uses content-addressed copies so a later revision does not replace an earlier original. A hosted deployment replaces it with a database adapter and adds authentication; it does not replace the UI or the reporting rules.

## Reporting workflow

1. Create or select the framework and reporting month.
2. Enter activity or import the official MI workbook for that framework/month.
3. Validate the return before submission.
4. Upload the current approved template to the GCA reporting portal.
5. Record the portal confirmation in Remi and mark the return as submitted.
6. Record a nil return explicitly where required—never infer one merely because no workbook is present.

The ledger's `fully invoiced` and `partially invoiced` labels become a calculated view. Initially Remi compares the value of reported invoices with the contract value. The next data slice adds an invoice plan, allowing progress to use expected instalments instead.

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
| Invoice plan item | Expected amount/date; optional initially, required for instalment-accurate completion |
| Monthly return | Framework/month, draft/submitted/nil state, timestamp, portal reference and original workbook name |
| Evidence | Immutable original MI workbooks, order forms, pricing/dates documents, screenshots and guidance; source path, checksum and optional contract link |
| Audit event | Actor, time, field-level change and correction reason (next slice) |

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

## Next delivery slice

1. Add the contract/invoice entry form and an invoice-plan editor.
2. Capture the latest approved GCA templates as versioned framework configuration.
3. Generate and validate an untouched-format `.xlsx` return from a selected reporting period.
4. Add immutable audit events and review/correction workflow.

## Path to colleague access

When the workflow and template export are proven locally:

1. Host `Remi.Web` as an internal web app.
2. Replace `IRemiStore`'s JSON implementation with PostgreSQL or SQL Server.
3. Store evidence in a controlled file/blob store.
4. Add Microsoft Entra ID and roles: preparer, reviewer, administrator.
5. Retain the local mode for individual/offline preparation if useful.

No WinForms removal project is required because no WinForms-specific domain or UI code is introduced.
