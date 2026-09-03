# Home dashboard redesign plan

Date: 3 September 2026. Status: steps 1–2 implemented in source; portable release verification and persistent forecast work pending.

## Implementation progress — 3 September 2026

The first slice implements the shared definitions and read projection from step 1. `OperationalHomeWorkspace` now supplies a single-read portfolio snapshot using grouped lookups, independent of the selected reporting period. It includes current local business date, 30/90/180-day ending filters, extension-aware commercial values, service go-live timing, dated payment positions for this and next month, and schedule coverage. It does not load evidence files or write to the register.

`ContractPortfolioRules` supplies the common lifecycle, commercial value, committed billing value and delivery calculations. The contract register and details now distinguish future starts and invalid/missing dates from Live. The invoice picker uses the current extended end date and local business date, retaining advance billing and final bills for ended contracts. A fully invoiced contract still within its extended term remains selectable. Confirmation warnings are preserved and also exposed in the contract register.

Analysis clarified three implementation rules:

- Existing data cannot prove which optional year an extension exercised. A contract with both options and a recorded extension is therefore **Association needs review**, outside the count of recorded options awaiting a decision. Multiple positions in the same optional year form one recorded option period. No absence of data is interpreted as an explicit no-further-option assessment.
- Forecasts count explicit **payment positions**, not invoices. Optional rows remain outside the committed dated forecast, even when an extension may match them; all recorded extensions remain available for association review. Dated final bills on ended contracts remain visible. Legacy plan rows are used only when there is no charge schedule, avoiding overlap.
- The existing progress calculation could use only the extension value when no payment schedule existed. Shared committed value now uses the non-optional schedule (or a legacy plan when no charge schedule exists), falling back to the base contract value, then adds recorded changes. Commercial contract value remains base value plus changes. Ambiguous contract/reference matches have unknown invoice balances in the new operational projection instead of assigning the same invoices confidently to two contracts.

Verification covers date and horizon boundaries, leap-day/year rollover and local time, historical frameworks, multiple/unconfirmed/future-dated agreements, ambiguous option associations, missing dates and zero complete schedules, legacy overlap, final billing, credits, duplicate references, service timing, reporting-period independence and read-only behaviour. Component tests compare the register's Live filter with the operational projection and check detail status labels. The accessibility test now resolves the blueprint from its existing `dev-resources` location.

Validation: `dotnet test src/Remi.Tests/Remi.Tests.csproj --no-restore --verbosity minimal` passed all **183 tests**, with none skipped. An earlier full run hit the existing attachment retry test's render timeout; the final full run passed without modifying that test. `git diff --check -- src` also passed. Tests built the application assemblies but did not launch an application instance.

The foundation was committed as `a13e1d1` (`Add shared operational dashboard foundation`).

### Second slice — operational page and navigation

The Home page now has five compact fact groups, date-coverage links, Contract endings and Billing outlook, a separate reporting-preparation summary and at most three recent audit events. The unreachable reporting-first branch has been removed and the Home design blueprint updated. Home reads the portfolio, selected-period reporting entries and audit events in one store read, reusing the Reports lifecycle projection and existing deadline policy. Submitted NIL returns stay submitted; corrections, blockers, review findings and unsubmitted empty periods stay distinguishable.

Contracts supports URL-backed status, 30/90/180-day horizon, search, framework, documents, progress, sort direction and pagination. The same portfolio predicates supply Home counts and matching records. Invoices still defaults to Registered; its Planned view lists dated positions by month, undated positions (including optional rows for review) and past expected dates needing reconciliation. It supports URL-backed month, review view, search and pagination. These are payment positions, not exact invoice counts or proof of outstanding invoices.

Contract links target the Changes tab or Payment schedule and carry a validated local return link to the source filters. New tests exercise all five fact drill-downs, filter/page restoration, month independence, submitted NIL/correction states, schedule navigation, missing-date review, and loading/error/retry/empty states. The portable package has built successfully in `.remi-build/home-dashboard`, but has not yet replaced the published files. Windows denied stopping the running Remi process on port 5243; its console needs to be closed before the complete-folder backup, portable update and browser checks.

Second-slice validation: the focused component/operational suite passed **107/107** tests. The final full-suite run passed **193/194**; the remaining failure was an intermittent final-render timeout in the pre-existing attachment workflow theory. All **16/16** attachment workflow cases passed when rerun together. The design assertion was updated for the additional ending-horizon selector. Release publishing to the staging directory and `git diff --check` passed. No operational records or published application files were changed during this slice.

