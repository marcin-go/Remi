# Register-first redesign implementation plan

## Purpose

Evolve Remi into a calm, auditable, data-dense reporting register. The redesign retains the established navy and teal identity while reducing presentation layers around everyday work.

The intended experience is **quiet assurance**: precise, calm, authoritative and visibly auditable. Remi remains distinct from an activity-led dashboard by making records, validation and evidence the centre of the interface.

## Delivery status

- **Completed:** plan documentation, global reporting-period context, shared design tokens, operational dashboard, full-width Contracts/Invoices registers, consistent contract/invoice detail views, and Phase 5 quality gates.
- **Verified:** 22 automated application, component, accessibility and workbook-export tests; responsive register checks at 1440px, 1024px and 390px; a zero-warning solution build; and migration-tool startup.
- **Release follow-up:** validate a supplied production G-Cloud/VAS source-workbook set through the migration tool before a production data migration. No source workbooks are stored in this repository.

## Product decisions

1. Contracts and invoices are persistent registers, not stages in a workflow.
2. The selected reporting period is visible throughout the application. It is carried in a `?period=yyyy-MM` query parameter so it survives navigation and can be shared.
3. Registers retain their full historical data by default. The active period is a visible context and an available quick filter; it must not silently hide records.
4. The four-step process appears only in the dedicated monthly-return workspace.
5. A selected-record summary appears only when records are selected. Detailed selection information opens in a temporary right-side drawer.
6. Existing evidence, validation and audit data remain close to each record. No data is discarded for the visual redesign.

## Design system

Use semantic CSS variables rather than repeated literal colours.

| Token | Starting value | Use |
| --- | --- | --- |
| `--color-navy` | `#12324A` | Headings and high-emphasis text |
| `--color-teal` | `#0B918F` | Primary actions, selection and current location |
| `--color-teal-hover` | `#087977` | Primary-action hover |
| `--color-canvas` | `#F4F7F8` | Application background |
| `--color-surface` | `#FFFFFF` | Primary working surface |
| `--color-border` | `#D7E1E5` | Surface and input borders |
| `--color-text` | `#172D3C` | Primary text |
| `--color-text-muted` | `#5E7482` | Supporting text |
| `--color-success` | `#23845A` | Verified and complete states |
| `--color-warning` | `#A96500` | Review-needed and approaching-expiry states |
| `--color-error` | `#C54242` | Invalid or submission-blocking states |

Use only three surface levels:

1. application canvas;
2. one white working surface for a page's main task;
3. elevated temporary controls such as drawers, dialogs, menus and popovers.

Target typography:

| Role | Size / line height | Weight |
| --- | --- | --- |
| Page title | 32px / 38px | semibold |
| Section title | 20px / 28px | semibold |
| Card title | 16px / 24px | semibold |
| Body | 14-15px / 21px | regular |
| Table body | 13-14px / 19px | regular |
| Metadata | 12px / 17px | regular |

All final token combinations and interactive controls must meet WCAG AA contrast requirements.

## Phase 1: common context and foundations

### Reporting-period context

**Targets**

- `src/Remi.Web/Components/Layout/MainLayout.razor`
- new `src/Remi.Web/ReportingPeriodContext.cs`
- `src/Remi.Web/Program.cs`
- `src/Remi.Application/ReportingWorkspace.cs`
- `src/Remi.Application/WorkspaceModels.cs`

**Work**

1. Create a scoped reporting-period context service that owns the selected `yyyy-MM` period, validates input and publishes change notifications.
2. Read the `period` query parameter on navigation; add it to primary navigation links.
3. Add a compact period selector to the shared header.
4. Expose available reporting periods from structured contract, invoice and return data, and default to the latest available period (falling back to the previous calendar month only when data is absent).
5. Add optional period parameters to dashboard and register queries. Existing register data remains visible unless the user chooses a period-specific filter.
6. Replace page-level `DateTime.Today.AddMonths(-1)` defaults with the shared selected period.

**Acceptance criteria**

- Selecting July 2026 keeps July active across Dashboard, Contracts, Invoices and Monthly returns.
- Direct navigation to `?period=2026-07` selects July 2026.
- An invalid or unavailable query value is rejected safely and replaced by the default period.
- No existing import, export or validation logic changes its stored reporting month.

### Visual foundations

**Targets**

- `src/Remi.Web/wwwroot/app.css`

**Work**

1. Introduce semantic colour, type, radius, spacing and shadow tokens.
2. Migrate global typography, buttons, inputs, navigation and panel primitives to those tokens.
3. Make filled teal the primary-action style and reserve navy for heading/high-emphasis text.
4. Remove default shadows from persistent register and dashboard surfaces; retain elevation for temporary UI only.

**Acceptance criteria**

