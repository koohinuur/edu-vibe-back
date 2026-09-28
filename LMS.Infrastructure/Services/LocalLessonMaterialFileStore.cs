using LMS.Application.Common.Abstractions;
using Microsoft.AspNetCore.Hosting;

namespace LMS.Infrastructure.Services;

/// <summary>
/// Disk-backed <see cref="ILessonMaterialFileStore"/>. Files land under
/// <c>{ContentRoot}/uploads/lesson-materials/</c> — a private bucket NOT
/// exposed through the static-file pipeline, so every download goes through
/// the controller's enrolment/teacher access check. Mirrors the submission
/// file store; allows lesson content types (docs, slides, sheets, images,
/// archives, audio, video).
/// </summary>
public sealed class LocalLessonMaterialFileStore : ILessonMaterialFileStore
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc", ".docx",
        ".ppt", ".pptx",
        ".xls", ".xlsx",
        ".txt", ".md", ".csv", ".rtf",
        ".html", ".htm",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".heic", ".svg",
        ".zip", ".rar", ".7z",
        ".mp3", ".wav", ".m4a",
        ".mp4", ".webm", ".mov",
    };

    private readonly string _root;

    public LocalLessonMaterialFileStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(env.ContentRootPath, "uploads", "lesson-materials");
        if (!Directory.Exists(_root)) Directory.CreateDirectory(_root);
    }

    public async Task<SavedLessonMaterial> SaveAsync(Stream source, string originalFileName, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"File type '{ext}' is not allowed. Permitted: {string.Join(", ", AllowedExtensions.OrderBy(e => e))}.");

        var storedFileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var absolutePath = Path.Combine(_root, storedFileName);

        await using (var dest = File.Create(absolutePath))
        {
            await source.CopyToAsync(dest, ct);
        }
        var size = new FileInfo(absolutePath).Length;
        return new SavedLessonMaterial(storedFileName, size);
    }

    public Task<Stream?> OpenAsync(string storedFileName, CancellationToken ct)
    {
        var safe = Path.GetFileName(storedFileName);
        if (string.IsNullOrEmpty(safe) || safe != storedFileName) return Task.FromResult<Stream?>(null);
        var absolutePath = Path.Combine(_root, safe);
        if (!File.Exists(absolutePath)) return Task.FromResult<Stream?>(null);
        Stream s = File.OpenRead(absolutePath);
        return Task.FromResult<Stream?>(s);
    }

    public Task<bool> DeleteAsync(string storedFileName, CancellationToken ct)
    {
        var safe = Path.GetFileName(storedFileName);
        if (string.IsNullOrEmpty(safe) || safe != storedFileName) return Task.FromResult(false);
        var absolutePath = Path.Combine(_root, safe);
        if (!File.Exists(absolutePath)) return Task.FromResult(false);
        try { File.Delete(absolutePath); return Task.FromResult(true); }
        catch { return Task.FromResult(false); }
    }
}
