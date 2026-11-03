using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Attendance;

public class AbsenceCode : TenantEntity
{
    public Guid CampusId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>present, authorized, unauthorized, late</summary>
    public string Category { get; set; } = "present";
    public bool NotifyParents { get; set; }
}

public class AttendanceRecord : TenantEntity
{
    public Guid EnrollmentId { get; set; }
    public Guid StudentId { get; set; }
    public Guid SectionId { get; set; }
    public DateOnly Date { get; set; }
    public Guid? PeriodId { get; set; }
    public string Code { get; set; } = "P";
    public string Category { get; set; } = "present";
    public string? Remarks { get; set; }
    public Guid MarkedById { get; set; }
    public DateTime MarkedAt { get; set; }
    public bool AlertSent { get; set; }
}

public class LeaveRequest : TenantEntity
{
    public Guid StaffId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Unpaid leave reduces pay in the payroll run for those days.</summary>
    public bool Unpaid { get; set; }
    /// <summary>pending, approved, rejected</summary>
    public string Status { get; set; } = "pending";
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
}
