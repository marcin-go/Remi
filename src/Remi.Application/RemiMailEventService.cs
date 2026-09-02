using System.Globalization;
using System.Net;
using System.Text;
using Remi.Domain;

namespace Remi.Application;

public sealed class RemiMailEventService(
    IRemiStore store,
    IRemiMailStore mailStore,
    MailCaptureService captureService,
    IEvidenceArchive evidenceArchive)
{
    public async Task<MailCaptureResult> CaptureCustomerGoLiveAsync(
        Guid contractId,
        IReadOnlyList<Guid> livePartIds,
        CancellationToken cancellationToken = default)
    {
        if (livePartIds.Count == 0)
        {
            return new MailCaptureResult(false, false, "No live contract parts are available for this message.", null);
        }
        var template = await mailStore.GetTemplateAsync(MailEventTypes.CustomerGoLive, cancellationToken);
        if (template is null || !template.Enabled)
        {
            return new MailCaptureResult(false, false, "The customer go-live template is not enabled.", null);
        }

        var context = await store.ReadAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (contract is null) return null;
            var selectedIds = livePartIds.ToHashSet();
            var parts = database.ContractServiceParts
                .Where(part => part.ContractId == contractId && selectedIds.Contains(part.Id) && part.GoLiveDate is not null)
                .OrderBy(part => part.SortOrder)
                .ToList();
            return parts.Count == 0 ? null : new GoLiveContext(contract, parts);
        }, cancellationToken);
        if (context is null)
        {
            return new MailCaptureResult(false, false, "No matching live contract parts were found.", null);
        }

        var tokens = Tokens(context.Contract, null);
        var subject = Expand(template.SubjectTemplate, tokens);
        var partsPlain = new StringBuilder();
        var partsHtml = new StringBuilder("<ul style=\"padding-left:22px\">");
        foreach (var part in context.Parts)
        {
            var goLiveDate = part.GoLiveDate!.Value.ToString("dd MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
            partsPlain.Append("- ").Append(part.Name).Append(": ").AppendLine(goLiveDate);
            partsHtml.Append("<li style=\"margin:6px 0\"><strong>").Append(Html(part.Name)).Append("</strong>: ")
                .Append(Html(part.GoLiveDate!.Value.ToString("dd MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")))).Append("</li>");
        }
        partsHtml.Append("</ul>");
        var body = RenderTemplateBody(template.BodyTemplate, tokens,
            new TemplateBlock("operational_parts", partsPlain.ToString().TrimEnd(), partsHtml.ToString()));

        var keyParts = context.Parts.Select(part => $"{part.Id:N}-{part.GoLiveDate:yyyyMMdd}");
        return await captureService.CaptureAsync(new MailCaptureDraft(
            MailEventTypes.CustomerGoLive,
            $"customer-go-live:{contractId:N}:{string.Join('-', keyParts)}",
            subject,
            body.PlainText,
            HtmlDocument(subject, body.Html),
            template.Recipients,
            RelatedEntityType: "Contract",
            RelatedEntityId: contractId), cancellationToken);
    }

    public async Task<MailCaptureResult> CaptureMonthlyActiveContractsAsync(
        string sourcePeriod,
        DateTimeOffset triggeredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!DateOnly.TryParseExact($"{sourcePeriod}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthStart))
        {
            return new MailCaptureResult(false, false, "The monthly-contract event has an invalid source period.", null);
        }
        var template = await mailStore.GetTemplateAsync(MailEventTypes.MonthlyActiveContracts, cancellationToken);
        if (template is null || !template.Enabled)
        {
            return new MailCaptureResult(false, false, "The monthly active-contract template is not enabled.", null);
        }

        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var context = await store.ReadAsync(database =>
        {
            var inventory = BuildActiveInventory(database, monthStart, monthEnd, triggeredAtUtc);
            return new MonthlyActiveContractsContext(
                inventory,
                BuildReportableFrameworks(database, monthStart, monthEnd, inventory));
        }, cancellationToken);
        var monthName = monthStart.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reporting_month"] = monthName,
        };
        var subject = Expand(template.SubjectTemplate, tokens);
        var body = RenderTemplateBody(template.BodyTemplate, tokens,
            new TemplateBlock(
                "reportable_frameworks",
                BuildReportableFrameworksPlainBlock(context.ReportableFrameworks),
                BuildReportableFrameworksHtmlBlock(context.ReportableFrameworks)),
            new TemplateBlock("active_contracts", BuildMonthlyPlainBlock(context.Inventory), BuildMonthlyHtmlBlock(context.Inventory)));
        return await captureService.CaptureAsync(new MailCaptureDraft(
            MailEventTypes.MonthlyActiveContracts,
            $"monthly-active-contracts:{sourcePeriod}:{triggeredAtUtc.UtcDateTime:yyyyMMddTHHmmssZ}",
            subject,
            body.PlainText,
            HtmlDocument(subject, body.Html),
            template.Recipients,
            SourcePeriod: sourcePeriod), cancellationToken);
    }

    public async Task<MailCaptureResult> CapturePostSubmissionReportAsync(
        string sourcePeriod,
        CancellationToken cancellationToken = default)
    {
        if (!DateOnly.TryParseExact($"{sourcePeriod}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthStart))
        {
            return new MailCaptureResult(false, false, "Choose a valid reporting month.", null);
        }
        var template = await mailStore.GetTemplateAsync(MailEventTypes.PostSubmissionReport, cancellationToken);
        if (template is null || !template.Enabled)
        {
            return new MailCaptureResult(false, false, "The post-submission template is not enabled.", null);
        }

        var context = await store.ReadAsync(
            database => BuildPostSubmissionContext(database, sourcePeriod, monthStart),
            cancellationToken);
        if (context.Problems.Count != 0)
        {
            return new MailCaptureResult(false, false, $"The message was not captured. {string.Join(" ", context.Problems)}", null);
        }

        var attachments = new List<MailInlineAttachment>();
        var renderedFrameworks = new List<PostSubmissionRenderedFramework>();
        foreach (var framework in context.Frameworks)
        {
            var renderedEvidence = new List<PostSubmissionRenderedEvidence>();
            for (var index = 0; index < framework.Evidence.Count; index++)
            {
                var evidence = framework.Evidence[index];
                await using var stream = await evidenceArchive.OpenReadAsync(evidence, cancellationToken);
                if (stream is null)
                {
                    return new MailCaptureResult(false, false, $"The message was not captured because {evidence.FileName} is missing from Remi's evidence archive.", null);
                }
                await using var content = new MemoryStream();
                await stream.CopyToAsync(content, cancellationToken);
                var contentId = $"submission-{sourcePeriod.Replace("-", string.Empty, StringComparison.Ordinal)}-{(int)framework.Framework}-{index + 1}@remi.local";
                attachments.Add(new MailInlineAttachment(
                    evidence.FileName,
                    ImageContentType(evidence),
                    contentId,
                    content.ToArray()));
                renderedEvidence.Add(new PostSubmissionRenderedEvidence(evidence.FileName, contentId));
            }
            renderedFrameworks.Add(new PostSubmissionRenderedFramework(SubmissionFrameworkTitle(framework.Framework), renderedEvidence));
        }

        var reportingMonth = monthStart.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reporting_month"] = reportingMonth,
        };
        var subject = Expand(template.SubjectTemplate, tokens);
        var body = RenderTemplateBody(template.BodyTemplate, tokens,
            new TemplateBlock("submission_evidence", BuildPostSubmissionPlainBlock(renderedFrameworks), BuildPostSubmissionHtmlBlock(renderedFrameworks)));
        return await captureService.CaptureAsync(new MailCaptureDraft(
            MailEventTypes.PostSubmissionReport,
            $"post-submission-report:{sourcePeriod}",
            subject,
            body.PlainText,
            HtmlDocument(subject, body.Html),
            template.Recipients,
            SourcePeriod: sourcePeriod,
            RelatedEntityType: "MonthlyReportingCycle",
            InlineAttachments: attachments), cancellationToken);
    }

    private static PostSubmissionContext BuildPostSubmissionContext(
        RemiDatabase database,
        string sourcePeriod,
        DateOnly monthStart)
    {
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var problems = new List<string>();
        var frameworks = new List<PostSubmissionFramework>();
        var obligations = Frameworks.All
            .Where(framework => framework.ReportingDeadline is not null)
            .Where(framework => (database.FrameworkConfigurations.SingleOrDefault(item => item.Framework == framework.Code)?.StartDate
                ?? framework.DefaultStartDate) is DateOnly startDate && startDate <= monthEnd)
            .OrderBy(framework => (int)framework.Code);

        foreach (var framework in obligations)
        {
            var monthlyReturn = database.MonthlyReturns.SingleOrDefault(item =>
                item.Framework == framework.Code && item.ReportMonth == sourcePeriod);
            if (monthlyReturn is null || monthlyReturn.Status is not (ReturnStatus.Submitted or ReturnStatus.NilReturn))
            {
                problems.Add($"{SubmissionFrameworkTitle(framework.Code)} has not been recorded as submitted.");
                continue;
            }

            var latestSubmission = database.AuditEvents
                .Where(item => item.EntityType == "MonthlyReturn" && item.EntityId == monthlyReturn.Id
                    && item.Action is "ReturnSubmitted" or "NilReturnRecorded")
                .OrderByDescending(item => item.OccurredAtUtc)
                .FirstOrDefault()?.OccurredAtUtc
                ?? monthlyReturn.SubmittedAtUtc
                ?? monthlyReturn.UpdatedAtUtc;
            var evidence = database.Evidence
                .Where(item => item.Kind == EvidenceKind.SubmissionEvidence
                    && item.Framework == framework.Code
                    && item.ReportMonth == sourcePeriod
                    && item.ArchivedAtUtc >= latestSubmission.AddSeconds(-1)
                    && IsImageEvidence(item))
                .OrderBy(item => item.ArchivedAtUtc)
                .ThenBy(item => item.Id)
                .ToList();
            if (evidence.Count == 0)
            {
                problems.Add($"{SubmissionFrameworkTitle(framework.Code)} has no image evidence for its latest submission.");
                continue;
            }
            frameworks.Add(new PostSubmissionFramework(framework.Code, evidence));
        }

        return new PostSubmissionContext(frameworks, problems);
    }

    private static string BuildPostSubmissionPlainBlock(
        IReadOnlyList<PostSubmissionRenderedFramework> frameworks)
    {
        var builder = new StringBuilder();
        foreach (var framework in frameworks)
        {
            builder.AppendLine(framework.Name);
            foreach (var evidence in framework.Evidence)
            {
                builder.Append("  [Submission evidence: ").Append(evidence.FileName).AppendLine("]");
            }
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    private static string BuildPostSubmissionHtmlBlock(
        IReadOnlyList<PostSubmissionRenderedFramework> frameworks)
    {
        var builder = new StringBuilder();
        foreach (var framework in frameworks)
        {
            builder.Append("<h2 style=\"font-size:17px;margin:24px 0 10px;color:#143c5d\">")
                .Append(Html(framework.Name)).Append("</h2>");
            foreach (var evidence in framework.Evidence)
            {
                builder.Append("<div style=\"margin:0 0 16px\"><img src=\"cid:")
                    .Append(Html(evidence.ContentId)).Append("\" alt=\"")
                    .Append(Html(evidence.FileName))
                    .Append("\" style=\"display:block;max-width:100%;height:auto;border:1px solid #d6e0e5\"></div>");
            }
        }
        return builder.ToString();
    }

    private static bool IsImageEvidence(EvidenceRecord evidence) =>
        evidence.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(evidence.FileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    private static string ImageContentType(EvidenceRecord evidence) =>
        evidence.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? evidence.ContentType
            : Path.GetExtension(evidence.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/png",
            };

    private static string SubmissionFrameworkTitle(FrameworkCode framework) => framework switch
    {
        FrameworkCode.VerticalApplicationSolutions => "VAS",
        _ => Frameworks.Get(framework).DisplayName,
    };

    private static IReadOnlyList<FrameworkInventory> BuildActiveInventory(
        RemiDatabase database,
        DateOnly monthStart,
        DateOnly monthEnd,
        DateTimeOffset asOfUtc)
    {
        var asOfDate = DateOnly.FromDateTime(asOfUtc.UtcDateTime);
        var reportedAt = database.ContractReportingOccurrences.ToDictionary(item => item.ContractId, item => item.ReportedAtUtc);
        var marketplaceNames = database.DigitalMarketplaceServices.ToDictionary(
            item => MarketplaceServiceKey(item.Framework, item.ServiceId),
            item => item.Name,
            StringComparer.OrdinalIgnoreCase);
        return database.Contracts
            .Where(contract => contract.CreatedAtUtc <= asOfUtc)
            .Select(contract => new
            {
                Contract = contract,
                EndDate = EffectiveEndDate(database, contract, asOfUtc),
            })
            .Where(item => (item.Contract.StartDate is null || item.Contract.StartDate <= monthEnd)
                && (item.EndDate is null || item.EndDate >= monthStart))
            .GroupBy(item => item.Contract.Framework)
            .OrderBy(group => FrameworkTitle(group.Key), StringComparer.OrdinalIgnoreCase)
            .Select(group => new FrameworkInventory(
                group.Key,
                FrameworkTitle(group.Key),
                group.Select(item =>
                {
                    var contract = item.Contract;
                    var parts = database.ContractServiceParts.Where(part => part.ContractId == contract.Id).ToList();
                    var liveCount = parts.Count(part => part.GoLiveDate is not null && part.GoLiveDate <= asOfDate);
                    var statusSuffix = liveCount == 0
                        ? " - NOT live yet"
                        : liveCount < parts.Count ? " - PARTIALLY live" : string.Empty;
                    var isNew = !reportedAt.TryGetValue(contract.Id, out var occurrence) || occurrence > asOfUtc;
                    var service = ServiceName(contract, marketplaceNames);
                    return new ActiveContractLine(
                        isNew,
                        contract.ReportMonth,
                        ReportingMonthLabel(contract.ReportMonth),
                        contract.CustomerName,
                        service,
                        ContractTerm(database, contract, asOfUtc),
                        statusSuffix);
                })
                .OrderBy(line => line.ReportingMonthSortKey, StringComparer.Ordinal)
                .ThenBy(line => line.CustomerName, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .Where(group => group.Contracts.Count != 0)
            .ToList();
    }

    private static IReadOnlyList<string> BuildReportableFrameworks(
        RemiDatabase database,
        DateOnly monthStart,
        DateOnly monthEnd,
        IReadOnlyList<FrameworkInventory> inventory)
    {
        var ongoingFrameworks = inventory.Select(item => item.Framework).ToHashSet();
        return Frameworks.All
            .Where(framework => ongoingFrameworks.Contains(framework.Code)
                || IsFrameworkActiveForMonth(database, framework, monthStart, monthEnd))
            .Select(framework => FrameworkTitle(framework.Code))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsFrameworkActiveForMonth(
        RemiDatabase database,
        FrameworkDefinition framework,
        DateOnly monthStart,
        DateOnly monthEnd)
    {
        var configuration = database.FrameworkConfigurations.SingleOrDefault(item => item.Framework == framework.Code);
        var startDate = configuration?.StartDate ?? framework.DefaultStartDate;
        var endDate = configuration?.EndDate ?? framework.DefaultEndDate;
        return startDate is DateOnly start && start <= monthEnd && (endDate is null || endDate >= monthStart);
    }

    private static string BuildReportableFrameworksPlainBlock(IReadOnlyList<string> frameworks) =>
        frameworks.Count == 0
            ? "No reportable frameworks."
            : string.Join('\n', frameworks.Select(framework => $"- {framework}"));

    private static string BuildReportableFrameworksHtmlBlock(IReadOnlyList<string> frameworks)
    {
        if (frameworks.Count == 0) return "<p>No reportable frameworks.</p>";

        var builder = new StringBuilder("<ul style=\"padding-left:22px\">");
        foreach (var framework in frameworks)
        {
            builder.Append("<li style=\"margin:6px 0\">").Append(Html(framework)).Append("</li>");
        }
        return builder.Append("</ul>").ToString();
    }

    private static string BuildMonthlyPlainBlock(
        IReadOnlyList<FrameworkInventory> inventory)
    {
        var builder = new StringBuilder();
        foreach (var framework in inventory)
        {
            builder.AppendLine(framework.Name);
            foreach (var contract in framework.Contracts)
            {
                builder.Append("  ");
                if (contract.IsNew) builder.Append("NEW - ");
                builder.Append(contract.ReportingMonth).Append(" - ").Append(contract.CustomerName)
                    .Append(" - ").Append(contract.ServiceName).Append(" (").Append(contract.Term).Append(')')
                    .AppendLine(contract.StatusSuffix);
            }
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    private static string BuildMonthlyHtmlBlock(
        IReadOnlyList<FrameworkInventory> inventory)
    {
        var builder = new StringBuilder();
        foreach (var framework in inventory)
        {
            builder.Append("<h2 style=\"font-size:17px;margin:24px 0 8px;color:#143c5d\">").Append(Html(framework.Name)).Append("</h2><ul style=\"padding-left:22px\">");
            foreach (var contract in framework.Contracts)
            {
                builder.Append("<li style=\"margin:6px 0\">");
                if (contract.IsNew) builder.Append("<strong style=\"color:#087977\">NEW</strong> - ");
                builder.Append(Html(contract.ReportingMonth)).Append(" - ").Append(Html(contract.CustomerName))
                    .Append(" - ").Append(Html(contract.ServiceName)).Append(" (").Append(Html(contract.Term)).Append(')')
                    .Append(Html(contract.StatusSuffix)).Append("</li>");
            }
            builder.Append("</ul>");
        }
        return builder.ToString();
    }

    private static DateOnly? EffectiveEndDate(RemiDatabase database, ContractRecord contract, DateTimeOffset asOfUtc) =>
        database.ContractChanges
            .Where(change => change.ContractId == contract.Id && change.IsConfirmed && change.CreatedAtUtc <= asOfUtc && change.Kind == ContractChangeKind.Extension)
            .Select(change => change.EffectiveEndDate)
            .Where(date => date is not null)
            .Append(contract.EndDate)
            .Max();

    private static string ServiceName(
        ContractRecord contract,
        IReadOnlyDictionary<string, string> marketplaceNames)
    {
        if (!string.IsNullOrWhiteSpace(contract.ServiceDescription)) return contract.ServiceDescription;
        if (!string.IsNullOrWhiteSpace(contract.DigitalMarketplaceServiceId)
            && marketplaceNames.TryGetValue(MarketplaceServiceKey(contract.Framework, contract.DigitalMarketplaceServiceId), out var name)) return name;
        return contract.ServiceGroup ?? "Service not recorded";
    }

    private static string MarketplaceServiceKey(FrameworkCode framework, string serviceId) =>
        $"{(int)framework}:{serviceId.Trim()}";

    private static string ContractTerm(RemiDatabase database, ContractRecord contract, DateTimeOffset asOfUtc)
    {
        var schedule = database.ChargeScheduleItems.Where(item => item.ContractId == contract.Id && item.CreatedAtUtc <= asOfUtc).ToList();
        var baseYears = schedule.Where(item => !item.IsOptionalExtension).Select(item => item.ContractYear).Distinct().Count();
        var optionYears = schedule.Where(item => item.IsOptionalExtension).Select(item => item.ContractYear).Distinct().Count();
        if (baseYears == 0 && contract.StartDate is DateOnly start && EffectiveEndDate(database, contract, asOfUtc) is DateOnly end)
        {
            baseYears = Math.Max(1, (int)Math.Ceiling((end.DayNumber - start.DayNumber + 1) / 365.25));
        }
        if (baseYears == 0) return "term not recorded";
        return optionYears == 0 ? $"{baseYears} years" : $"{baseYears}+{optionYears} years";
    }

    private static string ReportingMonthLabel(string reportingMonth) =>
        DateOnly.TryParseExact($"{reportingMonth}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("MMM yyyy", CultureInfo.GetCultureInfo("en-GB"))
            : reportingMonth;

    private static string FrameworkTitle(FrameworkCode framework) => framework switch
    {
        FrameworkCode.VerticalApplicationSolutions => "Vertical Application Solutions",
        _ => Frameworks.Get(framework).DisplayName,
    };

    private static Dictionary<string, string> Tokens(ContractRecord contract, string? reportingMonth) => new(StringComparer.Ordinal)
    {
        ["customer_name"] = contract.CustomerName,
        ["contract_reference"] = contract.SupplierReference,
        ["framework_name"] = Frameworks.Get(contract.Framework).DisplayName,
        ["reporting_month"] = reportingMonth ?? contract.ReportMonth,
    };

    private static RenderedTemplateBody RenderTemplateBody(
        string bodyTemplate,
        IReadOnlyDictionary<string, string> tokens,
        params TemplateBlock[] blocks)
    {
        var expanded = Expand(bodyTemplate, tokens)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var plain = expanded;
        foreach (var block in blocks)
        {
            plain = plain.Replace($"{{{{{block.Token}}}}}", block.PlainText, StringComparison.Ordinal);
        }

        var html = new StringBuilder();
        var offset = 0;
        while (offset < expanded.Length)
        {
            TemplateBlock? nextBlock = null;
            var nextIndex = expanded.Length;
            foreach (var block in blocks)
            {
                var index = expanded.IndexOf($"{{{{{block.Token}}}}}", offset, StringComparison.Ordinal);
                if (index >= 0 && index < nextIndex)
                {
                    nextIndex = index;
                    nextBlock = block;
                }
            }

            if (nextBlock is null)
            {
                AppendStaticTemplateHtml(html, expanded[offset..]);
                break;
            }
            AppendStaticTemplateHtml(html, expanded[offset..nextIndex]);
            html.Append(nextBlock.Html);
            offset = nextIndex + nextBlock.Token.Length + 4;
        }

        return new RenderedTemplateBody(plain.Trim(), html.ToString());
    }

    private static void AppendStaticTemplateHtml(StringBuilder builder, string value)
    {
        foreach (var paragraph in value.Split("\n\n", StringSplitOptions.None))
        {
            var content = paragraph.Trim();
            if (content.Length == 0) continue;
            builder.Append("<p>").Append(HtmlWithBreaks(content)).Append("</p>");
        }
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> tokens)
    {
        var expanded = value;
        foreach (var token in tokens) expanded = expanded.Replace($"{{{{{token.Key}}}}}", token.Value, StringComparison.Ordinal);
        return expanded;
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
    private static string HtmlWithBreaks(string value) => Html(value).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
    private static string HtmlDocument(string title, string body) => $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{Html(title)}</title></head><body style=\"font-family:Arial,sans-serif;color:#17364b;line-height:1.5;max-width:760px;margin:24px auto\">{body}</body></html>";

    private sealed record GoLiveContext(ContractRecord Contract, IReadOnlyList<ContractServicePart> Parts);
    private sealed record MonthlyActiveContractsContext(
        IReadOnlyList<FrameworkInventory> Inventory,
        IReadOnlyList<string> ReportableFrameworks);
    private sealed record FrameworkInventory(FrameworkCode Framework, string Name, IReadOnlyList<ActiveContractLine> Contracts);
    private sealed record ActiveContractLine(bool IsNew, string ReportingMonthSortKey, string ReportingMonth, string CustomerName, string ServiceName, string Term, string StatusSuffix);
    private sealed record PostSubmissionContext(IReadOnlyList<PostSubmissionFramework> Frameworks, IReadOnlyList<string> Problems);
    private sealed record PostSubmissionFramework(FrameworkCode Framework, IReadOnlyList<EvidenceRecord> Evidence);
    private sealed record PostSubmissionRenderedFramework(string Name, IReadOnlyList<PostSubmissionRenderedEvidence> Evidence);
    private sealed record PostSubmissionRenderedEvidence(string FileName, string ContentId);
    private sealed record TemplateBlock(string Token, string PlainText, string Html);
    private sealed record RenderedTemplateBody(string PlainText, string Html);
}
