# Reports Redesign Plan

## Purpose

Make monthly reporting feel like a focused workflow rather than a long page containing browsing, preparation, audit detail, evidence, and help at the same time.

## Current problem

The current Reports page shows all of the following together:

- the reporting-month/framework browser;
- the selected return's GCA summary;
- generate, submit, record, and correction actions;
- full contract and invoice field cards;
- evidence archive; and
- explanatory validation and retention text.

This repeats the selected return context and makes the important next action difficult to find.

## Target information architecture

### Reports page: monthly work queue

Keep the reporting-month selector and framework list. Each framework row should show only:

- framework and reference;
- status;
- activity counts and value where useful;
- readiness; and
- one `Open` action.

Do not expand a selected return beneath this work queue.

### Return workspace: one framework, one month

Open a dedicated view or full-width panel for a return, with a compact header:

`Reports / July 2026 / G-Cloud 14`

`Ready for review · 1 contract · 4 invoices · £70,600`

Present the task as a state-driven sequence:

1. Review data
2. Generate workbook
3. Upload to the GCA portal
4. Record submission

Only the relevant next action should be visually primary. Actions that do not apply to the current status should be hidden or placed in an overflow area, rather than appearing alongside the primary action.

### Progressive disclosure

Below the workflow, use collapsed sections:

- `Data review` — compact contract/invoice rows first; expand a row for every reporting field.
- `Checks` — show a concise pass/fail count and expand for findings.
- `Generated files` — show the latest retained workbook and its creation time.

Move permanent explanatory text such as “What Remi checks today” and evidence-retention guidance into contextual help, empty states, or an information popover.

## Design principles

- The first question answered is: “What do I need to do this month?”
- A return has one unambiguous next action.
- Audit detail remains available, but does not dominate preparation.
- Keep the GCA-style counts as a concise summary rather than a duplicate page section.
- Preserve the reporting month in the URL and make opening a return linkable.

## Implementation status

Implemented on 6 August 2026:

1. The return workspace uses a linkable route: `/reports/{frameworkCode}/{reportingMonth}?period=yyyy-MM`.
2. The Reports page is a monthly work queue only. It retains the month/framework views and each row provides one `Open` link.
3. The workspace header shows the framework, reporting month, status, activity counts and value, followed by a four-step sequence.
4. Only the next applicable action is primary: generate workbook, record portal submission, or record a nil return. A submitted return exposes correction only in a secondary disclosure.
5. Data review, Checks and Generated files use collapsed sections. Full reporting fields are expanded only for an individual contract or invoice row.
6. Workflow activity is derived from the monthly-return register counts, so a return with recorded activity cannot be offered a nil-return action when detailed card rows are unavailable.

Verification: the component suite covers the queue-to-workspace route split, and a rebuilt portable instance was checked with a populated July 2026 G-Cloud 14 return.

## Out of scope

- Changing report data, templates, export behaviour, or portal submission rules.
- Removing evidence or auditability; the redesign changes prominence and navigation only.
