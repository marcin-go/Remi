using Remi.Application;
using Remi.Domain;
using Xunit;

namespace Remi.Tests;

public sealed class GeneratedReturnRetentionTests
{
    [Fact]
    public async Task Export_replaces_unsubmitted_drafts_but_keeps_submitted_and_correction_evidence()
    {
        var database = new RemiDatabase();
        var now = new AdvancingTimeProvider();
        var templateEvidenceId = Guid.NewGuid();
        database.Evidence.Add(new EvidenceRecord(
            templateEvidenceId,
            EvidenceKind.TemplateWorkbook,
            FrameworkCode.GCloud14,
            null,
            "template.xlsx",
            "templates/template.xlsx",
            "template.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            1,
            "template",
            null,
            now.GetUtcNow()));
        database.MiTemplates.Add(new MiTemplateConfiguration(
            Guid.NewGuid(),
            FrameworkCode.GCloud14,
            templateEvidenceId,
            "template.xlsx",
            true,
            now.GetUtcNow()));
        var archive = new InMemoryEvidenceArchive();
        var workspace = new ReportingWorkspace(
            new InMemoryStore(database),
            null!,
            new FixedWorkbookExporter(),
            archive,
            null!,
            now);

        await workspace.ExportReturnAsync(FrameworkCode.GCloud14, "2026-07");
        var firstDraft = GeneratedEvidence(database).Single();

        await workspace.ExportReturnAsync(FrameworkCode.GCloud14, "2026-07");
        var submittedWorkbook = GeneratedEvidence(database).Single();
        Assert.NotEqual(firstDraft.Id, submittedWorkbook.Id);
        Assert.Contains(firstDraft.StoredRelativePath, archive.DeletedPaths);

        var submittedAt = submittedWorkbook.ArchivedAtUtc.AddTicks(1);
        var monthlyReturn = database.MonthlyReturns.Single();
        database.MonthlyReturns[0] = monthlyReturn with
        {
            Status = ReturnStatus.Submitted,
            SubmittedAtUtc = submittedAt,
            SubmissionReference = "SUB-001",
        };

        var correction = await workspace.RequestCorrectionAsync(FrameworkCode.GCloud14, "2026-07", "Corrected customer invoice details.");
        Assert.True(correction.Succeeded);
        Assert.Equal(submittedAt, database.MonthlyReturns.Single().SubmittedAtUtc);

        var thirdExport = await workspace.ExportReturnAsync(FrameworkCode.GCloud14, "2026-07");
        Assert.NotNull(thirdExport);
        var retainedEvidence = GeneratedEvidence(database);
        Assert.Equal(2, retainedEvidence.Count);
        Assert.Contains(retainedEvidence, evidence => evidence.Id == submittedWorkbook.Id);
        Assert.Contains(retainedEvidence, evidence => evidence.Id == thirdExport!.EvidenceId);
        Assert.DoesNotContain(submittedWorkbook.StoredRelativePath, archive.DeletedPaths);

        var staleDraft = retainedEvidence.Single(evidence => evidence.Id == thirdExport.EvidenceId) with
        {
            Id = Guid.NewGuid(),
            StoredRelativePath = "stale-draft.xlsx",
            ArchivedAtUtc = retainedEvidence.Single(evidence => evidence.Id == thirdExport.EvidenceId).ArchivedAtUtc.AddTicks(-1),
        };
        database.Evidence.Add(staleDraft);

        var evidenceForReturn = await workspace.GetReportingEvidenceAsync(FrameworkCode.GCloud14, "2026-07");
        Assert.Equal(thirdExport.EvidenceId, evidenceForReturn[0].Id);
        Assert.DoesNotContain(database.Evidence, evidence => evidence.Id == staleDraft.Id);
        Assert.Contains(staleDraft.StoredRelativePath, archive.DeletedPaths);
    }

    private static List<EvidenceRecord> GeneratedEvidence(RemiDatabase database) =>
        database.Evidence.Where(item => item.Kind == EvidenceKind.GeneratedMiWorkbook).ToList();

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) =>
            Task.FromResult(update(database));
    }

    private sealed class InMemoryEvidenceArchive : IEvidenceArchive
    {
        private int nextFile;
        public List<string> DeletedPaths { get; } = [];

        public Task<ArchivedEvidenceFile> ArchiveAsync(EvidenceArchiveRequest request, CancellationToken cancellationToken = default)
        {
            var storedPath = $"generated-{++nextFile}-{request.FileName}";
            return Task.FromResult(new ArchivedEvidenceFile(storedPath, 1, $"hash-{nextFile}"));
        }

        public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(new MemoryStream([1]));

        public Task DeleteAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default)
        {
            DeletedPaths.Add(evidence.StoredRelativePath);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedWorkbookExporter : IMiWorkbookExporter
    {
        public Task<TemplateValidationResult> ValidateTemplateAsync(FrameworkCode framework, Stream workbook, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TemplateValidationResult(true, []));

        public Task<GeneratedMiWorkbook> GenerateAsync(FrameworkCode framework, Stream templateWorkbook, IReadOnlyList<ContractRecord> contracts, IReadOnlyList<InvoiceRecord> invoices, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedMiWorkbook(new MemoryStream([1]), []));
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private DateTimeOffset current = new(2026, 8, 6, 14, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => current = current.AddMinutes(1);
    }
}
