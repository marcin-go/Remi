using System.Text;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class MailCaptureTests
{
    [Fact]
    public async Task All_event_templates_are_manual_and_have_no_schedule()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        Directory.CreateDirectory(root);
        try
        {
            var schemaStore = new SqliteRemiStore(databasePath);
            var mailStore = new SqliteRemiMailStore(databasePath, schemaStore);

            var templates = await mailStore.GetTemplatesAsync();

            Assert.Equal(5, templates.Count);
            Assert.All(templates, template =>
            {
                Assert.Equal(MailTriggerMode.Manual, template.TriggerMode);
                Assert.Null(template.ScheduleDay);
                Assert.Null(template.ScheduleTimeLocal);
                Assert.Null(template.TimeZoneId);
            });
            Assert.Contains(
                "{{reportable_frameworks}}",
                Assert.Single(templates, template => template.EventType == MailEventTypes.MonthlyActiveContracts).BodyTemplate,
                StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Capture_writes_one_immutable_eml_and_deduplicates_the_event()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        var mailRoot = Path.Combine(root, "mail");
        Directory.CreateDirectory(root);
        try
        {
            var schemaStore = new SqliteRemiStore(databasePath);
            var mailStore = new SqliteRemiMailStore(databasePath, schemaStore);
            var contentStore = new FileMailContentStore(mailRoot);
            var now = new DateTimeOffset(2026, 8, 8, 12, 30, 0, TimeSpan.Zero);
            var service = new MailCaptureService(
                mailStore,
                contentStore,
                new MailRuntimeOptions(MailDeliveryMode.Capture, "remi@example.test", "Remi"),
                new FixedTimeProvider(now));
            var recipients = new List<MailRecipient>
            {
                new(Guid.NewGuid(), MailRecipientType.To, "Director", "director@example.test", 0),
                new(Guid.NewGuid(), MailRecipientType.Bcc, null, "audit@example.test", 0),
            };
            var draft = new MailCaptureDraft(
                MailEventTypes.MonthlyActiveContracts,
                "monthly-active-contracts:2026-07:2026-08-01T09:00:00Z",
                "Active contracts for July 2026",
                "Hello.\n\nThis is a captured message.",
                "<p>Hello.</p><p>This is a captured message.</p>",
                recipients,
                now,
                "2026-07");

            var first = await service.CaptureAsync(draft);
            var duplicate = await service.CaptureAsync(draft);

            Assert.True(first.Succeeded);
            Assert.False(first.AlreadyCaptured);
            Assert.True(duplicate.Succeeded);
            Assert.True(duplicate.AlreadyCaptured);
            var message = Assert.Single(await mailStore.GetMessagesAsync());
            Assert.Equal(MailDeliveryMode.Capture, message.DeliveryMode);
            Assert.Equal(2, message.Recipients.Count);
            Assert.Single(Directory.GetFiles(mailRoot, "*.eml", SearchOption.AllDirectories));
            await using var stream = await contentStore.OpenReadAsync(message.StorageKey);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!, Encoding.UTF8);
            var eml = await reader.ReadToEndAsync();
            Assert.Contains("To: Director <director@example.test>", eml, StringComparison.Ordinal);
            Assert.Contains("Bcc: audit@example.test", eml, StringComparison.Ordinal);
            Assert.Contains("multipart/alternative", eml, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Redirect_and_live_modes_are_disabled_in_this_release()
    {
        var store = new StubMailStore();
        var service = new MailCaptureService(
            store,
            new StubContentStore(),
            new MailRuntimeOptions(MailDeliveryMode.Redirect, "remi@example.test", "Remi"),
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync(new MailCaptureDraft(
            MailEventTypes.CustomerGoLive,
            "go-live:test",
            "Test",
            "Test",
            "<p>Test</p>",
            [new MailRecipient(Guid.NewGuid(), MailRecipientType.To, null, "director@example.test", 0)])));
    }

    [Fact]
    public async Task Monthly_inventory_uses_manual_trigger_time_for_new_and_excludes_later_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        var mailRoot = Path.Combine(root, "mail");
        Directory.CreateDirectory(root);
        try
        {
            var triggeredAt = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
            var reportedId = Guid.NewGuid();
            var newId = Guid.NewGuid();
            var lateId = Guid.NewGuid();
            var ongoingHistoricalId = Guid.NewGuid();
            var schemaStore = new SqliteRemiStore(databasePath);
            await schemaStore.UpdateAsync(database =>
            {
                database.Contracts.AddRange(
                [
                    Contract(reportedId, "REPORTED", "Reported Council", new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero)),
                    Contract(newId, "NEW-CONTRACT", "New Council", new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero)),
                    Contract(lateId, "LATE", "Late Council", new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero)),
                    Contract(ongoingHistoricalId, "G13-ONGOING", "Historical Framework Council", new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero)) with
                    {
                        Framework = FrameworkCode.GCloud13,
                    },
                ]);
                database.FrameworkConfigurations.Add(new FrameworkConfiguration(
                    FrameworkCode.GCloud15,
                    new DateOnly(2026, 1, 1),
                    new DateOnly(2028, 12, 31)));
                database.ContractServiceParts.AddRange(
                [
                    new ContractServicePart(Guid.NewGuid(), reportedId, "Earthlight", new DateOnly(2026, 1, 1), 0, DateTimeOffset.UtcNow),
                    new ContractServicePart(Guid.NewGuid(), newId, "Gazetteer", null, 0, DateTimeOffset.UtcNow),
                    new ContractServicePart(Guid.NewGuid(), lateId, "Planning", null, 0, DateTimeOffset.UtcNow),
                ]);
                database.ContractReportingOccurrences.AddRange(
                [
                    new ContractReportingOccurrence(Guid.NewGuid(), reportedId, Guid.NewGuid(), "2026-06", new DateTimeOffset(2026, 7, 7, 10, 0, 0, TimeSpan.Zero)),
                    new ContractReportingOccurrence(Guid.NewGuid(), newId, Guid.NewGuid(), "2026-07", new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero)),
                ]);
                return 0;
            });
            var mailStore = new SqliteRemiMailStore(databasePath, schemaStore);
            var template = Assert.Single(await mailStore.GetTemplatesAsync(), item => item.EventType == MailEventTypes.MonthlyActiveContracts);
            await mailStore.SaveTemplateAsync(new MailTemplateUpdate(
                template.EventType,
                true,
                template.SubjectTemplate,
                "Message before {{reporting_month}}.\n\n{{reportable_frameworks}}\n\n{{active_contracts}}\n\nMessage after the inventory.",
                [new MailRecipient(Guid.NewGuid(), MailRecipientType.To, null, "director@example.test", 0)]), triggeredAt);
            var contentStore = new FileMailContentStore(mailRoot);
            var capture = new MailCaptureService(mailStore, contentStore, new MailRuntimeOptions(MailDeliveryMode.Capture, "remi@example.test", "Remi"), new FixedTimeProvider(triggeredAt));
            var events = new RemiMailEventService(schemaStore, mailStore, capture, new DiscardEvidenceArchive());

            var result = await events.CaptureMonthlyActiveContractsAsync("2026-07", triggeredAt);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.CapturedMessage);
            var message = result.CapturedMessage!;
            Assert.Null(message.ScheduledForUtc);
            await using var stream = await contentStore.OpenReadAsync(message.StorageKey);
            using var reader = new StreamReader(stream!, Encoding.UTF8);
            var plainText = DecodeFirstMimePart(await reader.ReadToEndAsync());
            Assert.Contains("Reported Council", plainText, StringComparison.Ordinal);
            Assert.DoesNotContain("NEW - Jun 2026 - Reported Council", plainText, StringComparison.Ordinal);
            Assert.Contains("NEW - Jul 2026 - New Council", plainText, StringComparison.Ordinal);
            Assert.Contains("NOT live yet", plainText, StringComparison.Ordinal);
            Assert.DoesNotContain("Late Council", plainText, StringComparison.Ordinal);
            Assert.DoesNotContain("{{reportable_frameworks}}", plainText, StringComparison.Ordinal);
            Assert.DoesNotContain("{{active_contracts}}", plainText, StringComparison.Ordinal);
            var gCloud13Index = plainText.IndexOf("- G-Cloud 13", StringComparison.Ordinal);
            var gCloud14Index = plainText.IndexOf("- G-Cloud 14", StringComparison.Ordinal);
            var gCloud15Index = plainText.IndexOf("- G-Cloud 15", StringComparison.Ordinal);
            var vasIndex = plainText.IndexOf("- Vertical Application Solutions", StringComparison.Ordinal);
            Assert.True(gCloud13Index >= 0 && gCloud13Index < gCloud14Index);
            Assert.True(gCloud14Index < gCloud15Index);
            Assert.True(gCloud15Index < vasIndex);
            Assert.True(plainText.IndexOf("Jun 2026 - Reported Council", StringComparison.Ordinal)
                < plainText.IndexOf("Jul 2026 - New Council", StringComparison.Ordinal));
            Assert.True(plainText.IndexOf("Message before July 2026.", StringComparison.Ordinal)
                < gCloud13Index);
            Assert.True(plainText.IndexOf("Reported Council", StringComparison.Ordinal)
                < plainText.IndexOf("Message after the inventory.", StringComparison.Ordinal));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Post_submission_capture_embeds_latest_portal_evidence_for_each_framework()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        var mailRoot = Path.Combine(root, "mail");
        var evidenceRoot = Path.Combine(root, "evidence");
        Directory.CreateDirectory(root);
        try
        {
            var capturedAt = new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);
            var submissionAt = new DateTimeOffset(2026, 8, 6, 13, 45, 0, TimeSpan.Zero);
            var schemaStore = new SqliteRemiStore(databasePath);
            var evidenceArchive = new FileEvidenceArchive(evidenceRoot);
            var returnRows = new List<(FrameworkCode Framework, MonthlyReturn Return, EvidenceRecord Evidence)>();
            foreach (var framework in new[] { FrameworkCode.GCloud13, FrameworkCode.GCloud14, FrameworkCode.VerticalApplicationSolutions })
            {
                var returnId = Guid.NewGuid();
                var fileName = $"{framework}-accepted.png";
                var bytes = Encoding.UTF8.GetBytes($"portal-evidence-{framework}");
                await using var source = new MemoryStream(bytes);
                var archived = await evidenceArchive.ArchiveAsync(new EvidenceArchiveRequest(fileName, $"clipboard/monthly-return/{returnId:D}/{fileName}", "image/png", source));
                returnRows.Add((
                    framework,
                    new MonthlyReturn(returnId, framework, "2026-07", ReturnStatus.Submitted, submissionAt, $"GCA-{framework}", null, submissionAt),
                    new EvidenceRecord(Guid.NewGuid(), EvidenceKind.SubmissionEvidence, framework, "2026-07", fileName,
                        $"clipboard/monthly-return/{returnId:D}/{fileName}", archived.StoredRelativePath, "image/png", archived.FileSizeBytes,
                        archived.Sha256, null, submissionAt.AddSeconds(1))));
            }
            await schemaStore.UpdateAsync(database =>
            {
                foreach (var row in returnRows)
                {
                    database.MonthlyReturns.Add(row.Return);
                    database.Evidence.Add(row.Evidence);
                    database.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), submissionAt, "ReturnSubmitted", "MonthlyReturn", row.Return.Id, "Submitted.", row.Return.SubmissionReference, "test"));
                }
                return 0;
            });
            var mailStore = new SqliteRemiMailStore(databasePath, schemaStore);
            var template = Assert.Single(await mailStore.GetTemplatesAsync(), item => item.EventType == MailEventTypes.PostSubmissionReport);
            await mailStore.SaveTemplateAsync(new MailTemplateUpdate(
                template.EventType,
                true,
                template.SubjectTemplate,
                template.BodyTemplate,
                [new MailRecipient(Guid.NewGuid(), MailRecipientType.To, null, "director@example.test", 0)]), capturedAt);
            var contentStore = new FileMailContentStore(mailRoot);
            var capture = new MailCaptureService(mailStore, contentStore, new MailRuntimeOptions(MailDeliveryMode.Capture, "remi@example.test", "Remi"), new FixedTimeProvider(capturedAt));
            var events = new RemiMailEventService(schemaStore, mailStore, capture, evidenceArchive);

            var result = await events.CapturePostSubmissionReportAsync("2026-07");

            Assert.True(result.Succeeded, result.Message);
            Assert.NotNull(result.CapturedMessage);
            await using var stream = await contentStore.OpenReadAsync(result.CapturedMessage!.StorageKey);
            using var reader = new StreamReader(stream!, Encoding.UTF8);
            var eml = await reader.ReadToEndAsync();
            Assert.Contains("multipart/related", eml, StringComparison.Ordinal);
            Assert.Equal(3, CountOccurrences(eml, "Content-ID: <submission-202607-"));
            var plainText = DecodeFirstMimePart(eml);
            Assert.Contains("I'm pleased to inform you", plainText, StringComparison.Ordinal);
            Assert.Contains("G-Cloud 13", plainText, StringComparison.Ordinal);
            Assert.Contains("G-Cloud 14", plainText, StringComparison.Ordinal);
            Assert.Contains("VAS", plainText, StringComparison.Ordinal);
            Assert.Contains("This concludes our reporting obligations for this month.", plainText, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Post_submission_capture_is_blocked_when_a_framework_has_no_submission_evidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "remi-data.db");
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);
            var schemaStore = new SqliteRemiStore(databasePath);
            await schemaStore.UpdateAsync(database =>
            {
                foreach (var framework in new[] { FrameworkCode.GCloud13, FrameworkCode.GCloud14, FrameworkCode.VerticalApplicationSolutions })
                {
                    database.MonthlyReturns.Add(new MonthlyReturn(Guid.NewGuid(), framework, "2026-07", ReturnStatus.Submitted, now, null, null, now));
                }
                return 0;
            });
            var mailStore = new SqliteRemiMailStore(databasePath, schemaStore);
            var template = Assert.Single(await mailStore.GetTemplatesAsync(), item => item.EventType == MailEventTypes.PostSubmissionReport);
            await mailStore.SaveTemplateAsync(new MailTemplateUpdate(template.EventType, true, template.SubjectTemplate, template.BodyTemplate,
                [new MailRecipient(Guid.NewGuid(), MailRecipientType.To, null, "director@example.test", 0)]), now);
            var capture = new MailCaptureService(mailStore, new FileMailContentStore(Path.Combine(root, "mail")), new MailRuntimeOptions(MailDeliveryMode.Capture, "remi@example.test", "Remi"), new FixedTimeProvider(now));
            var events = new RemiMailEventService(schemaStore, mailStore, capture, new FileEvidenceArchive(Path.Combine(root, "evidence")));

            var result = await events.CapturePostSubmissionReportAsync("2026-07");

            Assert.False(result.Succeeded);
            Assert.Contains("no image evidence", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await mailStore.GetMessagesAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ContractRecord Contract(Guid id, string reference, string customer, DateTimeOffset createdAtUtc) =>
        new(id, FrameworkCode.GCloud14, reference, customer, "URN-1", new DateOnly(2026, 1, 1), new DateOnly(2028, 12, 31), "2", "Information and Communication Technology (ICT)", null, null, null, null, 1000, createdAtUtc.ToString("yyyy-MM"), "test", createdAtUtc);

    private static string DecodeFirstMimePart(string eml)
    {
        var contentStart = eml.IndexOf("Content-Transfer-Encoding: base64\r\n\r\n", StringComparison.Ordinal);
        Assert.True(contentStart >= 0);
        contentStart += "Content-Transfer-Encoding: base64\r\n\r\n".Length;
        var contentEnd = eml.IndexOf("\r\n--remi-", contentStart, StringComparison.Ordinal);
        Assert.True(contentEnd > contentStart);
        var encoded = eml[contentStart..contentEnd].Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubContentStore : IMailContentStore
    {
        public Task<(string StorageKey, long SizeBytes, string Sha256)> CreateAsync(Guid messageId, string eventType, DateTimeOffset createdAtUtc, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DiscardEvidenceArchive : IEvidenceArchive
    {
        public Task<ArchivedEvidenceFile> ArchiveAsync(EvidenceArchiveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubMailStore : IRemiMailStore
    {
        public Task EnsureSeededAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailTemplateDefinition>> GetTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailTemplateDefinition>>([]);
        public Task<MailTemplateDefinition?> GetTemplateAsync(string eventType, CancellationToken cancellationToken = default) => Task.FromResult<MailTemplateDefinition?>(null);
        public Task SaveTemplateAsync(MailTemplateUpdate update, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(int maximum = 100, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<MailMessageSummary?> GetMessageByDeliveryKeyAsync(string deliveryKey, CancellationToken cancellationToken = default) => Task.FromResult<MailMessageSummary?>(null);
        public Task<MailMessageSummary?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default) => Task.FromResult<MailMessageSummary?>(null);
        public Task SaveCaptureAsync(PersistedMailCapture capture, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
