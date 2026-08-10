namespace Remi.Web;

public enum EvidencePreviewKind
{
    None,
    Image,
    Pdf,
    Text
}

public sealed record EvidencePreviewDefinition(EvidencePreviewKind Kind, string ContentType);

public static class EvidencePreviewPolicy
{
    public static EvidencePreviewKind GetKind(string fileName) => TryGetPreview(fileName, out var preview)
        ? preview.Kind
        : EvidencePreviewKind.None;

    public static bool TryGetPreview(string fileName, out EvidencePreviewDefinition preview)
    {
        preview = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => new EvidencePreviewDefinition(EvidencePreviewKind.Image, "image/png"),
            ".jpg" or ".jpeg" => new EvidencePreviewDefinition(EvidencePreviewKind.Image, "image/jpeg"),
            ".gif" => new EvidencePreviewDefinition(EvidencePreviewKind.Image, "image/gif"),
            ".webp" => new EvidencePreviewDefinition(EvidencePreviewKind.Image, "image/webp"),
            ".pdf" => new EvidencePreviewDefinition(EvidencePreviewKind.Pdf, "application/pdf"),
            ".txt" or ".csv" => new EvidencePreviewDefinition(EvidencePreviewKind.Text, "text/plain; charset=utf-8"),
            _ => null!
        };

        return preview is not null;
    }
}
