using System.Text.Json;

namespace LMS.Application.Features.Exercises;

/// <summary>
/// Pure XP math for fully completing a self-check <c>LessonExercise</c> in game mode.
/// Awarded ONCE per exercise (see <c>LessonExerciseSubmission.XpAwarded</c>), only on a
/// perfect score. 2 XP per gradable slot, floored at 5 and capped at 40 so a huge
/// exercise can't dwarf everything else. Kept out of the handler so it's unit-testable.
/// <paramref name="total"/> is the exercise's gradable-slot count; 0 ⇒ no award.
/// </summary>
public static class ExerciseXp
{
    public static int ForCompletion(int total)
    {
        if (total <= 0) return 0;
        return Math.Clamp(total * 2, 5, 40);
    }

    /// <summary>A teacher-set XP override from <c>content.xp</c> (number or numeric string),
    /// clamped to a sane 1..1000, or null to fall back to <see cref="ForCompletion"/>. A
    /// missing / non-positive / unparseable value ⇒ null.</summary>
    public static int? CustomFromContent(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("xp", out var xp))
                return null;
            var v = xp.ValueKind switch
            {
                JsonValueKind.Number when xp.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(xp.GetString(), out var n) => n,
                _ => 0,
            };
            return v > 0 ? Math.Clamp(v, 1, 1000) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
