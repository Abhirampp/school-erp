using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Students;

public abstract class Person : TenantEntity
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    /// <summary>Name in native script (e.g. Arabic, Devanagari).</summary>
    public string? NativeName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string CustomFieldsJson { get; set; } = "{}";
    public string FullName() => $"{FirstName} {LastName}".Trim();
}

public class Student : Person
{
    public Guid CampusId { get; set; }
    public string AdmissionNumber { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? PhotoPath { get; set; }
    public string? Category { get; set; }
    /// <summary>Encrypted at rest.</summary>
    public string? NationalId { get; set; }
    /// <summary>Encrypted at rest; visible only with students.medical.</summary>
    public string? MedicalNotes { get; set; }
    /// <summary>Encrypted at rest; visible only with students.medical.</summary>
    public string? SpecialNeeds { get; set; }
    /// <summary>active, withdrawn, transferred, graduated, erased</summary>
    public string Status { get; set; } = "active";
    public DateOnly? LeftOn { get; set; }
    public string? LeavingReason { get; set; }
}

public class Guardian : Person
{
    public string? Occupation { get; set; }
}

public class Staff : Person
{
    public Guid CampusId { get; set; }
    public string EmployeeNumber { get; set; } = "";
    public string? Designation { get; set; }
    public string? Department { get; set; }
    public string? Qualification { get; set; }
    public DateOnly? JoinedOn { get; set; }
    /// <summary>active or left</summary>
    public string Status { get; set; } = "active";
    public string? BankName { get; set; }
    /// <summary>Encrypted at rest; visible only with payroll.manage.</summary>
    public string? BankAccount { get; set; }
    /// <summary>Tax ID (e.g. PAN). Encrypted at rest; visible only with payroll.manage.</summary>
    public string? TaxId { get; set; }
}

public class StudentGuardian : TenantEntity
{
    public Guid StudentId { get; set; }
    public Guid GuardianId { get; set; }
    public string Relationship { get; set; } = "parent";
    public bool IsPrimary { get; set; }
}

public class Enrollment : TenantEntity
{
    public Guid StudentId { get; set; }
    public Guid SectionId { get; set; }
    public Guid AcademicYearId { get; set; }
    /// <summary>active, transferred, withdrawn, promoted, completed</summary>
    public string Status { get; set; } = "active";
    public int RollNumber { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

public class StudentDocument : TenantEntity
{
    public Guid StudentId { get; set; }
    public string Kind { get; set; } = "document";
    public string FileName { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
}
