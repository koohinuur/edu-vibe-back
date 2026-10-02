using System.Security.Cryptography;
using LMS.Application.Common.Abstractions;
using Microsoft.AspNetCore.Hosting;

namespace LMS.Infrastructure.Services;

/// <summary>
/// Disk-backed <see cref="ISubmissionFileStore"/>. Files land under
/// <c>{ContentRoot}/uploads/submissions/</c> — a private bucket NOT exposed
/// through the static-file pipeline, so every download goes through the
/// controller's access check. The SHA-256 is computed while the bytes stream
/// to disk (CryptoStream), so the anti-cheat hash is free.
/// </summary>
public sealed class LocalSubmissionFileStore : ISubmissionFileStore
{
    // Homework deliverables: documents, slides, sheets, images, archives,
    // and plain text. Executables / scripts are deliberately excluded.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc", ".docx",
        ".ppt", ".pptx",
        ".xls", ".xlsx",
        ".txt", ".md", ".csv", ".rtf",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".heic",
        ".zip", ".rar", ".7z",
        // Audio — student Speaking cue-card recordings (MediaRecorder output).
        ".webm", ".ogg", ".mp3", ".m4a", ".wav",
    };

    private readonly string _root;

    public LocalSubmissionFileStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(env.ContentRootPath, "uploads", "submissions");
        if (!Directory.Exists(_root)) Directory.CreateDirectory(_root);
    }

    public async Task<SavedSubmissionFile> SaveAsync(
        Stream source, string originalFileName, string mimeType, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"File type '{ext}' is not allowed. Permitted: {string.Join(", ", AllowedExtensions.OrderBy(e => e))}.");

        var storedFileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var absolutePath = Path.Combine(_root, storedFileName);

        long size;
        string sha;
        using (var sha256 = SHA256.Create())
        await using (var dest = File.Create(absolutePath))
        await using (var crypto = new CryptoStream(dest, sha256, CryptoStreamMode.Write))
        {
            await source.CopyToAsync(crypto, ct);
            await crypto.FlushFinalBlockAsync(ct);
            sha = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
            size = new FileInfo(absolutePath).Length;
        }

        return new SavedSubmissionFile(storedFileName, sha, size);
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
