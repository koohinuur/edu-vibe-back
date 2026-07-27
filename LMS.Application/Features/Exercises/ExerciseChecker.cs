using System.Linq;
using System.Text.Json;

namespace LMS.Application.Features.Exercises;

/// <summary>
/// Self-check scoring. Compares a user's answers against the exercise content, branching
/// on the free-string <c>type</c>. Comparison is always trimmed + case-insensitive.
/// Returns (score, total): total = number of gradable slots, score = number matched.
///
/// EXTENSIBILITY CONTRACT: to support a new exercise type, add ONE new case to the
/// switch — no other code (entity, handler, controller, migration) changes.
///
/// User-answer shapes accepted (forgiving): an object keyed by item id
/// <c>{ "1": "is" }</c>, OR an array by item order <c>["is", ...]</c>; per-item values
/// may be a single string or an array (multi-gap / dialogue lines).
/// </summary>
public static class ExerciseChecker
{
    public static (int Score, int Total) Check(string type, string contentJson, JsonElement userAnswers)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(contentJson) ? "{}" : contentJson);
        var content = doc.RootElement;

        return type switch
        {
            "mcq" or "mcq_ab" or "fill_blank" or "error_correction" or "transform"
                or "word_completion" or "matching" or "true_false" or "image_label"
                => CheckItems(content, userAnswers, multiGap: false),
            "word_bank_gap"
                => CheckItems(content, userAnswers, multiGap: true),
            "multi_select"
                => CheckMultiSelect(content, userAnswers),
            "underline" // student selects phrases in a passage; content.answers = correct phrases
                => CheckMultiSelect(content, userAnswers),
            "writing"
                => CheckWriting(content, userAnswers),
            "crossword"
                => CheckCrossword(content, userAnswers),
            "paragraph_cloze"
                => CheckCloze(content, userAnswers),
            "table_fill"
                => CheckTable(content, userAnswers),
            "dialogue"
                => CheckDialogue(content, userAnswers),
            "word_search"
                => CheckWordSearch(content, userAnswers),
            "multi"
                => CheckMulti(content, userAnswers),
            _ => (0, 0), // unknown type → ungraded (no crash); add a case to support it.
        };
    }

    // ---- per-type strategies -------------------------------------------------

    /// <summary>Composite (multi) exercise: content.sections[] are sub-exercises, each of its
    /// own type. The user's answers are keyed by section index ("0","1",…); score/total is the
    /// sum over sections. Recurses into <see cref="Check"/> — sections are always leaf types
    /// (the authoring UI forbids nesting a multi inside a multi).</summary>
    private static (int, int) CheckMulti(JsonElement content, JsonElement userAnswers)
    {
        if (!content.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            return (0, 0);

        int score = 0, total = 0, i = 0;
        foreach (var sec in sections.EnumerateArray())
        {
            var stype = sec.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var scontent = sec.TryGetProperty("content", out var c) ? c.GetRawText() : "{}";
            JsonElement subAnswers = default; // Undefined ⇒ the sub-checker treats it as "unanswered".
            if (userAnswers.ValueKind == JsonValueKind.Object && userAnswers.TryGetProperty(i.ToString(), out var sa))
                subAnswers = sa;
            var (s, t2) = Check(stype, scontent, subAnswers);
            score += s;
            total += t2;
            i++;
        }
        return (score, total);
    }

    /// <summary>items[] each with a single "answer" (or, when multiGap, an "answers" array
    /// = one expected per gap in that item).</summary>
    private static (int, int) CheckItems(JsonElement content, JsonElement userAnswers, bool multiGap)
    {
        int score = 0, total = 0;
        if (!content.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return (0, 0);

        var example = IsExample(content);
        var i = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (example && i == 0) { i++; continue; } // first item is a shown worked example — not graded
            var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : i.ToString();
            var userForItem = UserAnswerFor(userAnswers, id, i);

            // Multi-gap choice item: one option-set per blank, answer per gap.
            if (item.TryGetProperty("gaps", out var gaps) && gaps.ValueKind == JsonValueKind.Array)
            {
                var expectedGaps = new List<string>();
                foreach (var gap in gaps.EnumerateArray())
                    expectedGaps.Add(gap.TryGetProperty("answer", out var ga) ? ga.ToString() : string.Empty);
                var (s, t) = CheckByIndex(expectedGaps, AsList(userForItem));
                score += s;
                total += t;
            }
            else if (multiGap && item.TryGetProperty("answers", out var ans) && ans.ValueKind == JsonValueKind.Array)
            {
                var (s, t) = CheckByIndex(AsList(ans), AsList(userForItem));
                score += s;
                total += t;
            }
            else
            {
                total++;
                var expected = item.TryGetProperty("answer", out var a) ? a.ToString() : null;
                if (expected is not null && Norm(Single(userForItem)) == Norm(expected)) score++;
            }
            i++;
        }
        return (score, total);
    }

    /// <summary>Aligned compare of two string lists by index. total = expected.Count.</summary>
    private static (int, int) CheckByIndex(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var score = 0;
        for (var i = 0; i < expected.Count; i++)
            if (i < actual.Count && Norm(actual[i]) == Norm(expected[i])) score++;
        return (score, expected.Count);
    }

    /// <summary>items[] each with an "answers" array; the user's answer for that item is an array.</summary>
    private static (int, int) CheckDialogue(JsonElement content, JsonElement userAnswers)
    {
        int score = 0, total = 0;
        if (!content.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return (0, 0);

        var example = IsExample(content);
        var i = 0;
        foreach (var item in items.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : i.ToString();
            IReadOnlyList<string> expected = GetArray(item, "answers");
            IReadOnlyList<string> actual = AsList(UserAnswerFor(userAnswers, id, i));
            // First blank overall is a shown worked example — drop it from grading.
            if (example && i == 0 && expected.Count > 0)
            {
                expected = expected.Skip(1).ToList();
                actual = actual.Count > 0 ? actual.Skip(1).ToList() : actual;
            }
            var (s, t) = CheckByIndex(expected, actual);
            score += s;
            total += t;
            i++;
        }
        return (score, total);
    }

    /// <summary>paragraph_cloze: answers[] compared by index. When content.example is set the
    /// first gap is a shown worked example, so skip index 0 on both sides.</summary>
    private static (int, int) CheckCloze(JsonElement content, JsonElement userAnswers)
    {
        IReadOnlyList<string> expected = GetArray(content, "answers");
        IReadOnlyList<string> actual = AsList(userAnswers);
        if (IsExample(content) && expected.Count > 0)
        {
            expected = expected.Skip(1).ToList();
            actual = actual.Count > 0 ? actual.Skip(1).ToList() : actual;
        }
        return CheckByIndex(expected, actual);
    }

    /// <summary>True when content marks its first gradable slot as a worked example
    /// (shown pre-filled, not graded) — mirrors the textbook "item 1 is given" convention.</summary>
    private static bool IsExample(JsonElement content)
        => content.TryGetProperty("example", out var e) && e.ValueKind == JsonValueKind.True;

    /// <summary>"Tick all that apply": compare the user's ticked set against content.answers.
    /// total = number of correct answers; score = correctly ticked minus wrongly ticked (floored).</summary>
    private static (int, int) CheckMultiSelect(JsonElement content, JsonElement userAnswers)
    {
        var correct = GetArray(content, "answers").Select(Norm).ToHashSet();
        if (correct.Count == 0) return (0, 0);
        var ticked = AsList(userAnswers).Select(Norm).Where(s => s.Length > 0).ToHashSet();
        var correctTicked = ticked.Count(t => correct.Contains(t));
        var wrongTicked = ticked.Count - correctTicked;
        return (Math.Max(0, correctTicked - wrongTicked), correct.Count);
    }

    /// <summary>Writing: a free-text response (user answer under "text"). Prose can't be
    /// auto-graded, so it scores as "completed" (1/1) once the learner has written enough —
    /// at least <c>content.minWords</c> words (default 1). Keeps writing inside the self-check
    /// flow without pretending to mark it right/wrong; a teacher can still review the text.</summary>
    private static (int, int) CheckWriting(JsonElement content, JsonElement userAnswers)
    {
        var text = Single(UserAnswerFor(userAnswers, "text", -1)) ?? string.Empty;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var min = content.TryGetProperty("minWords", out var m) && m.TryGetInt32(out var mv) && mv > 0 ? mv : 1;
        return (words >= min ? 1 : 0, 1);
    }

    /// <summary>Crossword: content.entries = the words placed on the grid (number, direction,
    /// clue, answer, row, col). The user submits filled letters keyed by cell "r,c". total =
    /// entries; score = entries whose every cell matches the answer (case-insensitive).</summary>
    private static (int, int) CheckCrossword(JsonElement content, JsonElement userAnswers)
    {
        if (!content.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return (0, 0);

        int score = 0, total = 0;
        foreach (var e in entries.EnumerateArray())
        {
            var answer = Norm(e.TryGetProperty("answer", out var a) ? a.ToString() : "");
            if (answer.Length == 0) continue;
            total++;

            var down = e.TryGetProperty("direction", out var d) && d.GetString() == "down";
            var (dr, dc) = down ? (1, 0) : (0, 1);
            var row = e.TryGetProperty("row", out var r0) && r0.TryGetInt32(out var rv) ? rv : 0;
            var col = e.TryGetProperty("col", out var c0) && c0.TryGetInt32(out var cv) ? cv : 0;

            var ok = true;
            for (var i = 0; i < answer.Length; i++)
            {
                var user = Norm(Single(UserAnswerFor(userAnswers, $"{row + dr * i},{col + dc * i}", -1)));
                if (user != answer[i].ToString())
                {
                    ok = false;
                    break;
                }
            }
            if (ok) score++;
        }
        return (score, total);
    }

    /// <summary>Word search: content.words = the words to find; the user submits the array of
    /// words they located. total = distinct words; score = how many were found (case-insensitive).
    /// The grid + placements are for rendering only — scoring is by word, matching the self-check
    /// model (answers ship in the content).</summary>
    private static (int, int) CheckWordSearch(JsonElement content, JsonElement userAnswers)
    {
        var words = GetArray(content, "words").Select(Norm).Where(s => s.Length > 0).Distinct().ToList();
        if (words.Count == 0) return (0, 0);
        var found = AsList(userAnswers).Select(Norm).ToHashSet();
        return (words.Count(w => found.Contains(w)), words.Count);
    }

    /// <summary>Table completion: rows[].cells[]; a cell carrying an "answer" is a blank to
    /// fill (a cell with only "text" is pre-filled/given). User answers are keyed "r,c".
    /// total = number of blank cells; score = matched.</summary>
    private static (int, int) CheckTable(JsonElement content, JsonElement userAnswers)
    {
        int score = 0, total = 0;
        if (!content.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return (0, 0);

        var r = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
            {
                var c = 0;
                foreach (var cell in cells.EnumerateArray())
                {
                    if (cell.TryGetProperty("answer", out var ansEl))
                    {
                        total++;
                        var user = Single(UserAnswerFor(userAnswers, $"{r},{c}", -1));
                        if (Norm(user) == Norm(ansEl.ToString())) score++;
                    }
                    c++;
                }
            }
            r++;
        }
        return (score, total);
    }

    // ---- helpers -------------------------------------------------------------

    private static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    private static string? Single(JsonElement? el) => el is not { } e
        ? null
        : e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.ToString(),
            _ => null,
        };

    private static List<string> AsList(JsonElement? el)
    {
        var list = new List<string>();
        if (el is { ValueKind: JsonValueKind.Array } arr)
            foreach (var x in arr.EnumerateArray()) list.Add(Single(x) ?? string.Empty);
        else if (Single(el) is { } single)
            list.Add(single);
        return list;
    }

    private static List<string> GetArray(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var arr) ? AsList(arr) : new List<string>();

    /// <summary>The user's answer for an item — by id (object) or by order (array).</summary>
    private static JsonElement? UserAnswerFor(JsonElement userAnswers, string id, int index)
    {
        if (userAnswers.ValueKind == JsonValueKind.Object)
            return userAnswers.TryGetProperty(id, out var byId) ? byId : null;
        if (userAnswers.ValueKind == JsonValueKind.Array && index >= 0 && index < userAnswers.GetArrayLength())
            return userAnswers[index];
        return null;
    }
}
