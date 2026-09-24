namespace LMS.Domain.Enums;

public enum SubmissionStatus
{
    Submitted = 1,
    Late = 2,
    Graded = 3,
    /// <summary>Sent back by the teacher for the student to redo + resubmit.</summary>
    Returned = 4
}