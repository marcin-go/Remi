using Remi.Application;
using Remi.Domain;
using Xunit;

namespace Remi.Tests;

public sealed class TemplateRegistrationTests
{
    [Fact]
    public async Task Replacing_an_active_template_deactivates_the_previous_version_and_registers_the_new_workbook()
    {
        var previousTemplateId = Guid.NewGuid();
        var otherFrameworkTemplateId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var database = new RemiDatabase
        {
            MiTemplates =
            [
                new MiTemplateConfiguration(previousTemplateId, FrameworkCode.GCloud14, Guid.NewGuid(), "RM1557-14 Data Template (July 2026).xlsx", true, now.AddMonths(-1)),
                new MiTemplateConfiguration(otherFrameworkTemplateId, FrameworkCode.GCloud13, Guid.NewGuid(), "RM1557-13 Data Template (July 2026).xlsx", true, now.AddMonths(-1)),
            ],
        };
        var workspace = new ReportingWorkspace(
            new InMemoryStore(database),
            null!,
            new AcceptingWorkbookExporter(),
            new RecordingEvidenceArchive(),
            null!,
            new FixedTimeProvider(now));
        await using var workbook = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await workspace.RegisterTemplateAsync(
            FrameworkCode.GCloud14,
            "RM1557-14 Data Template (August 2026).xlsx",
            workbook);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(Assert.Single(database.MiTemplates, item => item.Id == previousTemplateId).IsActive);
        Assert.True(Assert.Single(database.MiTemplates, item => item.Id == otherFrameworkTemplateId).IsActive);
        var activeTemplate = Assert.Single(database.MiTemplates, item => item.Framework == FrameworkCode.GCloud14 && item.IsActive);
        Assert.Equal("RM1557-14 Data Template (August 2026).xlsx", activeTemplate.WorkbookName);
        Assert.Contains(database.Evidence, item => item.Id == activeTemplate.EvidenceId && item.Kind == EvidenceKind.TemplateWorkbook);
        Assert.Contains(database.AuditEvents, item => item.EntityId == activeTemplate.Id && item.Action == "TemplateRegistered");
    }

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) =>
            Task.FromResult(update(database));
    }

    private sealed class AcceptingWorkbookExporter : IMiWorkbookExporter
    {
        public Task<TemplateValidationResult> ValidateTemplateAsync(
            FrameworkCode framework,
            Stream workbook,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TemplateValidationResult(true, []));

        public Task<GeneratedMiWorkbook> GenerateAsync(
            FrameworkCode framework,
            Stream templateWorkbook,
            IReadOnlyList<ContractRecord> contracts,
            IReadOnlyList<InvoiceRecord> invoices,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingEvidenceArchive : IEvidenceArchive
    {
        public Task<ArchivedEvidenceFile> ArchiveAsync(
            EvidenceArchiveRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchivedEvidenceFile("test-template.xlsx", request.Content.Length, "test-hash"));

        public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task DeleteAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
