using Remi.Domain;

namespace Remi.Application;

/// <summary>
/// Validates an approved MI workbook and produces a return without changing its workbook design.
/// </summary>
public interface IMiWorkbookExporter
{
    Task<TemplateValidationResult> ValidateTemplateAsync(
        FrameworkCode framework,
        Stream workbook,
        CancellationToken cancellationToken = default);

    Task<GeneratedMiWorkbook> GenerateAsync(
        FrameworkCode framework,
        Stream templateWorkbook,
        IReadOnlyList<ContractRecord> contracts,
        IReadOnlyList<InvoiceRecord> invoices,
        CancellationToken cancellationToken = default);
}

public sealed record TemplateValidationResult(
    bool IsValid,
    IReadOnlyList<ValidationFinding> Findings);

public sealed record GeneratedMiWorkbook(
    Stream Content,
    IReadOnlyList<ValidationFinding> Findings);
