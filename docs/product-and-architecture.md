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

The current adapter stores the whole register in local SQLite tables, with original evidence files, approved workbook templates, captured mail, application-protection keys and Serilog rolling logs under the data folder beside the executable. The register retains each evidence file's original relative source-data path and SHA-256 checksum, while the archive stores a flat content-addressed copy under data/evidence so the physical layout does not mirror source folders and a later revision does not replace an earlier original. The customer-URN reference index also stays in that portable data folder: it is rebuilt from the dated ODS linked by the stable GOV.UK guidance page and retains that exact ODS as evidence. A hosted deployment can replace the SQLite adapter and add authentication without replacing the UI or reporting rules.

Remi became an operational system of record on 8 August 2026. Persistent structures are therefore changed only through additive, numbered migrations. An upgraded build creates a database backup before applying a pending migration, and a release deployment takes and verifies a cold copy of the complete published data folder before the upgraded executable opens it. Prototype-style database replacement is not an upgrade strategy.

## Interface design

[The Remi interface blueprint](design-blueprint.md) is the definitive design contract for page structure, controls, view and edit modes, responsive behaviour, copy and accessibility. It supersedes earlier interface notes and screenshots whenever they conflict.

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

The migration preflight intentionally reports two errors without changing source data:

- `WYC_202507_GMS` under VAS has an end date of 17 May 2025 and a start date of 18 May 2025.
- G-Cloud 13 invoice `866` uses reference `BRG_202306_SNN`; the supplied contract reference is `BRD_202306_SNN`.

These are exactly the sort of exceptions Remi should make visible. They should be resolved through a reviewed correction, with the original imported value retained in an audit trail.

## Core data model

| Record | Key information |
| --- | --- |
| Framework | Agreement number, current reporting authority, template/version policy and deadline configuration |
| Contract | Framework, supplier reference, customer/URN, dates, lot, service/order attributes, value and first reporting month |
| Contract service part | A system or billing group within a contract, its order and actual go-live date; ordinary contracts retain one whole-contract part |
| Contract reporting occurrence | Historical evidence of the first submitted monthly return in which a contract was reported; this drives the monthly `NEW` label without a mutable flag |
| Invoice | Framework, supplier reference, invoice number/date, service fields and ex-VAT value |
| Charge schedule item | Contract year, description, expected amount/date and optional contract-part link; supports several positions per year for instalment-accurate completion |
| Monthly return | Framework/month, draft/submitted/nil state, timestamp, portal reference and original workbook name |
| Evidence | Immutable original MI workbooks, order forms, pricing/dates documents, screenshots and guidance; source path, checksum and optional contract link |
| Audit event | Append-only actor, time, action, summary and correction reason |
| Customer reference entry | Locally indexed organisation name, eight-digit URN and address from the archived GCA customer list |
| Mail template | Subject, one complete body in reading order, explicit generated-content placements, manual trigger, enablement and event-specific To/Cc/Bcc recipients |
| Captured mail | Immutable recipient snapshot, delivery key, subject, related record/period, SHA-256 and `.eml` content; Capture mode performs no external delivery |

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
6. The one-off source-data migration is run through the validated command-line workflow, which rebuilds the local register, evidence archive and reporting history from the approved source folder.
7. Published databases are upgraded only through additive, numbered migrations with preservation tests and pre-migration backup. Destructive restore remains a separate, explicitly confirmed recovery operation; it is never used as a routine upgrade.
8. Settings can refresh the customer-URN directory. Contract intake then offers local organisation/URN suggestions, while the downloaded source ODS, URL and checksum remain reviewable evidence.
9. A contract has one operational part by default. A staged contract can expose several system/billing groups with separate actual go-live dates and optional charge-schedule links. Status is derived as `Not live`, `Partially live`, `Live` or `Ended`.
10. Mail initially runs in manual Capture mode only. Every message requires an explicit user action. The monthly active-contract inventory is evaluated at the instant the user chooses Capture, and a customer-go-live message can be captured from the contract only after one or more parts are first marked live.
11. The manually triggered post-submission message is one immutable capture per reporting month. It requires every reporting framework to have a submitted or nil return plus image evidence recorded after its latest submission event. Those original evidence bytes are embedded inline beneath the G-Cloud 13, G-Cloud 14 and VAS headings; missing state blocks the capture rather than producing a misleading acceptance message.

## Mail delivery boundary

Capture, Redirect and Live are explicit delivery modes, but only Capture is enabled at go-live. Capture renders an RFC 822 message, stores it immutably under `data/mail`, records recipients and a checksum in SQLite, and never calls Mailgun or any other transport. Attempting to configure this release for Redirect or Live fails closed at startup.

The Settings > Mail screen owns event templates and their recipients. Each template has one complete message body rather than storage-shaped greeting/introduction/request/closing/signature fragments. Explicit tokens place generated blocks at the author's chosen position: `{{active_contracts}}`, `{{operational_parts}}`, `{{submission_evidence}}` and, when enabled, `{{expiring_contracts}}`. Scalar tokens such as `{{customer_name}}` and `{{reporting_month}}` may be inserted as ordinary inline text. Schema migration 4 constructs the single body from every saved legacy fragment, retains the legacy columns for backward-safe rollback, and takes an automatic pre-migration database backup.

Monthly active contracts, customer going live and the post-submission report have active manual renderers. Contracts expiring within three months and the submission-deadline reminder remain visible but disabled until their remaining selection and manual-capture rules are approved. This preserves the event contracts without pretending unfinished behaviour is live.

## Next delivery slice

1. Agree and implement the three-month contract-expiry selection rules.
2. Record the formal GCA submission deadline and enable its manual reminder action.
3. Add field-level record amendments with before/after values and a reviewer resolution step.

## Path to colleague access

When the workflow and template export are proven locally:

1. Host `Remi.Web` as an internal web app.
2. Replace SQLite with PostgreSQL or SQL Server when concurrent colleague access requires it.
3. Store evidence in a controlled file/blob store.
4. Add Microsoft Entra ID and roles: preparer, reviewer, administrator.
5. Retain the local mode for individual/offline preparation if useful.

No WinForms removal project is required because no WinForms-specific domain or UI code is introduced.
