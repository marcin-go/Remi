# Remi interface blueprint

This is the definitive interface contract for Remi. New pages, features, view modes and edit modes must follow it. If an older plan, screenshot or implementation conflicts with this document, this document wins.

Remi is a calm, compact reporting workspace. It should feel deliberate and trustworthy: strong information hierarchy, restrained colour, evidence close to the work, and as little ornamental interface chrome as possible.

## Non-negotiable rules

1. **Actions use plain verbs without arrows.** Use `Open`, `Back`, `Register`, `Prepare`, `Save`, `Download` and `View audit`. Do not add `→`, `←`, `↗` or similar directional punctuation to a labelled action.
2. **Simple facts form a compact strip, not a dashboard.** Put related facts on one line with quiet vertical separators. Do not turn each value into a card, tile or large metric block.
3. **Registers open records directly.** Contract and invoice lists do not have row checkboxes, select-all controls, selection toolbars, selection drawers or a “review selected” workflow. The record reference is the row's primary route.
4. **Do not use decorative eyebrows.** Start a page or section with its real heading. A second title-like line above it is allowed only when it communicates operational state that would otherwise be lost; it must be a status treatment, not a generic category label.
5. **Floating labels are the default form control.** Text fields, dates, numbers, search fields and selects keep a persistent label on the field border. The label remains visible when the field is empty, filled, focused, disabled or invalid.
6. **One visual treatment has one meaning.** Teal identifies interaction and the current choice. Navy carries content and hierarchy. Status colours communicate status only. Borders structure content; shadows are reserved for temporary layers.

## Character and visual tokens

The implemented tokens in `src/Remi.Web/wwwroot/app.css` are the source of truth.

| Role | Token | Value |
| --- | --- | --- |
| Primary content and command text | `--color-navy` | `#12324A` |
| Interaction and active state | `--color-teal` | `#087977` |
| Interaction hover | `--color-teal-hover` | `#056B69` |
| Application canvas | `--color-canvas` | `#F4F7F8` |
| Surface | `--color-surface` | `#FFFFFF` |
| Structural border | `--color-border` | `#D7E1E5` |
| Resting control border | `--color-control-border` | `#B9CBD4` |
| Main text | `--color-text` | `#172D3C` |
| Supporting text | `--color-text-muted` | `#5E7482` |
| Success / warning / error | semantic tokens | `#1B704B` / `#895100` / `#A93636` |
| Control radius | `--radius-control` | `0.5rem` |
| Surface radius | `--radius-surface` | `0.75rem` |

Use Segoe UI through `--font-sans`. Page headings are navy, sentence case and compact. Body copy is sentence case. Commands are the only routinely uppercase treatment; CSS supplies the casing, so Razor labels should still be written naturally (`Save contract`, not `SAVE CONTRACT`).

## Layout grammar

### Application shell

- The 64px sticky header contains the Remi brand, quiet product context and five primary destinations.
- Main content uses the full available width. Pages do not invent isolated max-width containers unless a reading or data-entry task clearly benefits from one.
- The canvas separates working surfaces. White surfaces group a real unit of work; do not add a surface solely to decorate a heading or metric.

### Page header

- One `h1`, optionally followed by one concise sentence of context.
- Put the single most important page action at the far right.
- Do not add a breadcrumb when a clear `Back` action is enough.
- Do not add a category eyebrow above the `h1`.

### Fact strip

Use a fact strip for two to six summary values such as reporting period, counts, money, dates and record status.

- One horizontal line on wide screens.
- Quiet top and bottom borders and vertical separators between facts.
- Label first, value second; neither becomes a tile.
- Values use tabular numerals where appropriate.
- Supporting text may follow a value on the same compact line.
- At narrow widths, wrap into two columns and then one column while preserving separators.

Implemented patterns: `.dashboard-period-summary`, `.dashboard-period-current`, `.dashboard-metrics`, `.contract-summary-grid`, `.contract-summary-item`.

### Sections and surfaces

