using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Remi.Tests;

public sealed class DesignAccessibilityTests
{
    [Theory]
    [InlineData("--color-navy", "#FFFFFF", 4.5)]
    [InlineData("--color-navy", "#F4F7F8", 4.5)]
    [InlineData("--color-teal", "#FFFFFF", 4.5)]
    [InlineData("--color-teal-hover", "#FFFFFF", 4.5)]
    [InlineData("--color-text", "#FFFFFF", 4.5)]
    [InlineData("--color-text-muted", "#F4F7F8", 4.5)]
    [InlineData("--color-focus", "#FFFFFF", 3.0)]
    public void Core_tokens_meet_required_contrast_against_their_surfaces(string token, string background, double minimumContrast)
    {
        var ratio = Contrast(Token(token), background);

        Assert.True(ratio >= minimumContrast, $"{token} has contrast {ratio:F2}:1 against {background}; expected at least {minimumContrast:F1}:1.");
    }

    [Theory]
    [InlineData("--color-success")]
    [InlineData("--color-warning")]
    [InlineData("--color-error")]
    public void Status_tokens_remain_legible_on_their_tinted_status_surfaces(string token)
    {
        var foreground = Token(token);
        var tintedSurface = Blend(foreground, "#FFFFFF", 0.11);
        var ratio = Contrast(foreground, tintedSurface);

        Assert.True(ratio >= 4.5, $"{token} has contrast {ratio:F2}:1 against its status surface; expected at least 4.5:1.");
    }