Still pending: planned-invoice groups, allocations, explicit option associations and audited data review (steps 3–4). This second slice makes no schema change. Portable publishing, complete-folder backup and browser verification at the specified widths remain the release gate.

### User-authorised payment-date update — 3 September 2026

Following the separate explicit request to update schedules in bulk, `fill_payment_schedule_dates_v1.py` filled **134 missing expected dates across 38 contracts** in the existing published register. For entirely undated schedules, year 1 uses the contract start date and subsequent years use annual increments by contract year. For partially dated schedules, the operation uses the last supplied contract year's date to fill later years only; existing dates and earlier gaps are preserved. Leap-day anniversaries use the last valid day of February. The live data contained no partially dated schedules or ambiguous cases, so all 134 changes used contract starts.

All **145 positions now have dates**. The 11 previously supplied dates and all 31 optional flags remain unchanged. The operation appended 134 position audit events and 38 contract-history events; dating an optional position does not exercise it. No amounts, invoices, contracts, evidence, reference data, protection keys or schema records were changed.

Before applying the transaction, the complete data folder was backed up to `backups/payment-schedule-dates-v1-20260903T120941-0e482368/data`. SQLite's native backup incorporated committed WAL contents into the recoverable database; all 175 backup files were hash-verified. Validation passed six focused tests, a populated schema-9 rehearsal with rollback and idempotence checks, database integrity and foreign-key checks, and an exact before/after comparison allowing only the planned dates and appended audit events. Local preview, applied and verification reports are in `.remi-build/payment-schedule-dates-v1/`.

This explicitly authorised correction is separate from deployment and does not add automatic date filling to application reads or upgrades. Planned-invoice grouping, allocations, extension associations and portable UI release verification remain pending.

## Recommendation

Make Home the operational overview of the contract portfolio: what is live, what is approaching its end, what needs an extension decision, and what needs invoicing. Retain reporting preparation as a small, clearly separated section linking to Reports.

Use one compact KPI strip, two actionable working sections, and a compact reporting summary. A full dashboard should provide broader coverage and useful drill-downs, not a wall of oversized metric cards. Preserve Remi's existing typography, colours, compact tables, direct record navigation and accessibility patterns.

The contract overview can largely use existing records. Trustworthy monthly invoice counts require additional schedule dates and an explicit distinction between a payment position, a planned invoice and a registered invoice.

## 1. Inventory

### Current implementation

| Area | What exists | Implication |
| --- | --- | --- |
| Home | `Dashboard.razor`: Reporting overview, period/count strip, framework readiness table, reporting exceptions and five recent audit events | Reporting currently dominates the page; contract and invoice counts describe the selected return period, not the current portfolio |
| Dashboard query | `ReportingWorkspace.GetDashboardAsync` / `BuildDashboard`, with `DashboardModel` in `WorkspaceModels.cs` | Already supplies readiness and an all-contract projection, but mixes portfolio and reporting concerns |
| Contract term and value | Base dates/value plus separately recorded extensions and variations | Reuse extension-aware dates and agreed values; do not regress to the original end date |
| Contract lifecycle | Register labels unended contracts Live; its Ending soon filter uses 180 days | Future-start contracts currently also appear Live; shared lifecycle rules are needed |
| Operational delivery | Contract service parts with go-live dates; detail view distinguishes Not live, Partially live and Live | Commercially active and operationally live are different measures and need explicit labels |
| Optional extensions | Payment positions carry `ContractYear` and `IsOptionalExtension`; changes carry effective dates and confirmation state | Options are not awarded value. There is no explicit option-to-exercise link or definitive no-further-option field |
| Payment schedules | `ChargeScheduleItem`: description, year, optional expected invoice date, value, optional service-part association | Provides forecast inputs, but positions can be separate charges on one invoice |
| Legacy invoice plan | `InvoicePlanItem` remains in the model/store | Avoid double counting if both representations exist; the published register currently has no legacy plan rows |
| Invoices | Date, number, amount, framework/reference association; optional link to a contract change | No link to a specific payment position, no planned-invoice grouping, and no issue/payment/settlement status |
| Reports | Register and linkable per-framework/per-month workspaces; lifecycle, NIL state and deadline policy | Retain all preparation/export/submission workflows here and reuse their projections |
| Navigation | Reports preserve `period`; Contracts and Invoices mostly keep filters in component state | KPI drill-downs require explicit URL-backed filters; a link to an unfiltered register is insufficient |
| Tests | Reporting workflow, rendered register/component, design/accessibility and schema-migration coverage | Extend these suites with deterministic dashboard calculations and navigation contracts |

Source entry points:

- [Home](../src/Remi.Web/Components/Pages/Dashboard.razor), [Contracts](../src/Remi.Web/Components/Pages/Contracts.razor), [Invoices](../src/Remi.Web/Components/Pages/Invoices.razor), [Reports](../src/Remi.Web/Components/Pages/Reporting.razor).
- [Application queries](../src/Remi.Application/ReportingWorkspace.cs), [view models](../src/Remi.Application/WorkspaceModels.cs), [domain records](../src/Remi.Domain/Records.cs), [contract details](../src/Remi.Web/Components/ContractRecordView.razor).
- [Design blueprint](design-blueprint.md), [shared styles](../src/Remi.Web/wwwroot/app.css), [reporting period](../src/Remi.Web/ReportingPeriodContext.cs), [storage/migrations](../src/Remi.Infrastructure/SqliteRemiStore.cs).

### Published register snapshot

Read-only inspection of `publish/Remi/data/remi-data.db`, using SQLite read-only mode, query-only mode and read transactions. Counts below are a snapshot for **3 September 2026**, not a refreshable dashboard or verification of the underlying commercial agreements.

| Measure | Observed |
| --- | ---: |
| Registered contracts | 43 |
| Live by valid start/current end dates, including recorded extensions | 32 |
| Ended by valid dates | 9 |
| Starting in the future | 1 |
| Invalid date range, excluded from lifecycle counts | 1 |
| Live contracts with a service part already gone live | 31 |
| Ending within 90 days / 180 days | 1 / 3 |
| Of those ending within 180 days: no optional row recorded / optional year with no extension recorded | 2 / 1 |
| Recorded changes, both confirmed extensions with effective dates | 2 |
| Registered invoices | 70 |
| Payment positions / contracts with a schedule | 145 / 41 |
| Positions with an expected invoice date / without one | 11 / 134 |
| Contracts with fully dated schedules | 3 |
| Optional payment positions / contracts containing them | 31 / 26 |
| Contracts without any schedule | 2, including 1 live contract |
| Legacy invoice-plan rows | 0 |
| Invoice-to-contract-change links / position-to-service-part links | 1 / 0 |
| Persisted monthly returns / schema version | 97 / 9 |

All 70 invoices have invoice dates and match a contract by framework and normalised reference. There are no negative invoice rows or duplicate contract keys in this snapshot; those remain necessary regression cases.

Only about 8% of payment positions have explicit dates. September has no explicitly dated positions; October has one non-optional position for £21,600 ex VAT. **Neither observation establishes the complete number of invoices to issue.** Annual positions imported from the ledger intentionally have no expected date; contract anniversaries are not verified invoice dates.

The two near-end contracts with no optional row must initially be labelled **No further extension recorded**, not definitively **No extension available**. Absence of a recorded option is not proof that one does not exist.

### Existing inconsistencies to address within this work

- The contract register, contract detail and invoice-registration eligibility use different lifecycle logic. The invoice picker still checks the base end date. Share the relevant lifecycle projection so dashboard links and invoice registration agree.
- Recorded but unconfirmed changes already affect current dates/values and create warnings. Do not silently exclude them only on Home or redefine them as unexercised options. Preserve the established treatment and expose their confirmation warning consistently.
- Reporting readiness must use the existing submitted lifecycle plus NIL indicator, rather than interpreting NIL as unfinished. Do not equate no activity with a submitted NIL return or passing validation with a completed return.
- `Dashboard.razor` retains an unreachable older dashboard branch. Remove it during the page rewrite and update tests that currently assert the reporting-first layout.
- The definitive design blueprint currently prescribes a reporting-first Home. Update that Home section with the implementation while preserving its general compact-layout rules.

## 2. KPI definitions and defaults

Operational measures use today's local business date, displayed explicitly. Reporting preparation uses the selected reporting month, which defaults to the previous calendar month. On 3 September, invoice planning means **September and October**, while preparation normally means **August**. Changing the reporting month must not silently change operational KPIs.

Default ending horizon: **180 days**, matching the current register, with 30/90/180-day choices. Count distinct contract IDs, never extension rows or reporting occurrences.

| KPI | Definition and presentation | Drill-down |
| --- | --- | --- |
| Live contracts | Valid start date <= today <= current agreed end date. Include the end date itself; exclude future starts, ended contracts and invalid/unknown dates. Explain that this means within the contract term, not all services operational | Contracts filtered to Live, with matching count |
| Ending — no further extension recorded | Live contracts ending within the chosen horizon with no recorded remaining option. Separate unreviewed option information from an explicit assessment that no further option is available | Contracts filtered to this ending category, earliest end first |
| Ending — extension decision needed | Live contracts ending within the horizon with at least one remaining unexercised option. An already exercised option must not create another opportunity | Matching contracts, with remaining option and existing agreement context |
| Invoices scheduled this month | Planned invoice groups with expected issue dates in the current calendar month, plus scheduled ex-VAT amount. Show the matched/registered and remaining-to-register breakdown only where reconciliation is reliable | Invoices in a Planned view for the month |
| Invoices scheduled next month | Same definition for the next calendar month, with actual month names rather than ambiguous relative labels | Planned view for the next month |