- A section header contains one meaningful `h2` and optional supporting copy.
- Remove duplicated category labels such as `Reference data`, `Registered information`, `Settings menu`, `Audit trail` or `Monthly return` when the heading already names the content.
- Prefer a border or spacing change over nesting a card inside another card.
- Permanent surfaces use no shadow. Temporary UI such as menus, drawers and modal progress states may use `--shadow-temporary`.

## Controls

### Actions

| Tier | Use | Treatment |
| --- | --- | --- |
| Primary | Advance or commit the current workflow | Teal text, compact, top/right or form footer |
| Secondary | Navigate, cancel, edit, download or reveal | Navy text, compact |
| Destructive | Irreversible replacement or removal | Red text and explicit confirmation |
| Row | Open the row's record/workspace | Right aligned text action, or the record reference link |

Rules:

- Start labels with a specific verb.
- Never append arrows to a labelled action.
- Use one primary action per local context.
- Disabled controls remain visible and explainable; do not use colour alone to indicate availability.
- Icon-only controls need an accessible name and must be unmistakable without a text arrow.

### Floating fields

Use `.floating-field` with a `.floating-label`. Use `.floating-field--static` for Remi selectors, dates, numbers and filters so the label is always settled on the border.

```razor
<label class="floating-field floating-field--static">
    <input @bind="reportingMonth" placeholder="YYYY-MM" />
    <span class="floating-label">Reporting month</span>
</label>
```

- The resting border is neutral; teal appears on focus and active interaction.
- Keep the label visible for filled, empty, disabled and invalid states.
- Currency fields keep the symbol inside the control and align the numeric value right.
- Invalid fields use the error token and a nearby sentence explaining how to recover.
- Register filters use the same floating treatment. Their compact toolbar layout, quick filters and trailing `Reset` action distinguish them from transaction forms.

### Compact selectors

- Use Remi's floating trigger and anchored `role="listbox"` for two to five short, finite choices such as framework, lot or order channel.
- The closed trigger shows the current choice and a quiet disclosure chevron. The open panel shows every choice without a search box and never leaves the page for a browser-native menu.
- Dependent selectors remain visible and labelled while disabled. Their placeholder explains the prerequisite, for example `Select a lot first`.
- Do not use a native `<select>` in Remi transaction forms. Native `date` and numeric inputs remain appropriate because their platform keyboards and pickers add value.

### Searchable picklists

- Use a floating-label text input with `role="combobox"` and a linked `role="listbox"`; do not place a second select beside it.
- Focusing the input opens the list immediately and shows the complete candidate set, even when the input already contains a selected value.
- The list viewport is at most five rows high. Keep every matching item in the list and use vertical scrolling for the remainder; never truncate the result set to five records.
- When a selected value is already present, open the list with that option visible rather than returning the user to the beginning of a long list.
- Filter case-insensitively on every keystroke. The list becomes shorter as candidates stop matching and returns to the complete set when the query is cleared.
- Each row is one `role="option"` with the identifying value first and enough context to disambiguate it.
- `Escape`, moving focus away, or pressing anywhere outside the picker closes the list. Pressing inside the option panel must commit the choice before the list closes and leave the chosen description in the input. Preserve `aria-expanded`, `aria-controls` and `aria-selected` states.
- Treat the list as a temporary layer: use a quiet border and restrained shadow, with teal reserved for hover, focus and the current choice.
- Use Remi's shared searchable-picklist result panel, not the browser-native `datalist`. Large local reference sets may virtualise off-screen rows, but scrolling must still reach every candidate.

For linked reference data such as the GCA customer directory:

- Both the organisation-name and URN fields search the same locally stored directory. Choosing from either field updates the pair together.
- Include the organisation name, eight-digit URN and address in the option context so similar names can be distinguished before selection.
- After selection, show the address from the GCA URN list as a quiet verification fact beside the fields. It is a disambiguation cue, not evidence that the organisation is eligible to use a particular agreement.

### Quick filters

- Use pills only for optional, reversible shortcuts such as `Documents missing` or `Selected period`.
- An active quick filter uses a quiet teal tint and teal border.
- Do not add a quick filter that represents hidden UI state, such as selected rows.

