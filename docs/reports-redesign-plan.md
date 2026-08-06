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

## Implementation decisions for the next session

1. Decide whether the return workspace is a new route or a full-width in-page panel.
2. Define the status/action state machine and exact labels.
3. Sketch the compact work-queue row and return-workspace header.
4. Implement the new page hierarchy before restyling individual controls.
5. Test draft, ready, submitted, correction-requested, nil-return, and no-activity states.

## Out of scope

- Changing report data, templates, export behaviour, or portal submission rules.
- Removing evidence or auditability; the redesign changes prominence and navigation only.
