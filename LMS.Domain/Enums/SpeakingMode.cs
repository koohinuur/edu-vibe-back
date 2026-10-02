namespace LMS.Domain.Enums;

/// <summary>
/// How a Speaking section is taken. Chosen per exam when the section is configured.
/// Ordinals are fixed — the frontend depends on them.
/// </summary>
public enum SpeakingMode
{
    /// <summary>
    /// The student records their answer to the cue card with their microphone; the
    /// teacher listens back and grades. The default.
    /// </summary>
    CueCard = 0,

    /// <summary>
    /// The speaking test is conducted live (e.g. Zoom/Meet) outside the app; the
    /// student has nothing to do in the runner and the teacher just grades + feeds back.
    /// </summary>
    Live = 1,
}
