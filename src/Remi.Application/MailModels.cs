namespace Remi.Application;

public static class MailEventTypes
{
    public const string MonthlyActiveContracts = "monthly-active-contracts";
    public const string CustomerGoLive = "customer-go-live";
    public const string PostSubmissionReport = "post-submission-report";
    public const string ExpiringContracts = "expiring-contracts";
    public const string SubmissionDeadlineReminder = "submission-deadline-reminder";
}

public enum MailDeliveryMode
{
    Capture = 0,
    Redirect = 1,
    Live = 2,
}

public enum MailTriggerMode
{
    Automatic = 0,
    Manual = 1,
}

public enum MailRecipientType
{
    To = 0,
    Cc = 1,
    Bcc = 2,
}

public enum MailProcessingState
{
    Captured = 0,
    Failed = 1,
}

public sealed record MailRecipient(
    Guid Id,
    MailRecipientType Type,
    string? DisplayName,
    string EmailAddress,
    int SortOrder);

public sealed record MailTemplateDefinition(
    string EventType,
    string DisplayName,
    bool Enabled,
    MailTriggerMode TriggerMode,
    string SubjectTemplate,
    string BodyTemplate,
    // Retained for additive migration compatibility. New authoring and rendering use BodyTemplate.
    string Greeting,
    string Introduction,
    string RequestText,
    string Closing,
    string Signature,
    int? ScheduleDay,
    TimeOnly? ScheduleTimeLocal,
    string? TimeZoneId,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<MailRecipient> Recipients);

public sealed record MailTemplateUpdate(
    string EventType,
    bool Enabled,
    string SubjectTemplate,
    string BodyTemplate,
    IReadOnlyList<MailRecipient> Recipients);

public sealed record MailMessageSummary(
    Guid Id,
    string EventType,
    string Subject,
    MailProcessingState ProcessingState,
    MailDeliveryMode DeliveryMode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ScheduledForUtc,
    DateTimeOffset? CapturedAtUtc,
    string? SourcePeriod,
    string? RelatedEntityType,
    Guid? RelatedEntityId,
    string StorageKey,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<MailRecipient> Recipients);

public sealed record MailCaptureDraft(
    string EventType,
    string DeliveryKey,
    string Subject,
    string PlainTextBody,
    string HtmlBody,
    IReadOnlyList<MailRecipient> Recipients,
    DateTimeOffset? ScheduledForUtc = null,
    string? SourcePeriod = null,
    string? RelatedEntityType = null,
    Guid? RelatedEntityId = null,
    IReadOnlyList<MailInlineAttachment>? InlineAttachments = null);

/// <summary>
/// An image retained inside the immutable RFC 822 capture and referenced from the HTML body by
/// its Content-ID. The bytes are copied from Remi's evidence archive; the original evidence file
/// remains untouched.
/// </summary>
public sealed record MailInlineAttachment(
    string FileName,
    string ContentType,
    string ContentId,
    ReadOnlyMemory<byte> Content);

public sealed record MailCaptureResult(
    bool Succeeded,
    bool AlreadyCaptured,
    string Message,
    MailMessageSummary? CapturedMessage);

public sealed record PersistedMailCapture(
    Guid MessageId,
    MailCaptureDraft Draft,
    MailDeliveryMode DeliveryMode,
    DateTimeOffset CreatedAtUtc,
    string StorageKey,
    long SizeBytes,
    string Sha256);

public interface IRemiMailStore
{
    Task EnsureSeededAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailTemplateDefinition>> GetTemplatesAsync(CancellationToken cancellationToken = default);
    Task<MailTemplateDefinition?> GetTemplateAsync(string eventType, CancellationToken cancellationToken = default);
    Task SaveTemplateAsync(MailTemplateUpdate update, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(int maximum = 100, CancellationToken cancellationToken = default);
    Task<MailMessageSummary?> GetMessageByDeliveryKeyAsync(string deliveryKey, CancellationToken cancellationToken = default);
    Task<MailMessageSummary?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task SaveCaptureAsync(PersistedMailCapture capture, CancellationToken cancellationToken = default);
}

public interface IMailContentStore
{
    Task<(string StorageKey, long SizeBytes, string Sha256)> CreateAsync(
        Guid messageId,
        string eventType,
        DateTimeOffset createdAtUtc,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);
}
