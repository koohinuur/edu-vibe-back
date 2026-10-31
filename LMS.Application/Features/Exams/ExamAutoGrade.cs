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
        if (key is not null && key.Count > 0)
        {
            var given = ParseGiven(answersJson);
            if (given is null) return null; // malformed / no captured answers → skip

            var raw = 0;
            foreach (var (label, accepted) in key)
                if (given.TryGetValue(label, out var studentAnswer) && accepted.Contains(studentAnswer))
                    raw++;
            return (raw, key.Count);
        }

        // No answer key uploaded → grade from the test's OWN captured verdict
        // (the HTML paper reports per-question "ok" and/or the "correct" answer),
        // so Listening/Reading papers with embedded answers need no key.
        return GradeFromCaptured(answersJson);
    }

    /// <summary>
    /// Grades from the captured blob's own verdict: counts questions the test marked
    /// with <c>ok</c> (boolean) or carried a <c>correct</c> value for, and how many
    /// the student got right. Returns null when the test reported no verdict at all.
    /// </summary>
    private static (int Raw, int Total)? GradeFromCaptured(string? answersJson)
    {
        if (string.IsNullOrWhiteSpace(answersJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(answersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Array)
                return null;

            int raw = 0, total = 0;
            foreach (var a in answers.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;

                var hasOk = a.TryGetProperty("ok", out var okEl)
                            && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False;
                var correct = a.TryGetProperty("correct", out var cEl)
                    ? (cEl.ValueKind == JsonValueKind.Number ? cEl.ToString()
                        : cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null)
                    : null;
                var hasCorrect = !string.IsNullOrWhiteSpace(correct);
                if (!hasOk && !hasCorrect) continue; // no verdict → not a gradable question

                total++;
                if (hasOk)
                {
                    if (okEl.GetBoolean()) raw++;
                }
                else
                {
                    var given = a.TryGetProperty("given", out var g)
                        ? (g.ValueKind == JsonValueKind.Number ? g.ToString() : g.GetString()) : null;
                    var nc = Norm(correct);
                    if (nc.Length > 0 && Norm(given) == nc) raw++;
                }
            }
            return total == 0 ? null : (raw, total);
        }
        catch (JsonException) { return null; }
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
