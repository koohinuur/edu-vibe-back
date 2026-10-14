using FluentAssertions;
using LMS.Application.Features.Exams;
using LMS.Domain.Entities;
using Xunit;

namespace LMS.Tests;

/// <summary>
/// F8 offline-exam scoring: overall-% across sections, the pass/fail boundary
/// (>=), per-exam threshold override vs the system default, score-exceeds-max
/// rejection, and the destructive-section-edit rule.
/// </summary>
public sealed class ExamScoringTests
{
    private static ExamSectionInput S(decimal score, decimal max) => new(score, max);

    // ---- overall % ----------------------------------------------------------
    [Fact]
    public void Overall_percent_sums_scores_over_maxes()
    {
        // Reading 18/20 + Grammar 12/20 = 30/40 = 75%
        var r = ExamScoring.Compute(new[] { S(18, 20), S(12, 20) }, passThresholdPercent: 60m);
        r.OverallPercent.Should().Be(75m);
        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void Overall_percent_rounds_half_up_2dp()
        // 2/3 = 66.666… → 66.67
        => ExamScoring.Compute(new[] { S(2, 3) }, 60m).OverallPercent.Should().Be(66.67m);

    // ---- boundary (>=) ------------------------------------------------------
    [Theory]
    [InlineData(59.9, false)] // just below → fail
    [InlineData(60.0, true)]  // exactly at threshold → PASS
    [InlineData(60.1, true)]  // above → pass
    public void Pass_is_at_or_above_threshold(double score, bool expectedPass)
        => ExamScoring.Compute(new[] { S((decimal)score, 100m) }, 60m).Passed.Should().Be(expectedPass);

    // ---- per-exam override vs default fallback ------------------------------
    [Fact]
    public void Exam_threshold_falls_back_to_system_default_when_null()
    {
        var exam = new Exam(Guid.NewGuid(), Guid.NewGuid(), "Unit 1 Exam", passThresholdPercent: null);
        exam.EffectiveThresholdPercent.Should().Be(ExamDefaults.PassThresholdPercent);
        exam.EffectiveThresholdPercent.Should().Be(60m);
    }

    [Fact]
    public void Exam_threshold_uses_per_exam_override_when_set()
    {
        var exam = new Exam(Guid.NewGuid(), Guid.NewGuid(), "Hard Exam", passThresholdPercent: 70m);
        exam.EffectiveThresholdPercent.Should().Be(70m);
        // 65% passes the 60 default but fails this exam's 70 override.
        ExamScoring.Compute(new[] { S(65, 100) }, exam.EffectiveThresholdPercent).Passed.Should().BeFalse();
    }

    // ---- score > max validation --------------------------------------------
    [Fact]
    public void Score_within_max_is_valid()
        => ExamScoring.AreScoresValid(new[] { S(20, 20), S(0, 30) }).Should().BeTrue();

    [Fact]
    public void Score_above_max_is_rejected()
        => ExamScoring.AreScoresValid(new[] { S(21, 20) }).Should().BeFalse();

    [Fact]
    public void Negative_score_is_rejected()
        => ExamScoring.AreScoresValid(new[] { S(-1, 20) }).Should().BeFalse();

    [Fact]
    public void Empty_sections_are_invalid()
        => ExamScoring.AreScoresValid(Array.Empty<ExamSectionInput>()).Should().BeFalse();

    // ---- destructive section-edit rule -------------------------------------
    [Fact]
    public void Removing_a_section_is_destructive()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var existing = new[] { new ExamSectionState(a, 20m), new ExamSectionState(b, 20m) };
        var requested = new[] { new ExamSectionEdit(a, 20m) }; // b dropped
        ExamSectionChange.IsDestructive(existing, requested).Should().BeTrue();
    }

    [Fact]
    public void Decreasing_a_max_is_destructive()
    {
        var a = Guid.NewGuid();
        ExamSectionChange.IsDestructive(
            new[] { new ExamSectionState(a, 20m) },
            new[] { new ExamSectionEdit(a, 15m) }).Should().BeTrue();
    }

    [Fact]
    public void Adding_section_increasing_max_or_renaming_is_not_destructive()
    {
        var a = Guid.NewGuid();
        var requested = new[] { new ExamSectionEdit(a, 25m), new ExamSectionEdit(null, 30m) }; // a max↑, + new
        ExamSectionChange.IsDestructive(new[] { new ExamSectionState(a, 20m) }, requested).Should().BeFalse();
    }

    // ---- IELTS raw→band table ----------------------------------------------
    [Theory]
    [InlineData(40, 9.0)]
    [InlineData(39, 9.0)]
    [InlineData(37, 8.5)]
    [InlineData(30, 7.0)]
    [InlineData(23, 6.0)]
    [InlineData(16, 5.0)]
    [InlineData(0, 0.0)]
    public void Listening_band_from_raw(int raw, double band)
        => ExamScoring.IeltsBand(raw, isReading: false).Should().Be((decimal)band);

    [Theory]
    [InlineData(40, 9.0)]
    [InlineData(30, 7.0)]  // Reading needs 30 for 7 (Listening needs 30 too, but differs mid-band)
    [InlineData(27, 6.5)]
    [InlineData(23, 6.0)]
    [InlineData(15, 5.0)]
    public void Reading_band_from_raw(int raw, double band)
        => ExamScoring.IeltsBand(raw, isReading: true).Should().Be((decimal)band);

    [Fact]
    public void Band_clamps_raw_out_of_range()
    {
        ExamScoring.IeltsBand(99, isReading: false).Should().Be(9m);
        ExamScoring.IeltsBand(-5, isReading: false).Should().Be(0m);
    }

    // ---- auto-grade against the answer key ---------------------------------
    [Fact]
    public void Grade_counts_matches_case_and_space_insensitively()
    {
        const string key = """{ "q1": "A", "q2": ["20", "twenty"], "q3": "New York" }""";
        const string answers = """
            { "answers": [
              { "q": "q1", "given": "a" },
              { "q": "q2", "given": "Twenty" },
              { "q": "q3", "given": "new   york" },
              { "q": "volume-slider", "given": "7" }
            ] }
            """;
        var r = ExamAutoGrade.Grade(key, answers);
        r.Should().NotBeNull();
        r!.Value.Raw.Should().Be(3);
        r.Value.Total.Should().Be(3); // only keyed questions count; the slider is ignored
    }

    [Fact]
    public void Grade_counts_wrong_and_missing_as_not_correct()
    {
        const string key = """{ "q1": "A", "q2": "B", "q3": "C" }""";
        const string answers = """{ "answers": [ { "q": "q1", "given": "A" }, { "q": "q2", "given": "D" } ] }""";
        var r = ExamAutoGrade.Grade(key, answers);
        r!.Value.Raw.Should().Be(1); // q1 right, q2 wrong, q3 unanswered
        r.Value.Total.Should().Be(3);
    }

    [Fact]
    public void Grade_returns_null_when_nothing_to_grade()
    {
        ExamAutoGrade.Grade(null, """{ "answers": [] }""").Should().BeNull();        // no key
        ExamAutoGrade.Grade("""{ "q1": "A" }""", null).Should().BeNull();            // no answers
        ExamAutoGrade.Grade("""{ "q1": "A" }""", "not json").Should().BeNull();      // malformed
        ExamAutoGrade.Grade("""{ "q1": "A" }""", """{ "answers": [] }""").Should().BeNull(); // empty answers
    }
}
