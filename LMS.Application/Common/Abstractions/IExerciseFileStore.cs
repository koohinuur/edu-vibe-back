namespace LMS.Application.Common.Abstractions;

/// <summary>
/// Disk-backed store for lesson-exercise document attachments — a reading passage or a
/// writing worksheet supplied as a PDF / Word file. Files are private: served only through
/// the authenticated endpoint, never the public static pipeline. Mirrors
/// <see cref="IExerciseImageStore"/> / <see cref="IExerciseAudioStore"/>.
/// </summary>
public interface IExerciseFileStore
{
    /// <summary>Persist an uploaded document; returns the opaque stored file name.
    /// Throws <see cref="InvalidOperationException"/> for a disallowed extension.</summary>
    Task<string> SaveAsync(Stream source, string originalFileName, CancellationToken ct);

    /// <summary>Open a stored document for streaming (with its content type), or null if
    /// the name is invalid / missing.</summary>
    Task<(Stream Stream, string ContentType)?> OpenAsync(string storedFileName, CancellationToken ct);
}
