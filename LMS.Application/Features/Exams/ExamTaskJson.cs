using System.Text.Json;

namespace LMS.Application.Features.Exams;

/// <summary>
/// Serializes a Writing/Speaking section's tasks (Task 1 / Task 2 …) to and from
/// the JSON stored on <c>ExamSection.TasksJson</c>. Kept tiny + tolerant so a
/// malformed value never breaks reading an exam.
/// </summary>
public static class ExamTaskJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Serialize(IReadOnlyList<ExamTaskDto>? tasks)
    {
        var clean = (tasks ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Title) || !string.IsNullOrWhiteSpace(t.Prompt) || !string.IsNullOrWhiteSpace(t.ImageUrl))
            .Select(t => new ExamTaskDto(
                (t.Title ?? "").Trim(),
                string.IsNullOrWhiteSpace(t.Prompt) ? null : t.Prompt!.Trim(),
                string.IsNullOrWhiteSpace(t.ImageUrl) ? null : t.ImageUrl!.Trim()))
            .ToList();
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean, Options);
    }

    public static IReadOnlyList<ExamTaskDto>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var tasks = JsonSerializer.Deserialize<List<ExamTaskDto>>(json, Options);
            return tasks is { Count: > 0 } ? tasks : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
