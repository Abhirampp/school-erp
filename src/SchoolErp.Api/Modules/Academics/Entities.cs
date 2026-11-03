using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Academics;

public class ClassSubject : TenantEntity
{
    public Guid AcademicYearId { get; set; }
    public Guid SectionId { get; set; }
    public Guid SubjectId { get; set; }
    public Guid? TeacherId { get; set; }
}

public class Period : TenantEntity
{
    public Guid CampusId { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public bool IsBreak { get; set; }
}

public class TimetableSlot : TenantEntity
{
    public Guid AcademicYearId { get; set; }
    public Guid SectionId { get; set; }
    public Guid ClassSubjectId { get; set; }
    public Guid PeriodId { get; set; }
    /// <summary>1 = Monday … 7 = Sunday</summary>
    public int Day { get; set; }
    public Guid? TeacherId { get; set; }
    public string? Room { get; set; }
}

public class Substitution : TenantEntity
{
    public Guid TimetableSlotId { get; set; }
    public DateOnly Date { get; set; }
    public Guid SubstituteTeacherId { get; set; }
    public string? Reason { get; set; }
}
