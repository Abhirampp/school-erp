namespace SchoolErp.Api.Modules.Security;

/// <summary>Append-only audit record written for every create, update, delete and export.</summary>
public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public bool ViaSupportAccess { get; set; }
    /// <summary>create, update, delete, export, login, support-access</summary>
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? Ip { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>Support staff can enter a tenant only with tenant approval, time-limited and logged (FR-09-04).</summary>
public class SupportAccessRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RequestedById { get; set; }
    public string RequestedByName { get; set; } = "";
    public string Reason { get; set; } = "";
    public int Hours { get; set; } = 4;
    /// <summary>pending, approved, denied, revoked</summary>
    public string Status { get; set; } = "pending";
    public string? DecidedByName { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}
