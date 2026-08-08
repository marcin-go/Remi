using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Remi.Application;

public sealed record MailRuntimeOptions(
    MailDeliveryMode DeliveryMode,
    string FromAddress,
    string FromDisplayName);

public sealed class MailCaptureService(
    IRemiMailStore mailStore,
    IMailContentStore contentStore,
    MailRuntimeOptions options,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim captureLock = new(1, 1);

    public async Task<MailCaptureResult> CaptureAsync(
        MailCaptureDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (options.DeliveryMode != MailDeliveryMode.Capture)
        {
            throw new InvalidOperationException("This Remi release is Capture-only. Redirect and Live mail delivery are deliberately disabled.");
        }
        if (string.IsNullOrWhiteSpace(draft.DeliveryKey) || draft.DeliveryKey.Length > 240)
        {
            return new MailCaptureResult(false, false, "The mail event has no valid delivery key.", null);
        }
        if (string.IsNullOrWhiteSpace(draft.Subject))
        {
            return new MailCaptureResult(false, false, "The mail subject is empty.", null);
        }
        if (!draft.Recipients.Any(recipient => recipient.Type == MailRecipientType.To))
        {
            return new MailCaptureResult(false, false, "Configure at least one To recipient before capturing this message.", null);
        }

        await captureLock.WaitAsync(cancellationToken);
        try
        {
            await mailStore.EnsureSeededAsync(cancellationToken);
            var existing = await mailStore.GetMessageByDeliveryKeyAsync(draft.DeliveryKey, cancellationToken);
            if (existing is not null)
            {
                return new MailCaptureResult(true, true, "This event has already been captured.", existing);
            }

            var now = timeProvider.GetUtcNow();
            var messageId = Guid.NewGuid();
            var mime = RenderMime(messageId, draft, now);
            var content = await contentStore.CreateAsync(messageId, draft.EventType, now, mime, cancellationToken);
            await mailStore.SaveCaptureAsync(new PersistedMailCapture(
                messageId,
                draft,
                MailDeliveryMode.Capture,
                now,
                content.StorageKey,
                content.SizeBytes,
                content.Sha256), cancellationToken);
            var captured = await mailStore.GetMessageAsync(messageId, cancellationToken);
            return new MailCaptureResult(true, false, "The message has been captured as an immutable .eml file. Nothing was sent.", captured);
        }
        finally
        {
            captureLock.Release();
        }
    }

    private byte[] RenderMime(Guid messageId, MailCaptureDraft draft, DateTimeOffset createdAtUtc)
    {
        var alternativeBoundary = $"remi-alternative-{messageId:N}";
        var relatedBoundary = $"remi-related-{messageId:N}";
        var inlineAttachments = draft.InlineAttachments ?? [];
        var builder = new StringBuilder();
        Header(builder, "From", Address(options.FromDisplayName, options.FromAddress));
        AddRecipientHeader(builder, "To", draft.Recipients, MailRecipientType.To);
        AddRecipientHeader(builder, "Cc", draft.Recipients, MailRecipientType.Cc);
        AddRecipientHeader(builder, "Bcc", draft.Recipients, MailRecipientType.Bcc);
        Header(builder, "Date", createdAtUtc.ToString("r", CultureInfo.InvariantCulture));
        Header(builder, "Message-ID", $"<{messageId:N}@remi.local>");
        Header(builder, "Subject", EncodeHeader(draft.Subject));
        Header(builder, "MIME-Version", "1.0");
        Header(builder, "Content-Type", inlineAttachments.Count == 0
            ? $"multipart/alternative; boundary=\"{alternativeBoundary}\""
            : $"multipart/related; boundary=\"{relatedBoundary}\"");
        builder.Append("\r\n");
        if (inlineAttachments.Count != 0)
        {
            builder.Append("--").Append(relatedBoundary).Append("\r\n");
            Header(builder, "Content-Type", $"multipart/alternative; boundary=\"{alternativeBoundary}\"");
            builder.Append("\r\n");
        }
        AddBase64Part(builder, alternativeBoundary, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(draft.PlainTextBody));
        AddBase64Part(builder, alternativeBoundary, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(draft.HtmlBody));
        builder.Append("--").Append(alternativeBoundary).Append("--\r\n");
        if (inlineAttachments.Count != 0)
        {
            foreach (var attachment in inlineAttachments)
            {
                AddInlineAttachment(builder, relatedBoundary, attachment);
            }
            builder.Append("--").Append(relatedBoundary).Append("--\r\n");
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AddRecipientHeader(
        StringBuilder builder,
        string header,
        IReadOnlyList<MailRecipient> recipients,
        MailRecipientType type)
    {
        var values = recipients
            .Where(recipient => recipient.Type == type)
            .OrderBy(recipient => recipient.SortOrder)
            .Select(recipient => Address(recipient.DisplayName, recipient.EmailAddress))
            .ToList();
        if (values.Count != 0)
        {
            Header(builder, header, string.Join(", ", values));
        }
    }

    private static string Address(string? displayName, string address)
    {
        var safeAddress = SingleLine(address).Trim();
        return string.IsNullOrWhiteSpace(displayName)
            ? safeAddress
            : $"{EncodeHeader(SingleLine(displayName).Trim())} <{safeAddress}>";
    }

    private static void AddBase64Part(StringBuilder builder, string boundary, string contentType, ReadOnlySpan<byte> value)
    {
        builder.Append("--").Append(boundary).Append("\r\n");
        Header(builder, "Content-Type", contentType);
        Header(builder, "Content-Transfer-Encoding", "base64");
        builder.Append("\r\n");
        AppendBase64(builder, value);
    }

    private static void AddInlineAttachment(StringBuilder builder, string boundary, MailInlineAttachment attachment)
    {
        var fileName = Path.GetFileName(attachment.FileName).Replace("\"", string.Empty, StringComparison.Ordinal);
        var contentType = SingleLine(attachment.ContentType);
        var contentId = SingleLine(attachment.ContentId).Trim('<', '>');
        builder.Append("--").Append(boundary).Append("\r\n");
        Header(builder, "Content-Type", $"{contentType}; name=\"{fileName}\"");
        Header(builder, "Content-Transfer-Encoding", "base64");
        Header(builder, "Content-ID", $"<{contentId}>");
        Header(builder, "Content-Disposition", $"inline; filename=\"{fileName}\"");
        builder.Append("\r\n");
        AppendBase64(builder, attachment.Content.Span);
    }

    private static void AppendBase64(StringBuilder builder, ReadOnlySpan<byte> value)
    {
        var base64 = Convert.ToBase64String(value);
        for (var index = 0; index < base64.Length; index += 76)
        {
            builder.Append(base64, index, Math.Min(76, base64.Length - index)).Append("\r\n");
        }
    }

    private static void Header(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append(": ").Append(SingleLine(value)).Append("\r\n");

    private static string EncodeHeader(string value) =>
        value.All(character => character is >= ' ' and <= '~')
            ? SingleLine(value)
            : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(SingleLine(value)))}?=";

    private static string SingleLine(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
