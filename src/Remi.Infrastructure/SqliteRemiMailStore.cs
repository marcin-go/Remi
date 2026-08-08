using System.Globalization;
using Microsoft.Data.Sqlite;
using Remi.Application;

namespace Remi.Infrastructure;

public sealed class SqliteRemiMailStore(
    string databasePath,
    SqliteRemiStore schemaStore) : IRemiMailStore
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
    }.ToString();
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        await schemaStore.ReadAsync(_ => 0, cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (var template in DefaultTemplates(now))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT OR IGNORE INTO mail_templates (
                        event_type, display_name, enabled, trigger_mode, subject_template,
                        greeting, introduction, request_text, closing, signature,
                        schedule_day, schedule_time_local, time_zone_id, updated_at_utc)
                    VALUES (
                        $eventType, $displayName, 0, $triggerMode, $subject,
                        $greeting, $introduction, $requestText, $closing, $signature,
                        $scheduleDay, $scheduleTime, $timeZoneId, $updatedAtUtc);
                    """;
                Add(command, "$eventType", template.EventType);
                Add(command, "$displayName", template.DisplayName);
                Add(command, "$triggerMode", (int)template.TriggerMode);
                Add(command, "$subject", template.SubjectTemplate);
                Add(command, "$greeting", template.Greeting);
                Add(command, "$introduction", template.Introduction);
                Add(command, "$requestText", template.RequestText);
                Add(command, "$closing", template.Closing);
                Add(command, "$signature", template.Signature);
                Add(command, "$scheduleDay", template.ScheduleDay);
                Add(command, "$scheduleTime", template.ScheduleTimeLocal?.ToString("HH:mm", CultureInfo.InvariantCulture));
                Add(command, "$timeZoneId", template.TimeZoneId);
                Add(command, "$updatedAtUtc", now);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<MailTemplateDefinition>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var recipients = await LoadTemplateRecipientsAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_type, display_name, enabled, trigger_mode, subject_template,
                       greeting, introduction, request_text, closing, signature,
                       schedule_day, schedule_time_local, time_zone_id, updated_at_utc
                FROM mail_templates
                ORDER BY CASE event_type
                    WHEN 'monthly-active-contracts' THEN 0
                    WHEN 'customer-go-live' THEN 1
                    WHEN 'post-submission-report' THEN 2
                    WHEN 'expiring-contracts' THEN 3
                    ELSE 4 END, display_name;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var templates = new List<MailTemplateDefinition>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var eventType = reader.GetString(0);
                templates.Add(new MailTemplateDefinition(
                    eventType,
                    reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    (MailTriggerMode)reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : TimeOnly.ParseExact(reader.GetString(11), "HH:mm", CultureInfo.InvariantCulture),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
                    recipients.GetValueOrDefault(eventType, [])));
            }

            return templates;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<MailTemplateDefinition?> GetTemplateAsync(string eventType, CancellationToken cancellationToken = default) =>
        (await GetTemplatesAsync(cancellationToken)).SingleOrDefault(template => string.Equals(template.EventType, eventType, StringComparison.Ordinal));

    public async Task SaveTemplateAsync(
        MailTemplateUpdate update,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE mail_templates
                    SET enabled = $enabled, subject_template = $subject, greeting = $greeting,
                        introduction = $introduction, request_text = $requestText,
                        closing = $closing, signature = $signature, updated_at_utc = $updatedAtUtc
                    WHERE event_type = $eventType;
                    """;
                Add(command, "$enabled", update.Enabled ? 1 : 0);
                Add(command, "$subject", update.SubjectTemplate.Trim());
                Add(command, "$greeting", update.Greeting.Trim());
                Add(command, "$introduction", update.Introduction.Trim());
                Add(command, "$requestText", update.RequestText.Trim());
                Add(command, "$closing", update.Closing.Trim());
                Add(command, "$signature", update.Signature.Trim());
                Add(command, "$updatedAtUtc", updatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                Add(command, "$eventType", update.EventType);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException("The mail template no longer exists.");
                }
            }
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM mail_template_recipients WHERE event_type = $eventType;";
                Add(delete, "$eventType", update.EventType);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var recipient in update.Recipients.OrderBy(recipient => recipient.Type).ThenBy(recipient => recipient.SortOrder))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mail_template_recipients
                        (id, event_type, recipient_type, display_name, email_address, sort_order)
                    VALUES ($id, $eventType, $recipientType, $displayName, $emailAddress, $sortOrder);
                    """;
                Add(command, "$id", (recipient.Id == Guid.Empty ? Guid.NewGuid() : recipient.Id).ToString("D"));
                Add(command, "$eventType", update.EventType);
                Add(command, "$recipientType", (int)recipient.Type);
                Add(command, "$displayName", string.IsNullOrWhiteSpace(recipient.DisplayName) ? null : recipient.DisplayName.Trim());
                Add(command, "$emailAddress", recipient.EmailAddress.Trim());
                Add(command, "$sortOrder", recipient.SortOrder);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(int maximum = 100, CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await LoadMessagesAsync(connection, "ORDER BY messages.created_at_utc DESC LIMIT $maximum", null, Math.Clamp(maximum, 1, 500), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<MailMessageSummary?> GetMessageByDeliveryKeyAsync(string deliveryKey, CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return (await LoadMessagesAsync(connection, "WHERE messages.delivery_key = $filter", deliveryKey, 1, cancellationToken)).SingleOrDefault();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<MailMessageSummary?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return (await LoadMessagesAsync(connection, "WHERE messages.id = $filter", messageId.ToString("D"), 1, cancellationToken)).SingleOrDefault();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveCaptureAsync(PersistedMailCapture capture, CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var created = capture.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mail_messages (
                        id, event_type, delivery_key, processing_state, delivery_mode,
                        scheduled_for_utc, created_at_utc, captured_at_utc, subject,
                        related_entity_type, related_entity_id, source_period, failure_summary)
                    VALUES (
                        $id, $eventType, $deliveryKey, $state, $mode,
                        $scheduledForUtc, $createdAtUtc, $capturedAtUtc, $subject,
                        $relatedEntityType, $relatedEntityId, $sourcePeriod, NULL);
                    """;
                Add(command, "$id", capture.MessageId.ToString("D"));
                Add(command, "$eventType", capture.Draft.EventType);
                Add(command, "$deliveryKey", capture.Draft.DeliveryKey);
                Add(command, "$state", (int)MailProcessingState.Captured);
                Add(command, "$mode", (int)capture.DeliveryMode);
                Add(command, "$scheduledForUtc", capture.Draft.ScheduledForUtc?.ToString("O", CultureInfo.InvariantCulture));
                Add(command, "$createdAtUtc", created);
                Add(command, "$capturedAtUtc", created);
                Add(command, "$subject", capture.Draft.Subject);
                Add(command, "$relatedEntityType", capture.Draft.RelatedEntityType);
                Add(command, "$relatedEntityId", capture.Draft.RelatedEntityId?.ToString("D"));
                Add(command, "$sourcePeriod", capture.Draft.SourcePeriod);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var recipient in capture.Draft.Recipients)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO mail_recipients (id, mail_message_id, recipient_type, display_name, email_address) VALUES ($id, $messageId, $type, $displayName, $emailAddress);";
                Add(command, "$id", Guid.NewGuid().ToString("D"));
                Add(command, "$messageId", capture.MessageId.ToString("D"));
                Add(command, "$type", (int)recipient.Type);
                Add(command, "$displayName", recipient.DisplayName);
                Add(command, "$emailAddress", recipient.EmailAddress);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var content = connection.CreateCommand())
            {
                content.Transaction = transaction;
                content.CommandText = "INSERT INTO mail_contents (id, mail_message_id, attempt_number, storage_key, size_bytes, sha256, created_at_utc) VALUES ($id, $messageId, 1, $storageKey, $sizeBytes, $sha256, $createdAtUtc);";
                Add(content, "$id", Guid.NewGuid().ToString("D"));
                Add(content, "$messageId", capture.MessageId.ToString("D"));
                Add(content, "$storageKey", capture.StorageKey);
                Add(content, "$sizeBytes", capture.SizeBytes);
                Add(content, "$sha256", capture.Sha256);
                Add(content, "$createdAtUtc", created);
                await content.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var mailEvent = connection.CreateCommand())
            {
                mailEvent.Transaction = transaction;
                mailEvent.CommandText = "INSERT INTO mail_events (id, mail_message_id, event_type, occurred_at_utc, summary) VALUES ($id, $messageId, 0, $occurredAtUtc, $summary);";
                Add(mailEvent, "$id", Guid.NewGuid().ToString("D"));
                Add(mailEvent, "$messageId", capture.MessageId.ToString("D"));
                Add(mailEvent, "$occurredAtUtc", created);
                Add(mailEvent, "$summary", "Captured as an immutable .eml file. No external delivery was attempted.");
                await mailEvent.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string?> GetSchedulerPeriodAsync(string eventType, CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_evaluated_period FROM mail_scheduler_state WHERE event_type = $eventType;";
            Add(command, "$eventType", eventType);
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetSchedulerPeriodAsync(string eventType, string period, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mail_scheduler_state (event_type, last_evaluated_period, updated_at_utc)
                VALUES ($eventType, $period, $updatedAtUtc)
                ON CONFLICT(event_type) DO UPDATE SET
                    last_evaluated_period = excluded.last_evaluated_period,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            Add(command, "$eventType", eventType);
            Add(command, "$period", period);
            Add(command, "$updatedAtUtc", updatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<MailRecipient>>> LoadTemplateRecipientsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_type, recipient_type, display_name, email_address, sort_order FROM mail_template_recipients ORDER BY event_type, recipient_type, sort_order;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string EventType, MailRecipient Recipient)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(1), new MailRecipient(
                Guid.Parse(reader.GetString(0)),
                (MailRecipientType)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5))));
        }

        return rows.GroupBy(row => row.EventType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MailRecipient>)group.Select(row => row.Recipient).ToList(), StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<MailMessageSummary>> LoadMessagesAsync(
        SqliteConnection connection,
        string filterClause,
        string? filter,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT messages.id, messages.event_type, messages.subject, messages.processing_state,
                   messages.delivery_mode, messages.created_at_utc, messages.scheduled_for_utc,
                   messages.captured_at_utc, messages.source_period, messages.related_entity_type,
                   messages.related_entity_id, contents.storage_key, contents.size_bytes, contents.sha256
            FROM mail_messages messages
            INNER JOIN mail_contents contents ON contents.mail_message_id = messages.id AND contents.attempt_number = 1
            {filterClause};
            """;
        Add(command, "$filter", filter);
        Add(command, "$maximum", maximum);
        var rows = new List<MailMessageSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new MailMessageSummary(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    (MailProcessingState)reader.GetInt32(3),
                    (MailDeliveryMode)reader.GetInt32(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
                    reader.GetString(11),
                    reader.GetInt64(12),
                    reader.GetString(13),
                    []));
            }
        }

        var recipients = await LoadMessageRecipientsAsync(connection, rows.Select(row => row.Id).ToHashSet(), cancellationToken);
        return rows.Select(row => row with { Recipients = recipients.GetValueOrDefault(row.Id, []) }).ToList();
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<MailRecipient>>> LoadMessageRecipientsAsync(
        SqliteConnection connection,
        IReadOnlySet<Guid> messageIds,
        CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0) return [];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, mail_message_id, recipient_type, display_name, email_address FROM mail_recipients ORDER BY recipient_type, rowid;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(Guid MessageId, MailRecipient Recipient)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = Guid.Parse(reader.GetString(1));
            if (!messageIds.Contains(messageId)) continue;
            rows.Add((messageId, new MailRecipient(
                Guid.Parse(reader.GetString(0)),
                (MailRecipientType)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                rows.Count)));
        }
        return rows.GroupBy(row => row.MessageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MailRecipient>)group.Select(row => row.Recipient).ToList());
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static IEnumerable<MailTemplateDefinition> DefaultTemplates(string now) =>
    [
        new(MailEventTypes.MonthlyActiveContracts, "Monthly active contracts", false, MailTriggerMode.Automatic,
            "Framework reporting - active contracts for {{reporting_month}}",
            "Hello everyone,",
            "Another month has passed and the framework returns for {{reporting_month}} now need compiling.",
            "Please check the active-contract inventory below and reply with any new contracts, extensions or invoices that Remi does not yet contain.",
            "Take care",
            "Marcin Goralski\nGeneral Manager\nStatMap Ltd",
            1, new TimeOnly(9, 0), "Europe/London", DateTimeOffset.Parse(now), []),
        new(MailEventTypes.CustomerGoLive, "Customer going live", false, MailTriggerMode.Manual,
            "{{customer_name}} is now live - {{contract_reference}}",
            "Hello,",
            "{{customer_name}} has gone live under {{framework_name}} contract {{contract_reference}}.",
            "The operational systems and dates are listed below.",
            "Take care",
            "Marcin Goralski\nGeneral Manager\nStatMap Ltd",
            null, null, null, DateTimeOffset.Parse(now), []),
        new(MailEventTypes.PostSubmissionReport, "Post-submission report", false, MailTriggerMode.Manual,
            "Framework MI submission accepted - {{reporting_month}}",
            "Hi,",
            "I hope this message finds you well.\n\nI'm pleased to inform you that the G-Cloud and VAS monitoring information has been accepted by GCA.",
            "This month we reported the following values.",
            "This concludes our reporting obligations for this month.",
            "Take care\nMarcin",
            null, null, null, DateTimeOffset.Parse(now), []),
        new(MailEventTypes.ExpiringContracts, "Contracts expiring within three months", false, MailTriggerMode.Automatic,
            "Contracts approaching expiry", "Hello,", "The contracts below are approaching their recorded end dates.",
            "Please review the renewal or extension position.", "Take care", "Marcin Goralski\nGeneral Manager\nStatMap Ltd", 1, new TimeOnly(9, 15), "Europe/London", DateTimeOffset.Parse(now), []),
        new(MailEventTypes.SubmissionDeadlineReminder, "Submission deadline reminder", false, MailTriggerMode.Automatic,
            "Framework MI submission deadline reminder - {{reporting_month}}", "Hello,", "The monthly framework reporting deadline is approaching.",
            "Please ensure the return and its evidence are ready for submission.", "Take care", "Marcin Goralski\nGeneral Manager\nStatMap Ltd", null, null, "Europe/London", DateTimeOffset.Parse(now), []),
    ];

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
