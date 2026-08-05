namespace Remi.Web.Components;

public sealed record ClipboardImagePending(string Id, string FileName, long FileSizeBytes, string PreviewDataUrl);