- The existing pages render unchanged functionally after the token migration.
- Status styles convey success, warning and error without relying on teal.
- No new literal colour values are introduced outside the token definitions.

## Phase 2: operational dashboard

**Targets**

- `src/Remi.Web/Components/Pages/Dashboard.razor`
- `src/Remi.Application/ReportingWorkspace.cs`
- `src/Remi.Application/WorkspaceModels.cs`

**Work**

1. Replace the large proposition header and framework card grid with compact current-period metrics.
2. Add a flat framework-readiness table: contracts, invoices, readiness and concise next action.
3. Group validation into **Blocking preparation** and **Review recommended**, using actionable links to filtered records.
4. Add the five most recent material audit events to the dashboard model.
5. Give each framework row a filtered-register destination carrying the active reporting period.

**Acceptance criteria**

- A user can identify the active period, blockers, return readiness and recent changes without scrolling.
- A progress value over 100% is warning or error styled and remains explicit about the exception.
- The dashboard has no more than one primary action.

## Phase 3: register-first Contracts and Invoices

**Targets**

- `src/Remi.Web/Components/Pages/Contracts.razor`
- `src/Remi.Web/Components/Pages/Invoices.razor`
- new shared register toolbar/drawer components as needed
- `src/Remi.Web/wwwroot/app.css`

**Work**

1. Remove the permanent breadcrumb, workflow stepper and separate context card from both registers.
2. Use a compact header: title, active period and one page-specific primary action. Cross-workflow actions become secondary.
3. Merge filters, selection state and table within one working surface.
4. Hide the selection interface until the first record is selected. Show a sticky bar with count, combined value, unresolved exceptions, clear action and review action.
5. Provide a right-side drawer for detailed selected-record information when requested; never reserve a blank column.
6. Make rows keyboard-accessible and clickable without compromising checkbox actions.
7. Apply sticky headers, tabular numbers, right-aligned currency, frozen identity columns on wide screens and restrained row-hover states.
8. Condense all-passing invoice checks while retaining explicit failing checks.

**Acceptance criteria**

- At desktop width the unselected table uses the full page width and shows at least five more rows than the current design.
- At zero selection no selection sidebar or empty summary is rendered.
- Selection, filters, pagination and return-to-register navigation retain their expected behaviour.
- At 200% or greater contract progress, the exception has warning/error styling and accessible explanatory text.

## Phase 4: record-detail consistency

**Targets**

- `src/Remi.Web/Components/ContractRecordView.razor`
- `src/Remi.Web/Components/Pages/InvoiceDetails.razor`
- `src/Remi.Web/Components/Pages/ContractDetails.razor`

**Work**

1. Treat `ContractRecordView` as the reference detail pattern: main details, related records, evidence, validation and history.
2. Build an equivalent invoice detail view and extract shared primitives where this reduces duplication.
3. Retain full-page detail routes first. Add a wide edit drawer for routine register edits only after the full-page model is stable.
4. Preserve filters, period, current page and selection on return from a full-page record.
5. Once in-progress detail work is confirmed, remove the unreachable legacy contract-detail branch instead of maintaining two implementations.

**Acceptance criteria**

- Contract and invoice records present evidence, validation, related records and audit history in the same predictable locations.
- Closing an edit drawer or returning from a detail route restores the prior register state.
- No record mutation bypasses the existing audit trail.

## Phase 5: quality gates and release

**Work**

1. Add application tests for period resolution, readiness grouping, validation severity and filter-route construction.
2. Add component tests for header context, zero-selection state and selection toolbar behaviour.
3. Add browser visual checks at desktop, tablet and mobile widths, plus keyboard navigation checks.
4. Test contrast for all tokens and status combinations; verify touch targets and focus visibility.
5. Run existing build, migration validation and representative G-Cloud/VAS return-export checks.

**Acceptance criteria**

- Automated tests cover every period-related query and all register selection transitions.
- Visual checks pass at 1440px, 1024px and 390px widths.
- `dotnet build` has no warnings or errors and representative export validation remains unchanged.

## Implementation sequence

1. Deliver Phase 1 as a focused foundation change.
2. Deliver the dashboard as a separate reviewable change.
3. Deliver Contracts and Invoices together because they share selection and table behaviour.
4. Consolidate details after the current contract-detail work is merged.
5. Add automated coverage before release, then perform an evidence-backed visual QA pass.

## Guardrails

- Do not change or discard stored contract, invoice, evidence, monthly-return or audit data for this redesign.
- Do not infer a nil return from missing activity.
- Do not make reporting deadlines a hard-coded legal rule.
- Preserve the existing portable/local-first deployment model.
- Keep the current uncommitted payment-schedule, migration and record-detail work intact unless explicitly included in a reviewed change.
