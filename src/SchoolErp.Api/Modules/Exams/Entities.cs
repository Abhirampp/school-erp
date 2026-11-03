using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Exams;

/// <summary>Configurable, versioned grading scheme locked to an academic year (FR-06-02).</summary>
public class GradingScheme : TenantEntity
{
    public Guid CampusId { get; set; }
    public Guid AcademicYearId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>percentage, letter, gpa, ib, cambridge, cbse. A label only: behaviour comes from DefinitionJson.</summary>
    public string ScaleType { get; set; } = "percentage";
    public int Version { get; set; } = 1;
    public string DefinitionJson { get; set; } = "{}";
}

public static class PlanStatus
{
    public const string Open = "open", Submitted = "submitted", Approved = "approved", Published = "published";
}

public class AssessmentPlan : TenantEntity
{
    public Guid AcademicYearId { get; set; }
    public Guid TermId { get; set; }
    public Guid ClassSubjectId { get; set; }
    public Guid SectionId { get; set; }
    public Guid GradingSchemeId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = PlanStatus.Open;
    public DateTime? MarksDeadline { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class Assessment : TenantEntity
{
    public Guid AssessmentPlanId { get; set; }
    public string Name { get; set; } = "";
    public decimal Weight { get; set; }
    public decimal MaxMarks { get; set; } = 100;
    public int Order { get; set; }
}

public class Score : TenantEntity
{
    public Guid AssessmentId { get; set; }
    public Guid EnrollmentId { get; set; }
    public decimal? Marks { get; set; }
    public bool IsAbsent { get; set; }
    public bool IsExcused { get; set; }
}
