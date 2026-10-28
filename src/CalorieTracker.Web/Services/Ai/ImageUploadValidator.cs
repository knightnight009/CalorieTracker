namespace CalorieTracker.Web.Services.Ai;

public record ValidatedImage(byte[] Bytes, string MimeType);

/// <summary>Checks size, extension, declared type and the file's actual magic bytes. Images stay in memory and are never written to disk.</summary>
public static class ImageUploadValidator
{
    public const long MaxBytes = 8 * 1024 * 1024;
    private static readonly Dictionary<string, string> ExtensionToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp",
    };

    public static async Task<(ValidatedImage? Image, string? Error)> ValidateAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return (null, "Please choose a photo to analyze.");
        if (file.Length > MaxBytes) return (null, $"The photo is too large. The limit is {MaxBytes / 1024 / 1024} MB.");
        if (!ExtensionToMime.TryGetValue(Path.GetExtension(file.FileName), out var mime))
            return (null, "Unsupported file type. Use a JPEG, PNG or WebP image.");
        if (!string.Equals(file.ContentType, mime, StringComparison.OrdinalIgnoreCase) && !(mime == "image/jpeg" && file.ContentType == "image/jpg"))
            return (null, "The file type doesn't match its extension.");

        using var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        return DetectMime(bytes) == mime ? (new ValidatedImage(bytes, mime), null) : (null, "The file isn't a valid image.");
    }

    public static string? DetectMime(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