### Status

- Status is sentence case in a subtle semantic pill.
- Green means complete/good, amber means review, red means blocking/error and neutral grey means inactive/draft.
- A status is never styled like an action and is never clickable unless it is actually a filter.

## Page blueprints

### Home

1. Page header with `Home`, the local business date and a compact `Ending within` selector (30/90/180 days; default 180).
2. One responsive fact strip: Live contracts, endings with no further extension recorded, extension decisions, and scheduled billing for the current and next calendar months. Use **scheduled payment positions** until explicit invoice groups and allocations support invoice counts. Keep date/association coverage directly below the facts.
3. Contract endings and Billing outlook are the main working surfaces, with at most five rows each and links to the exact full results. Keep ambiguous exercised options in a separate review count; never imply no option exists merely because none is recorded.
4. Reporting preparation is a compact selected-month summary below the operational work, with submitted/ready/blocked counts, configured deadlines and framework links. Submitted NIL returns are submitted. Empty unsubmitted months remain actionable. Changing this reporting month must not change the operational date or billing months.
5. At most three recent audit events, with `View audit`. Preserve compact tables, visible keyboard focus and horizontal table scrolling within each section at narrow widths.
6. Home fact links use URL-backed filters in Contracts and the Planned view of Invoices. The Registered invoice view remains the default. Preserve month, ending horizon, search/sort/page and the relevant contract section in navigation and return links.

### Contract and invoice registers

1. Page header with `Contracts` or `Invoices` and `Register`.
2. One register surface containing result count, floating search/filter fields, quick filters, sortable table and pagination.
3. The reference/designation is the direct route to the record.
4. No row selectors, selection mode, selection summary, drawer or bulk review.

### Record view

1. Record identity, status, one context line and compact actions.
2. One fact strip for the highest-value summary facts.
3. Tabs for genuinely different modes: overview, changes/invoices where relevant, documents and history.
4. Overview uses clear `h2`/`h3` hierarchy and definition lists. Do not prefix headings with decorative symbols or eyebrows.
5. `Edit` switches the relevant content area into floating fields without moving Save/Cancel to an unrelated location.

### Registration and edit modes

- Keep the same page identity and context as the corresponding view mode.
- Group fields by user task, not by database entity.
- Use floating labels throughout.
- Use searchable Remi picklists for long or descriptive choice sets and compact Remi selectors for short sets; do not mix these with native selection menus.
- Keep evidence visible beside the form on wide screens and below it on narrow screens.
- Put `Cancel` and `Save` in a stable local footer or section header. Do not duplicate them at both page and section level.

### Reports

- The register is a sortable/browsable table with direct `Open` actions.
- A return workspace keeps framework, month and reporting summary visible, then uses four clickable workflow tabs: Review data, Generate workbook, Upload to GCA and Record submission.
- The workflow tabs communicate both progress and navigation; they do not lock users into a wizard.
- Checks and report rows belong to Review data; generated workbooks belong to Generate workbook; GCA instructions belong to Upload to GCA; task details, evidence, history and corrections belong to Record submission.

### Settings

- Use the persistent left settings navigation on wide screens and a compact horizontal/stacked navigation on narrow screens.
- The content surface begins directly with its real `h2`.
- Do not repeat `Settings`, `Settings menu`, `Reference data`, `Backup` or `Restore` as eyebrow labels.
- Destructive restore remains visually distinct through border, warning copy and confirmation—not through extra title rows.

### Contract operational delivery

- An ordinary contract presents one floating `Actual go-live date` control. Do not make the common case administer a one-row subtable.
- A single `This contract goes live in stages` switch reveals operational parts only when needed.
- A part represents systems that go live and are normally billed together; its fields are `System or billing group` and `Actual go-live date`.
- Derive `Not live`, `Partially live`, `Live` and `Ended` from dates. Do not store a second status flag that can disagree with them.
- Payment positions may link to a part using a Remi picklist. The link is optional because some charges cover the complete contract.
- Moving a blank go-live date to a date is an operational event. Correcting an existing date is a record correction, not a second go-live event.

