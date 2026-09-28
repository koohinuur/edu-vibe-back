using LMS.Application.Common.Abstractions;
using Microsoft.AspNetCore.Hosting;

namespace LMS.Infrastructure.Services;

/// <summary>
/// Disk-backed implementation of <see cref="IMaterialFileStore"/>. Files
/// land under <c>{ContentRoot}/uploads/materials/</c>. We do NOT expose
/// this directory through the static-file pipeline — downloads route through
/// the controller so RBAC + the Private-material class allowlist apply on
/// every request.
/// </summary>
public sealed class LocalMaterialFileStore : IMaterialFileStore
{
    // Generous whitelist for course materials — PDFs are the common case,
    // but slide decks, spreadsheets, audio/video, and reference images all
    // belong here too. Executables / scripts deliberately excluded.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc", ".docx",
        ".ppt", ".pptx",
        ".xls", ".xlsx",
        ".txt", ".md", ".csv",
        ".html", ".htm",
        ".png", ".jpg", ".jpeg", ".webp", ".gif",
        ".mp3", ".m4a", ".wav",
        ".mp4", ".webm", ".mov",
        ".zip",
    };

    private readonly string _root;

    public LocalMaterialFileStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(env.ContentRootPath, "uploads", "materials");
        if (!Directory.Exists(_root)) Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream source, string originalFileName, string mimeType, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"Material extension '{ext}' is not allowed. Permitted: {string.Join(", ", AllowedExtensions.OrderBy(e => e))}.");

        var storedFileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var absolutePath = Path.Combine(_root, storedFileName);
        await using var dest = File.Create(absolutePath);
        await source.CopyToAsync(dest, ct);
        return storedFileName;
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