Additional rules:

- A recorded extension updates the current term; a future optional year does not. Multiple payment positions in one optional year represent one option period, not several extension opportunities.
- Classify option information as available, exhausted/no further option, or unknown. An agreed extension awaiting confirmation is a separate warning, not another available option.
- Do not assume every agreement exercises every option. Partial, successive or ambiguous exercises require explicit association/review. Date/year comparisons may suggest a match, not silently persist one.
- Retain contracts under historical frameworks in portfolio counts if the contracts themselves are still active.
- Keep scheduled charges on ended contracts visible when still relevant to billing; do not discard a valid final bill simply because the term has ended.
- Unknown dates and incomplete option information get visible coverage counts. Never render them as a confident zero.
- Use a single injected `TimeProvider` and common local-date rules. Historical report selection is not an historical reconstruction of the whole portfolio.

## 3. Recommended additional information

Prioritise information that leads to a concrete action:

1. **Schedule completeness:** contracts without a schedule, positions without dates, and extension/billing associations needing review. This is essential while 134 positions remain undated.
2. **Billing overdue for review:** expected issue date has passed and an explicitly reconciled planned invoice remains outstanding. Until matched, call it Needs reconciliation; do not assert that an invoice was never issued.
3. **Ended contracts with value still to invoice:** show count and remaining recorded committed value, excluding unexercised options. Label it a register-based invoicing position, not customer debt, cash outstanding or guaranteed revenue.
4. **Reporting deadlines and blockers:** selected-period unfinished returns, next configured deadline and blocking errors, contained in the compact reporting section.

Secondary, quiet context: total value of live contracts; starting soon or active contracts not yet operational; at most three recent audit events with View audit. Keep these subordinate to the main five KPIs.

Defer customer concentration, historical trends and a six-month forecast chart until the core dashboard is useful and schedule coverage supports meaningful interpretation. Do not add cash-flow, late-payment, profit or renewal-probability KPIs: Remi lacks the facts needed to substantiate them.

## 4. Proposed page composition

```text
Home                       As at 3 Sep 2026     Ending within [180 days]

Live contracts | Ending: no further option recorded | Extension decisions
Invoices scheduled: September [count, value] | October [count, value]
Coverage: dated schedules, missing dates and items needing reconciliation

Contract endings                         Billing outlook
Reference / customer / end / option      September | October | Needs review
Earliest and most urgent, max 5 rows     Contract / expected date / amount / state
View matching contracts                 View planned invoices

Reporting preparation — August 2026                         Open reports
Submitted x/y | Ready to review x | Blocked x | Next deadline
Compact framework/status rows, each with Open

Recent activity — at most 3 entries                           View audit
```

This is a structure, not a pixel mockup. Implement the five KPI groups as a unified responsive fact strip, not five separate oversized cards. Give Contract endings and Billing outlook the main working space. Keep reporting to a small summary and a few framework rows, without duplicating its workflow tabs, full findings, workbook/evidence panels or submission actions.

Use existing tokens, restrained semantic status colours and text labels. On narrower screens stack the working sections, wrap the fact strip and keep table overflow within its section. Preserve keyboard-visible focus, descriptive links, adequate touch targets and accessible loading/empty/error states. No new charting or frontend framework is needed.

## 5. Invoice forecasting: required data work

This is the main dependency, not merely a UI calculation.

### Usable first dashboard

- Project explicit expected dates and values from the existing schedule without mutating it.
- Show known scheduled payment positions and date coverage until planned-invoice grouping is confirmed. Use an honest interim label, not an exact invoice-to-issue count.
- Put missing dates and unmatched exercised extensions in a review queue with direct links to the contract's payment schedule or changes section.
- Offer start/go-live anniversaries as suggestions only when the user reviews schedules. Never mass-fill the 134 missing dates automatically.
- Exclude unexercised options from committed billing. An exercised option must be associated with its agreement and billing schedule before it contributes to a reliable dated forecast; unresolved cases remain visible.

### Complete requested invoice KPIs

Add the smallest persistent structures needed to make the count and remaining work defensible:

