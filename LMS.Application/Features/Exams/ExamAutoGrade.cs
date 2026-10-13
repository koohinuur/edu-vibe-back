using System.Text.Json;

namespace LMS.Application.Features.Exams;

/// <summary>
/// Pure, DB-free auto-grading of an HTML Listening/Reading paper: compares the
/// student's captured answers against the section's answer key and returns how
/// many were correct. Kept out of the handler so it's fully unit-testable.
///
/// The answer key is a JSON object mapping each question label to its accepted
/// answer(s): <c>{ "q11": "A", "q12": ["20", "twenty"] }</c>. The captured answers
/// are the EduVibe answer-protocol blob the test posted back:
/// <c>{ "answers": [{ "q": "q11", "given": "A" }, ...] }</c>. Only questions present
/// in the key are graded (UI-widget noise like a volume slider is ignored).
/// </summary>
public static class ExamAutoGrade
{
    /// <summary>
    /// Grades <paramref name="answersJson"/> against <paramref name="answerKeyJson"/>.
    /// Returns (Raw correct, Total keyed questions), or null when there's nothing to
    /// grade — no parseable key, or no parseable captured answers. Matching is
    /// case-insensitive and whitespace-normalised.
    /// </summary>
    public static (int Raw, int Total)? Grade(string? answerKeyJson, string? answersJson)
    {
        var key = ParseKey(answerKeyJson);
        if (key is null || key.Count == 0) return null;

        var given = ParseGiven(answersJson);
        if (given is null) return null; // malformed / no captured answers → skip

        var raw = 0;
        foreach (var (label, accepted) in key)
            if (given.TryGetValue(label, out var studentAnswer) && accepted.Contains(studentAnswer))
                raw++;

        return (raw, key.Count);
    }

    /// <summary>Parses the key into label → set of accepted (normalised) answers. Null on bad JSON.</summary>
    private static Dictionary<string, HashSet<string>>? ParseKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, HashSet<string>>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var label = Norm(prop.Name);
                if (label.Length == 0) continue;
                var accepted = new HashSet<string>();
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        AddAnswer(accepted, prop.Value.GetString());
                        break;
                    case JsonValueKind.Number:
                        AddAnswer(accepted, prop.Value.ToString());
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in prop.Value.EnumerateArray())
                            AddAnswer(accepted, item.ValueKind == JsonValueKind.Number ? item.ToString() : item.GetString());
                        break;
                }
                if (accepted.Count > 0) map[label] = accepted;
            }
            return map;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Parses the captured answers into label → normalised given answer. Null on bad JSON / no answers.</summary>
    private static Dictionary<string, string>? ParseGiven(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Array)
                return null;

            var map = new Dictionary<string, string>();
            foreach (var a in answers.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                if (!a.TryGetProperty("q", out var q)) continue;
                var label = Norm(q.ValueKind == JsonValueKind.Number ? q.ToString() : q.GetString());
                if (label.Length == 0) continue;
                var given = a.TryGetProperty("given", out var g)
                    ? (g.ValueKind == JsonValueKind.Number ? g.ToString() : g.GetString())
                    : null;
                map[label] = Norm(given); // last write wins for a repeated label
            }
            return map.Count == 0 ? null : map;
        }
        catch (JsonException) { return null; }
    }

    private static void AddAnswer(HashSet<string> set, string? raw)
    {
        var n = Norm(raw);
        if (n.Length > 0) set.Add(n);
    }

    /// <summary>Trim, lowercase, and collapse internal whitespace — tolerant equality for answers.</summary>
    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return string.Join(' ', s.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
