using LMS.Domain.Common;

namespace LMS.Domain.Entities;

/// <summary>
/// An additional (co-)teacher of a <see cref="Class"/>. The class's primary
/// teacher stays on <see cref="Class.TeacherUserId"/>; this join row holds an
/// extra teacher. A co-teacher has the same access to the class as the primary —
/// they see it in their list, run its curriculum, take attendance, grade, and
/// message its students.
/// </summary>
public sealed class ClassTeacher : BaseEntity
{
    private ClassTeacher() { }

    public ClassTeacher(Guid classId, Guid userId)
    {
        ClassId = classId;
        UserId = userId;
    }

    public Guid ClassId { get; private set; }
    public Guid UserId { get; private set; }
}