1. **Planned invoice grouping:** explicitly group one or more payment positions into an expected invoice with an expected issue date. Support instalments/split positions and combined charges; do not assume one payment row equals one invoice.
2. **Invoice allocations:** associate registered invoices with planned invoices, including allocated ex-VAT amounts. Support partial billing, multiple actual invoices, combined invoices and credit adjustments. Enforce amount limits, contract consistency and audited corrections.
3. **Extension associations and assessment:** record which option period(s) an agreement exercises, which planned charges it activates, and whether remaining option information has been reviewed. Keep original optional schedule information and agreement evidence intact.

New links are optional for historical rows. Migration must not guess historical invoice matches or mark existing options exhausted. Matching suggestions can use contract, invoice date, amount and an existing change link; users confirm material associations through the existing register/detail workflow.

The completed billing view distinguishes **Scheduled**, **Registered against plan**, **Remaining to register**, and **Needs reconciliation**. Remi records accounting invoices; it does not currently issue them or prove whether an unregistered invoice exists elsewhere. The dashboard must state that limitation.

## 6. Implementation sequence

1. **Shared definitions and tests.** Introduce a dedicated operational Home read model/service, keeping report-period readiness separate. Share lifecycle/extension/value rules with Contracts, details and invoice eligibility. Use grouped lookups over one coherent store read; avoid per-contract evidence loading or one query per widget. Preserve existing report/export calculations.
2. **Dashboard and actionable navigation.** Build the compact KPI strip, endings list, date-coverage-aware billing outlook and small Reports summary. Add URL-backed contract filters and a Planned view within Invoices; existing invoice records remain the default register view. Add context-preserving routes to relevant contract sections. Use the same predicates for KPIs and drill-down results.
3. **Complete forecast semantics.** Implement additive grouping/allocation/option-association storage and the minimum review controls in schedule, change and invoice workflows. Replace interim position counts with planned-invoice counts only when those associations justify them. Expose incomplete coverage rather than requiring all historical data to be rewritten before Home works.
4. **Data review, explicitly separate from deployment.** Let the user confirm missing billing dates, planned groups and extension matches through audited edits. Reconcile current/next month first, then improve older and later schedules. No source-data reimport or automatic rewrite is part of deployment.
5. **Verification and portable release.** Run relevant calculation, component, accessibility and migration tests, then the full suite. Back up and verify the complete published data folder before the upgraded portable build first opens it. Rebuild/publish and use only `publish/Remi` with its existing data. Perform rendered verification at desktop, intermediate and mobile widths.

Steps 1–2 can deliver a useful first slice without a schema change, but they do **not** alone fulfil a reliable remaining-invoices KPI. Steps 3–4 complete that capability; reporting/date coverage must make partial readiness visible throughout.

## 7. Acceptance criteria

- The five requested KPI groups appear before the reporting-preparation section, and every actionable count opens its exact underlying records or plan items.
- Home, contract register, detail and invoice eligibility agree on lifecycle and extension-aware values; future starts and invalid dates do not inflate Live.
- The default 180-day ending window is explicit; 30/90/180 boundaries, today, leap years, month/year rollover and future agreement dates are tested.
- Multiple extensions, exercised options, unused later options, unconfirmed agreements, ambiguous associations and no recorded option produce distinct, explainable results without double counting.
- Monthly forecasts distinguish positions from invoices, include final billing after a term ends where applicable, and exclude unexercised options from committed amounts.
- Test missing dates, zero complete schedules, legacy-plan overlap, service-part timing, split/combined invoices, partial allocations, early/late registration, credits and unmatched invoices.
- September/October planning remains September/October when the reporting preparation period changes. Browser back/forward preserves dashboard drill-down filters.
- Submitted NIL returns count as submitted; empty unsubmitted periods remain actionable; correction-required returns and blocking/review findings remain distinguishable. Reuse configured deadline policy and label dates accordingly, without claiming a new externally verified deadline rule.
- Migration tests upgrade a populated version-9 fixture without losing contracts, invoices, evidence metadata, submission history, reference data or keys; rerunning migration is safe. Runtime protection-key/evidence preservation is also checked against the verified complete-folder backup.
- No GET/read path records data, exercises an option, creates an invoice, confirms a NIL return or changes a submission. No completed-monthly-workbook import workflow is introduced.
- Layout and keyboard behaviour are checked at approximately 1440, 1024, 768 and 390px widths, including empty, loading, error and partially populated states.

## Original investigation boundary (before implementation)

This plan is based on current source, existing test contracts and a read-only aggregate inspection of the operational register. No application source, operational records, schemas, evidence or keys were changed. The portable application was not started or rebuilt, and no live rendered UI verification or new test run was performed for this planning task.