### Mail under Settings

- Mail remains a Settings destination; it does not become a sixth primary navigation item.
- Lead with the current delivery mode. In Capture mode say literally that Remi retains messages and sends nothing.
- The event list is navigation, not a dashboard. Show event name plus Enabled/Disabled and Manual/Automatic status.
- To, Cc and Bcc belong to each event template. Use floating fields and permit several addresses without adding a recipient-management subsystem.
- Keep the subject separate, then present one full-width Message field containing the complete message in reading order. Internal rendering stages must never appear as separate copy fields.
- Show the event's available placements beside the Message field. Inserting a placement writes an explicit token such as `{{active_contracts}}`, `{{operational_parts}}` or `{{submission_evidence}}` at the caret; the generated content appears at that exact point when Remi composes the message.
- A generated-content placement that defines the event's purpose is required. Explain a missing placement beside the editor and disable Save until it has been restored. Scalar placements such as `{{customer_name}}` and `{{reporting_month}}` are optional and may appear in either the subject or message.
- For post-submission capture, use a searchable Remi reporting-month picklist beside the manual action. Require every in-scope framework to be submitted and to have image evidence for its latest submission; embed the retained images beneath concise framework headings in the message.
- A missing submission, missing image or missing archived file is a blocking error. Never compose an acceptance statement with an incomplete evidence set.
- The captured-message register shows when, event, subject, recipient summary, mode and a direct `.eml` download.
- Events whose business rules are not approved remain visible but disabled with a plain explanation; do not present placeholders as working automation.

## States and feedback

- Loading: one quiet status sentence in the eventual content area.
- Empty: state what is absent and provide one recovery action only when an action is possible.
- Success: say what changed and preserve context.
- Error: say what failed, what remains safe, and what the user can do next.
- Disabled: visually quiet but legible; never remove the label.
- Focus: a restrained teal focus halo; keyboard focus must remain visible.
- Editing: keep the same information architecture as viewing so the record does not feel like a different application.

## Responsive behaviour

- Above 980px: full navigation, side-by-side evidence, one-line fact strips and multi-column record layouts.
- 761–980px: fact strips may become two columns; evidence moves below the form when needed.
- 760px and below: navigation fits without horizontal page or header scrolling, forms become one column, tables scroll horizontally, and the first identifying table column remains available. Form controls are at least 46px high with 16px entry text.
- Below 600px: record fact strips become one column. Do not shrink text or hit areas to preserve a desktop layout.
- Mobile attachment controls say `Choose a file or photo`; desktop-only keyboard or drag instructions must not be the only guidance shown.

## Accessibility and content

- Use semantic headings in order; do not simulate headings with styled paragraphs.
- Every field has a programmatic label. Floating labels must remain part of the `<label>`.
- Sortable columns expose `aria-sort`; icon-only buttons have `aria-label`; tabs use tab semantics.
- Do not rely on colour alone for status or validation.
- Use UK date and currency formats in display views. Use concise, literal copy; avoid internal implementation terms.
- Prefer `Not recorded`, `No documents linked` and `No validation issues found` over blank cells.

## Agent implementation checklist

Before considering a Remi UI feature complete, verify:

- [ ] The page follows one of the page blueprints above.
- [ ] Every data-entry or filter control uses the floating-label pattern.
- [ ] No labelled action contains a directional arrow.
- [ ] No decorative eyebrow or duplicate title row was introduced.
- [ ] Simple facts use a fact strip rather than metric cards.
- [ ] Contract and invoice registers remain direct-navigation lists with no selection workflow.
- [ ] View and edit modes preserve the same hierarchy and action placement.
- [ ] Loading, empty, invalid, disabled, success and error states are designed.
- [ ] Keyboard focus, labels, status text and contrast are verified.
- [ ] The layout is checked at wide, intermediate and narrow widths.
- [ ] `DesignAccessibilityTests` and the relevant component tests pass.

When asking a coding agent for a feature, use: **“Implement this using `docs/design-blueprint.md` as the definitive Remi UI contract.”**
