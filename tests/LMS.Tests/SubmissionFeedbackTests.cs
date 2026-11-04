using FluentAssertions;
using LMS.Domain.Entities;
using Xunit;

namespace LMS.Tests;

/// <summary>
/// Regression for the "Return"/grade 500: a long teacher note flows into three
/// length-capped columns — submissions.Feedback (4000), submission_audits.Detail
/// (1024), messages.Text (4000, covered in InboxNotifier). The domain must cap
/// instead of letting SaveChanges throw (the global handler maps the resulting
/// DbUpdateException to a 500 "Internal server error").
/// </summary>
public sealed class SubmissionFeedbackTests
{
    private static Submission NewSubmission() =>
        Submission.Create(Guid.NewGuid(), Guid.NewGuid(), "my essay", Array.Empty<Submission>());

    // ---- audit Detail (1024) ------------------------------------------------
    [Fact]
    public void Audit_detail_is_truncated_to_the_column_size()
    {
        var detail = new string('x', 2000);
        var audit = new SubmissionAudit(Guid.NewGuid(), Guid.NewGuid(), "returned", detail);
        audit.Detail!.Length.Should().Be(1024);
    }

    [Fact]
    public void Audit_detail_keeps_short_text_and_null()
    {
        new SubmissionAudit(Guid.NewGuid(), null, "graded", "score=8/10").Detail.Should().Be("score=8/10");
        new SubmissionAudit(Guid.NewGuid(), null, "created", null).Detail.Should().BeNull();
    }

    // ---- Feedback (4000) ----------------------------------------------------
    [Fact]
    public void Return_caps_feedback_to_the_column_size()
    {
        var s = NewSubmission();
        s.ReturnForRedo(new string('y', 5000));
        s.Feedback!.Length.Should().Be(4000);
    }

    [Fact]
    public void Grade_caps_feedback_and_trims()
    {
        var s = NewSubmission();
        s.Grade(8, 10, "  good work  ");
        s.Feedback.Should().Be("good work");

        s.Grade(8, 10, new string('z', 4100));
        s.Feedback!.Length.Should().Be(4000);
    }

    [Fact]
    public void Blank_feedback_becomes_null()
        => NewSubmission().Also(s => s.Grade(8, 10, "   ")).Feedback.Should().BeNull();
}

file static class TestExtensions
{
    public static T Also<T>(this T self, Action<T> act) { act(self); return self; }
}
