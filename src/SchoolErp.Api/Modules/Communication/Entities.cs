using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Communication;

public class Announcement : TenantEntity
{
    public Guid CampusId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>campus, grade, section</summary>
    public string AudienceType { get; set; } = "campus";
    public Guid? AudienceId { get; set; }
    /// <summary>Comma-separated: inapp,email,sms,push</summary>
    public string Channels { get; set; } = "inapp,email";
    public string SentByName { get; set; } = "";
    public int RecipientCount { get; set; }
}

/// <summary>Outbox row: one message to one person over one channel, with delivery tracking.</summary>
public class Notification : TenantEntity
{
    public Guid? UserId { get; set; }
    public string Channel { get; set; } = "inapp";
    public string? Address { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Category { get; set; } = "general";
    /// <summary>pending, sent, failed</summary>
    public string Status { get; set; } = "pending";
    public DateTime? SentAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? Error { get; set; }
}

public class MessageThread : TenantEntity
{
    public Guid StudentId { get; set; }
    public Guid StaffUserId { get; set; }
    public Guid GuardianUserId { get; set; }
    public string Subject { get; set; } = "";
    public DateTime LastMessageAt { get; set; }
}

public class Message : TenantEntity
{
    public Guid ThreadId { get; set; }
    public Guid SenderUserId { get; set; }
    public string SenderName { get; set; } = "";
    public string Body { get; set; } = "";
}

public class ConsentForm : TenantEntity
{
    public Guid CampusId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string AudienceType { get; set; } = "campus";
    public Guid? AudienceId { get; set; }
    public DateOnly? DueDate { get; set; }
}

/// <summary>Stored record of a guardian's consent decision (FR-08-04).</summary>
public class Consent : TenantEntity
{
    public Guid ConsentFormId { get; set; }
    public Guid StudentId { get; set; }
    public Guid GuardianId { get; set; }
    public string GrantedByName { get; set; } = "";
    /// <summary>granted, declined</summary>
    public string Decision { get; set; } = "granted";
    public DateTime DecidedAt { get; set; }
    public DateTime? WithdrawnAt { get; set; }
    public string? Ip { get; set; }
}
