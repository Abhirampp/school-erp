using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Admissions;

public static class Stages
{
    public const string Enquiry = "enquiry", Applied = "applied", Assessed = "assessed", Offered = "offered",
        Waitlisted = "waitlisted", Rejected = "rejected", Enrolled = "enrolled";
    public static readonly string[] All = [Enquiry, Applied, Assessed, Offered, Waitlisted, Rejected, Enrolled];
}

public class Application : TenantEntity
{
    public Guid CampusId { get; set; }
    public Guid? AcademicYearId { get; set; }
    public Guid? GradeLevelId { get; set; }
    public string Stage { get; set; } = Stages.Enquiry;
    /// <summary>web, walk-in</summary>
    public string Source { get; set; } = "web";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string GuardianName { get; set; } = "";
    public string? GuardianEmail { get; set; }
    public string? GuardianPhone { get; set; }
    public string FormDataJson { get; set; } = "{}";
    public string? Notes { get; set; }
    public Guid? StudentId { get; set; }
}

public class ApplicationDocument : TenantEntity
{
    public Guid ApplicationId { get; set; }
    public string FileName { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
}

/// <summary>Entrance test or interview slot (FR-02-04).</summary>
public class AdmissionEvent : TenantEntity
{
    public Guid ApplicationId { get; set; }
    public string Kind { get; set; } = "interview";
    public DateTime ScheduledAt { get; set; }
    public string? Location { get; set; }
}

/// <summary>Configurable application form per campus (FR-02-02). FieldsJson: [{key,label,type,required}].</summary>
public class AdmissionFormConfig : TenantEntity
{
    public Guid CampusId { get; set; }
    public string FieldsJson { get; set; } = "[]";
    public string? Intro { get; set; }
}