    [Fact]
    public void Interactive_controls_do_not_use_bold_focus_outlines_and_keep_compact_supporting_controls()
    {
        var css = File.ReadAllText(AppCssPath());

        Assert.Contains(":where(button, input, select, a, [role=\"button\"], [role=\"tab\"]):focus-visible { outline: none; }", css, StringComparison.Ordinal);
        Assert.DoesNotContain("outline: 3px solid", css, StringComparison.Ordinal);
        Assert.DoesNotContain("outline: 2px solid", css, StringComparison.Ordinal);
        Assert.Contains(".floating-field { position: relative; display: block; height: 42px;", css, StringComparison.Ordinal);
        Assert.Contains(".quick-filter { min-height: 1.875rem;", css, StringComparison.Ordinal);
        Assert.Contains(".brand-subtitle { display: inline-flex; align-items: center; align-self: stretch;", css, StringComparison.Ordinal);
        Assert.Contains(".navigation { display: flex; align-items: stretch; gap: 0.1rem; min-width: 0; margin-left: auto; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_registration_centres_field_content()
    {
        var css = File.ReadAllText(AppCssPath());

        Assert.Contains(".contract-intake-page .registration-field .floating-field > input,", css, StringComparison.Ordinal);
        Assert.Contains(".contract-intake-page .registration-field .remi-picklist-trigger {", css, StringComparison.Ordinal);
        Assert.Contains("padding-top: 10px;", css, StringComparison.Ordinal);
        Assert.Contains("padding-bottom: 10px;", css, StringComparison.Ordinal);
        Assert.Contains("line-height: 20px;", css, StringComparison.Ordinal);
        Assert.Contains(".contract-intake-page .floating-field--currency > b { transform: translateY(-50%); }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_edit_picklists_use_the_refined_vertical_alignment()
    {
        var css = File.ReadAllText(AppCssPath());

        Assert.Contains(
            ".contract-edit-panel .record-edit-fields .remi-picklist-trigger { padding-top: 10px; padding-bottom: 10px; line-height: 20px; }",
            css,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Active_record_tab_underlines_have_square_corners()
    {
        var css = File.ReadAllText(AppCssPath());

        Assert.Matches(
            @"\.contract-tabs button\.active\s*\{[^}]*border-radius:\s*0;",
            css);
    }

    [Fact]
    public void Command_tiers_follow_the_compact_typographic_system()
    {
        var css = File.ReadAllText(AppCssPath());

        Assert.Contains(".remi-action, .button { display: inline-flex; align-items: center; justify-content: center; gap: 5px; min-height: 30px; padding: 0 6px;", css, StringComparison.Ordinal);
        Assert.Contains("font-size: 13px; font-weight: 800;", css, StringComparison.Ordinal);
        Assert.Contains("letter-spacing: 0.065em;", css, StringComparison.Ordinal);
        Assert.Contains(".remi-action--primary, .button.primary { color: #087f7d; font-size: 14px; }", css, StringComparison.Ordinal);
        Assert.Contains(".remi-action--compact, .button.secondary { min-height: 28px; padding-inline: 5px; font-size: 13px; }", css, StringComparison.Ordinal);
        Assert.Contains(".remi-action--table { min-height: 24px; padding-inline: 3px; font-size: 12px; letter-spacing: 0.055em; }", css, StringComparison.Ordinal);
        Assert.Contains(".remi-action--section { min-height: 26px; padding-inline: 3px; font-size: 13px; }", css, StringComparison.Ordinal);
        Assert.Contains(".remi-action:hover:not(:disabled), .button:hover:not(:disabled) { color: #087f7d; background: rgb(11 145 143 / 7%); }", css, StringComparison.Ordinal);
        Assert.Contains("--color-danger-hover: #9B3F36;", css, StringComparison.Ordinal);
        Assert.Contains(".button.danger:hover:not(:disabled), .button.danger:focus-visible { color: #fff; background: var(--color-danger-hover); }", css, StringComparison.Ordinal);
        Assert.Contains(".record-more-menu .record-more-delete:hover, .record-more-menu .record-more-delete:focus-visible { color: #fff; background: var(--color-danger-hover);", css, StringComparison.Ordinal);
        Assert.Contains(".table-action-cell { width: 70px; text-align: right !important; white-space: nowrap; }", css, StringComparison.Ordinal);
        Assert.Contains(".filter-actions { display: flex; align-items: center; align-self: end; height: 1.875rem; margin-left: 6px; }", css, StringComparison.Ordinal);
        Assert.Contains(".register-filters input, .register-filters select { height: 1.875rem; min-height: 1.875rem; padding: 0.25rem 0.55rem;", css, StringComparison.Ordinal);
        Assert.Contains(".register-reset { height: 1.875rem; min-height: 1.875rem; padding-inline: 5px; font-size: 13px; justify-self: start; }", css, StringComparison.Ordinal);
        Assert.Contains(".button-icon-only { min-height: 0; gap: 0; padding: 0;", css, StringComparison.Ordinal);
        Assert.Contains(".button-control-peer { align-self: stretch; }", css, StringComparison.Ordinal);
        Assert.Contains("select { min-height: 0; padding: 0.25rem 0.55rem; }", css, StringComparison.Ordinal);
        Assert.Contains(".register-filters select { min-height: 1.875rem; padding: 0.25rem 0.55rem; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Information_treatments_remain_sentence_case_and_distinct_from_commands()
    {
        var css = File.ReadAllText(AppCssPath());

        Assert.DoesNotContain(".eyebrow {", css, StringComparison.Ordinal);
        Assert.Contains(".dashboard-period-current span, .dashboard-metrics dt { color: var(--color-text-muted); font-size: 0.6875rem; font-weight: 600; letter-spacing: normal; text-transform: none; }", css, StringComparison.Ordinal);
        Assert.Contains(".dashboard-metrics .has-review-exceptions dd { color: var(--color-warning); }", css, StringComparison.Ordinal);
        Assert.Contains(".dashboard-metrics .has-blocking-exceptions dd { color: var(--color-error); }", css, StringComparison.Ordinal);
        Assert.Contains(".dashboard-header { display: flex; align-items: start; justify-content: space-between; gap: 2rem; margin-bottom: 1.35rem; }", css, StringComparison.Ordinal);
        Assert.Contains(".return-register-heading h2 { margin: 0; color: #183c50; font-size: 1.25rem; }", css, StringComparison.Ordinal);
        Assert.Contains(".gca-summary-content { display: grid; grid-template-columns: 1fr;", css, StringComparison.Ordinal);
        Assert.Contains("th { position: sticky; top: 0; z-index: 1; color: #607482; background: #f7f9fa; font-size: 0.65625rem; font-weight: 700; letter-spacing: normal; text-transform: none; }", css, StringComparison.Ordinal);
        Assert.Contains("/* Definitive Remi density rules: compact facts, persistent floating labels, no decorative title rows. */", css, StringComparison.Ordinal);
        Assert.Contains(".register-filters .floating-field > input,", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Components_follow_the_non_negotiable_interface_blueprint_rules()
    {
        var componentRoot = FindFromRepository("src", "Remi.Web", "Components");
        var razor = string.Join('\n', Directory.GetFiles(componentRoot, "*.razor", SearchOption.AllDirectories).Select(File.ReadAllText));
        var contracts = File.ReadAllText(Path.Combine(componentRoot, "Pages", "Contracts.razor"));
        var invoices = File.ReadAllText(Path.Combine(componentRoot, "Pages", "Invoices.razor"));

        Assert.DoesNotContain("<select", razor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<InputSelect", razor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=\"eyebrow\"", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("→", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("←", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("↗", razor, StringComparison.Ordinal);

        Assert.DoesNotContain("selectedContractIds", contracts, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedInvoiceIds", invoices, StringComparison.Ordinal);
        Assert.DoesNotContain("Select visible", contracts, StringComparison.Ordinal);
        Assert.DoesNotContain("Select visible", invoices, StringComparison.Ordinal);
        Assert.Contains("register-search floating-field floating-field--static", contracts, StringComparison.Ordinal);
        Assert.Contains("register-search floating-field floating-field--static", invoices, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", contracts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<select", invoices, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, contracts.Split("<RemiPicklist", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, invoices.Split("<RemiPicklist", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("quick-filters", contracts, StringComparison.Ordinal);
        Assert.DoesNotContain("<datalist", razor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<SearchablePicklistOptions", razor, StringComparison.Ordinal);

        var contractRecord = File.ReadAllText(Path.Combine(componentRoot, "ContractRecordView.razor"));
        var invoiceRecord = File.ReadAllText(Path.Combine(componentRoot, "InvoiceRecordView.razor"));
        Assert.Contains("<EvidenceGallery", contractRecord, StringComparison.Ordinal);
        Assert.Contains("<EvidenceGallery", invoiceRecord, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-evidence-file", contractRecord, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-evidence-file", invoiceRecord, StringComparison.Ordinal);

        var picklist = File.ReadAllText(Path.Combine(componentRoot, "SearchablePicklistOptions.razor"));
        Assert.Contains("Virtualize", picklist, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(Items.Count, 1, 5)", picklist, StringComparison.Ordinal);
        Assert.Contains("@onmousedown:preventDefault", picklist, StringComparison.Ordinal);

        Assert.True(razor.Split("@onblur=", StringSplitOptions.None).Length - 1 >= 9);

        var remiPicklist = File.ReadAllText(Path.Combine(componentRoot, "RemiPicklist.razor"));
        Assert.Contains("role=\"combobox\"", remiPicklist, StringComparison.Ordinal);
        Assert.Contains("ScrollSelectedIntoView=\"@(!filterActive)\"", remiPicklist, StringComparison.Ordinal);

        var picklistDismiss = File.ReadAllText(FindFromRepository("src", "Remi.Web", "wwwroot", "picklist-dismiss.js"));
        Assert.Contains("document.addEventListener(\"pointerdown\"", picklistDismiss, StringComparison.Ordinal);
        Assert.Contains("combobox.getAttribute(\"aria-expanded\") !== \"true\"", picklistDismiss, StringComparison.Ordinal);
        Assert.Contains("combobox.blur()", picklistDismiss, StringComparison.Ordinal);

        var clipboardEvidence = File.ReadAllText(FindFromRepository("src", "Remi.Web", "wwwroot", "clipboard-image-evidence.js"));
        Assert.Contains("isTextEditingTarget(event.target) && !host.contains(event.target)", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("dispose(host);", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("item.fingerprint === fingerprint", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("`clipboard-image-${fingerprint.slice(0, 12)}${extension}`", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("intake(state, files, true)", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("pendingOperations: 0", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("export function getState(host)", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("await state.queue;", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("invokeMethodAsync('DocumentsChanged')", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("URL.createObjectURL(file)", clipboardEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("readAsDataURL", clipboardEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("await state.dotNetReference.invokeMethodAsync", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("item.title = title", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("encodeURIComponent(item.title)", clipboardEvidence, StringComparison.Ordinal);
        Assert.Contains("state.fileInput.removeEventListener('change', state.onFileChange)", clipboardEvidence, StringComparison.Ordinal);

        var contractRegistration = File.ReadAllText(Path.Combine(componentRoot, "Pages", "ContractRegistration.razor"));
        Assert.DoesNotContain("<select", contractRegistration, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, contractRegistration.Split("<RemiPicklist", StringSplitOptions.None).Length - 1);

        var maintenance = File.ReadAllText(Path.Combine(componentRoot, "Pages", "Maintenance.razor"));
        Assert.Contains("To restore backup type: <strong>RESTORE</strong>", maintenance, StringComparison.Ordinal);
        Assert.Contains("data-remi-restore-submit>RESTORE</button>", maintenance, StringComparison.Ordinal);
        Assert.DoesNotContain("REPLACE", maintenance, StringComparison.Ordinal);
        var program = File.ReadAllText(FindFromRepository("src", "Remi.Web", "Program.cs"));
        Assert.Contains("form[\"replacementPhrase\"], \"RESTORE\"", program, StringComparison.Ordinal);

        var mailSettings = File.ReadAllText(Path.Combine(componentRoot, "MailSettings.razor"));
        Assert.Equal(1, mailSettings.Split("mail-message-field", StringSplitOptions.None).Length - 1);
        Assert.Contains("Insert into message", mailSettings, StringComparison.Ordinal);
        Assert.Contains("AvailablePlacements", mailSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("editGreeting", mailSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Request or explanation", mailSettings, StringComparison.Ordinal);
        Assert.Contains("Manual capture mode", mailSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("MailTriggerMode.Automatic", mailSettings, StringComparison.Ordinal);

        var saveOperationsStart = contractRecord.IndexOf("private async Task SaveOperationsAsync", StringComparison.Ordinal);
        var manualGoLiveStart = contractRecord.IndexOf("private async Task CaptureCustomerGoLiveMessageAsync", StringComparison.Ordinal);
        Assert.True(saveOperationsStart >= 0 && manualGoLiveStart > saveOperationsStart);
        Assert.DoesNotContain("CaptureCustomerGoLiveAsync", contractRecord[saveOperationsStart..manualGoLiveStart], StringComparison.Ordinal);
        Assert.Contains("Capture customer go-live message", contractRecord, StringComparison.Ordinal);
        Assert.Contains("CaptureCustomerGoLiveAsync", contractRecord[manualGoLiveStart..], StringComparison.Ordinal);

        var webRoot = FindFromRepository("src", "Remi.Web");
        Assert.False(File.Exists(Path.Combine(webRoot, "MonthlyActiveContractsCaptureWorker.cs")));
        Assert.DoesNotContain("MonthlyActiveContractsCaptureWorker", File.ReadAllText(Path.Combine(webRoot, "Program.cs")), StringComparison.Ordinal);

        var css = File.ReadAllText(AppCssPath());
        Assert.Contains("height: calc((var(--picklist-visible-rows) * 2.5rem) + (var(--picklist-visible-groups, 0) * 1.55rem) + 2px);", css, StringComparison.Ordinal);
        Assert.Contains(".picklist-group-heading", css, StringComparison.Ordinal);
        Assert.Contains(".customer-address-verification", css, StringComparison.Ordinal);
        Assert.Contains(".remi-picklist-trigger", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 46px;", css, StringComparison.Ordinal);
        Assert.Contains(".clipboard-instruction-mobile { display: inline; }", css, StringComparison.Ordinal);

        var blueprint = File.ReadAllText(FindFromRepository("dev-resources", "design-blueprint.md"));
        Assert.Contains("## Non-negotiable rules", blueprint, StringComparison.Ordinal);
        Assert.Contains("Floating labels are the default form control", blueprint, StringComparison.Ordinal);
        Assert.Contains("### Searchable picklists", blueprint, StringComparison.Ordinal);
        Assert.Contains("### Compact selectors", blueprint, StringComparison.Ordinal);
        Assert.Contains("never truncate the result set to five records", blueprint, StringComparison.Ordinal);
        Assert.Contains("pressing anywhere outside the picker closes the list", blueprint, StringComparison.Ordinal);
        Assert.Contains("Both the organisation-name and URN fields search the same locally stored directory", blueprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_layouts_use_the_shared_available_width()
    {
        var css = File.ReadAllText(AppCssPath());

        Assert.Contains("main { width: 100%; padding-top: 2rem; }", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".page-heading { display: flex; align-items: end; justify-content: space-between; gap: 2rem; max-width:", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".registration-page { max-width:", css, StringComparison.Ordinal);
    }

    private static string Token(string token)
    {
        var match = Regex.Match(File.ReadAllText(AppCssPath()), $"{Regex.Escape(token)}:\\s*(#[0-9A-Fa-f]{{6}})");
        Assert.True(match.Success, $"Could not find {token} in app.css.");
        return match.Groups[1].Value;
    }

    private static string AppCssPath()
        => FindFromRepository("src", "Remi.Web", "wwwroot", "app.css");

    private static string FindFromRepository(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(relativePath)} from the test output directory.");
    }

    private static double Contrast(string first, string second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static string Blend(string foreground, string background, double opacity)
    {
        var foregroundChannels = Channels(foreground);
        var backgroundChannels = Channels(background);
        var blendedChannels = Enumerable.Range(0, 3).Select(index =>
        {
            var channel = (int)Math.Round((foregroundChannels[index] * opacity) + (backgroundChannels[index] * (1 - opacity)));
            return channel.ToString("X2", CultureInfo.InvariantCulture);
        });
        return $"#{string.Concat(blendedChannels)}";
    }

    private static double Luminance(string color)
    {
        var channels = Channels(color).Select(channel =>
        {
            var value = channel / 255d;
            return value <= 0.04045d ? value / 12.92d : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }).ToArray();
        return (0.2126d * channels[0]) + (0.7152d * channels[1]) + (0.0722d * channels[2]);
    }

    private static int[] Channels(string color) =>
    [
        int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
    ];
}
