using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Admin;

/// <summary>A paying customer (school group). Not tenant-filtered: this table is the tenant directory.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Plan { get; set; } = "standard";
    public string Region { get; set; } = "india";
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Campus : TenantEntity
{
    public string Name { get; set; } = "";
    public string Country { get; set; } = "IN";
    public string Currency { get; set; } = "INR";
    public string TimeZone { get; set; } = "Asia/Kolkata";
    public string Locale { get; set; } = "en-IN";
    public string Curriculum { get; set; } = "CBSE";
    /// <summary>"daily" or "period" (FR-05-01).</summary>
    public string AttendanceMode { get; set; } = "daily";
    public decimal LateFinePercent { get; set; }
    public decimal LateFineFixed { get; set; }
    public int LateFineGraceDays { get; set; } = 7;
}

public class AcademicYear : TenantEntity
{
    public Guid CampusId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    /// <summary>draft, active, archived</summary>
    public string Status { get; set; } = "draft";
}

public class Term : TenantEntity
{
    public Guid AcademicYearId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int Order { get; set; }
}

public class GradeLevel : TenantEntity
{
    public Guid CampusId { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
}

public class Section : TenantEntity
{
    public Guid AcademicYearId { get; set; }
    public Guid GradeLevelId { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; } = 30;
    public Guid? ClassTeacherId { get; set; }
}

public class Subject : TenantEntity
{
    public Guid CampusId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Credits { get; set; } = 1;
}

public class Role : TenantEntity
{
    public string Name { get; set; } = "";
    public string Scope { get; set; } = Scopes.Tenant;
    /// <summary>Comma-separated permission keys.</summary>
    public string Permissions { get; set; } = "";
    public bool IsTemplate { get; set; }
}

public class User : TenantEntity
{
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public Guid RoleId { get; set; }
    public Guid? CampusId { get; set; }
    public Guid? StaffId { get; set; }
    public Guid? GuardianId { get; set; }
    public Guid? StudentId { get; set; }
    public string? MfaSecret { get; set; }
    public bool MfaEnabled { get; set; }
    public bool IsPlatformAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public string Locale { get; set; } = "en";
    public DateTime? LastLoginAt { get; set; }
}

public class OtpCode : TenantEntity
{
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
}

/// <summary>Custom fields on student, guardian and staff records without code changes (FR-01-05).</summary>
public class CustomFieldDefinition : TenantEntity
{
    public string EntityType { get; set; } = "student";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string FieldType { get; set; } = "text";
    public bool Required { get; set; }
}
