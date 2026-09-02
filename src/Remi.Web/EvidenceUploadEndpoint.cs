using Remi.Application;
using Remi.Domain;

namespace Remi.Web;

public static class EvidenceUploadEndpoint
{
    public static async Task<IResult> HandleAsync(
        string entityType, Guid entityId, string? title, IFormFile file,
        IRemiStore store, ReportingWorkspace workspace, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > 15 * 1024 * 1024)
            return Results.BadRequest("Add a non-empty file no larger than 15 MB.");

        var target = await store.ReadAsync(database => entityType.ToLowerInvariant() switch
        {
            "contract" => database.Contracts.Where(item => item.Id == entityId).Select(item => new Target(item.Framework, item.ReportMonth, item.SupplierReference)).SingleOrDefault(),
            "invoice" => database.Invoices.Where(item => item.Id == entityId).Select(item => new Target(item.Framework, item.ReportMonth, item.SupplierReference)).SingleOrDefault(),
            "contract-change" => (from change in database.ContractChanges
                                  join contract in database.Contracts on change.ContractId equals contract.Id
                                  where change.Id == entityId
                                  select new Target(contract.Framework, change.AgreementDate.ToString("yyyy-MM"), contract.SupplierReference)).SingleOrDefault(),
            "monthly-return" => database.MonthlyReturns.Where(item => item.Id == entityId).Select(item => new Target(item.Framework, item.ReportMonth, null)).SingleOrDefault(),
            _ => null,
        }, cancellationToken);
        if (target is null) return Results.NotFound();

        var originalName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(originalName);
        var documentTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(originalName) : title.Trim();
        if (string.IsNullOrWhiteSpace(documentTitle) || documentTitle.Length > 200 || documentTitle is "." or ".." ||
            documentTitle.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Results.BadRequest("Enter a valid document title of at most 200 characters, without file-path characters.");
        // A dot in a title is not an extension: preserve titles such as 'Agreement v1.2'.
        var fileName = !string.IsNullOrEmpty(extension) && documentTitle.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? documentTitle : $"{documentTitle}{extension}";
        await using var content = file.OpenReadStream();
        var archived = await workspace.ArchiveEvidenceAsync(
            entityType.Equals("monthly-return", StringComparison.OrdinalIgnoreCase) ? EvidenceKind.SubmissionEvidence : EvidenceKind.SupportingDocument,
            target.Framework, target.ReportMonth, fileName,
            $"clipboard/{entityType.ToLowerInvariant()}/{entityId:D}/{fileName}",
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            target.SupplierReference, content, cancellationToken);
        // The same path/content is already retained on retry after a lost HTTP response.
        return Results.Ok(new { archived });
    }

    private sealed record Target(FrameworkCode Framework, string ReportMonth, string? SupplierReference);
}
