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

## Interface design rules: typographic hierarchy and actions

Every visual treatment must have one meaning. In particular, uppercase teal text must not describe both a label and an action: this makes an otherwise borderless control ambiguous. Remi uses sentence case to describe information and uppercase only to describe an action the user can take.

### Commands and navigation

Commands are compact, text-led controls. Their priority comes primarily from colour, weight and placement, rather than from a filled surface or a large physical button.

| Context | Minimum height | Label size | Example |
| --- | ---: | ---: | --- |
| Page action | 30px | 11px | `REGISTER CONTRACT` |
| Section, toolbar or filter action | 26–28px | 10.5px | `RESET FILTERS` |
| Table-row action | 24px | 10px | `OPEN →` |

All commands use bold uppercase labels, 3–6px horizontal padding, a 4px radius, no border and no permanent fill. They centre their label and icon as one unit. Hover and active states use only a restrained teal tint that hugs the text. Keep 18–24px between adjacent page commands; page-header actions use 20px separation.

There are two command meanings:

- **Primary workflow commands** advance or change work and use teal: `PREPARE RETURN`, `REGISTER CONTRACT`, `SAVE CHANGES`. An arrow is normally omitted because the user is performing work rather than going elsewhere.
- **Secondary navigation actions** inspect a page or record and use navy: `OPEN RETURN →`, `VIEW AUDIT →`, `VIEW CONTRACT →`. The arrow consistently signals navigation.

Put page actions at the far right of the page title block, aligned with the title or first description line. Do not leave a primary command floating between the title and the next panel. A selected-record toolbar supplies the structure around its actions: for example, `REVIEW SELECTED →` is teal and `CLEAR` is muted navy.

Table actions always occupy a narrow, consistently right-aligned action column (about 70px). Do not use outlined `View` buttons in rows. Filter reset is a compact text action directly after the final filter, separated slightly from the fields; it has no pill, border or dedicated container.

Icon-only controls have zero labelled-button padding, retain a readable approximately 20px icon, and have an accessible `aria-label`. A button directly beside a dropdown may stretch its outer box to match the dropdown only when both form one visual control row; its type scale and internal padding do not change.

### Information hierarchy

Sentence case describes information. This includes page and panel headings, page context, labels, metric labels, table headings, framework names and statuses. Non-interactive headings and labels must not use teal, uppercase or command-like tracking.

| Information role | Treatment |
| --- | --- |
| Page and panel headings | Navy, sentence case, clear hierarchy; for panels use 16px / 700 weight. |
| Supporting copy | Muted blue-grey, sentence case; panel descriptions use 13px / 400 weight. |
| Context label | Optional only; muted blue-grey, sentence case, 12px / 600 weight, no letter spacing. |
| Metric label | Muted blue-grey, sentence case, 11px / 600 weight, no letter spacing. |
| Metric value | Navy, 18px / 700 weight; the value carries the emphasis. |
| Table heading | Neutral blue-grey, sentence case, 10.5px / 700 weight, no letter spacing. |
| Record data | Mixed case, bold where the value itself is important. |
| Status | Sentence-case text inside a subtle semantic pill. |

Status is never an action. Use labels such as `Submitted`, `Nil return recorded`, `Needs review` and `Evidence missing`; never uppercase them. Exception counts are semantic: zero is navy or muted, a non-blocking exception is amber, and a blocking exception is red.

### Dashboard application

The dashboard is the reference implementation for this hierarchy:

1. Do not use a redundant uppercase eyebrow. `Reporting overview` already identifies the page; use an optional quiet `Management information` context label only when it adds useful context.
2. Keep one primary page command, `PREPARE RETURN`, at the far right of the title block. Supporting page copy states the active reporting month.
3. The summary strip uses sentence-case labels (`Reporting period`, `Contracts`, `Invoices`, `Ready`, `Exceptions`) above the values. The value, not the label, is visually prominent.
4. Each panel has one direct heading and, only where helpful, accurate supporting text. For example, `Return readiness` with `Frameworks included in the June 2026 reporting period`; when there are no issues, `Needs attention` says that no validation issues were found rather than telling the user to resolve them.
5. `Recent activity` may have the navy navigation action `VIEW AUDIT →` aligned at its right. Do not duplicate it with a second `Audit trail` heading.
6. The readiness table uses sentence-case headings (`Framework`, `Contracts`, `Invoices`, `Readiness`) and a right-aligned `OPEN RETURN →` action column.

## Reporting workflow

1. Create or select the framework and reporting month.
2. Select the existing contract when recording an accounting invoice, confirm its MI designation, then enter the invoice facts for that reporting month.
3. Review the generated MI reporting card, which presents contract and invoice fields in the framework spreadsheet's order.
4. Generate and validate the return before submission.
5. Upload the current approved template to the GCA reporting portal.
6. Record the portal confirmation in Remi and mark the return as submitted.
7. Record a nil return explicitly where required. During the one-off historical source-data migration, Remi instead treats an absent workbook as a supplied NIL return: that is a property of this known reporting history, not a general rule for new work.

The ledger's `fully invoiced` and `partially invoiced` labels become a calculated view. Remi compares the value of reported invoices with the contract value until a structured annual charge schedule is recorded; it then uses the schedule total instead.

## Imported source-data baseline

The supplied workbooks contain:

| Framework | Historical MI workbooks | Contracts | Invoices |
| --- | ---: | ---: | ---: |
| G-Cloud 13 (RM1557.13) | 18 | 9 | 16 |
| G-Cloud 14 (RM1557.14) | 14 | 18 | 19 |
| Vertical Application Solutions (RM6259) | 20 | 15 | 30 |
| **Total** | **52** | **42** | **65** |

Each recognised historical MI workbook is recorded as a **submitted** monthly return. For each of the three frameworks represented in the supplied history, a reporting month found for another represented framework but without a workbook for that framework is recorded as a **NIL** return. The migration does not create retrospective returns for G-Cloud 15, because it is not part of the supplied historical source. Remi deliberately leaves the portal submission timestamp blank for these records: the evidence proves the return was supplied, but not the time at which it was submitted.

Each recognised historical MI workbook is recorded as a **submitted** monthly return. For each of the three frameworks represented in the supplied history, a reporting month found for another represented framework but without a workbook for that framework is recorded as a **NIL** return. The migration does not create retrospective returns for G-Cloud 15, because it is not part of the supplied historical source. Remi deliberately leaves the portal submission timestamp blank for these records: the evidence proves the return was supplied, but not the time at which it was submitted.

Each recognised historical MI workbook is recorded as a **submitted** monthly return. For each of the three frameworks represented in the supplied history, a reporting month found for another represented framework but without a workbook for that framework is recorded as a **NIL** return. The migration does not create retrospective returns for G-Cloud 15, because it is not part of the supplied historical source. Remi deliberately leaves the portal submission timestamp blank for these records: the evidence proves the return was supplied, but not the time at which it was submitted.

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
6. The Settings section plans a source-data migration, validates it with an in-memory register, and requires explicit review before it imports contracts, invoices and original evidence into SQLite.
7. Remi deliberately does not perform in-place upgrades of earlier prototype databases. Settings can validate a source folder and, after a separate destructive confirmation, rebuild the complete local register and evidence archive from that source.
8. Settings can refresh the customer-URN directory. Contract intake then offers local organisation/URN suggestions, while the downloaded source ODS, URL and checksum remain reviewable evidence.

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
