using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Modules.Academics;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Admissions;
using SchoolErp.Api.Modules.Attendance;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Modules.Security;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Data;

public class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser user) : DbContext(options)
{
    /// <summary>Evaluated per query by the global filter, so every query is tenant-filtered (NFR-SEC-02).</summary>
    public Guid CurrentTenantId => user.TenantId;
    public CurrentUser User => user;
    public bool SuppressAudit { get; set; }

    // Control plane
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SupportAccessRequest> SupportAccessRequests => Set<SupportAccessRequest>();

    // Admin
    public DbSet<Campus> Campuses => Set<Campus>();
    public DbSet<AcademicYear> AcademicYears => Set<AcademicYear>();
    public DbSet<Term> Terms => Set<Term>();
    public DbSet<GradeLevel> GradeLevels => Set<GradeLevel>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<CustomFieldDefinition> CustomFields => Set<CustomFieldDefinition>();

    // Students
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Guardian> Guardians => Set<Guardian>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<StudentGuardian> StudentGuardians => Set<StudentGuardian>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<StudentDocument> StudentDocuments => Set<StudentDocument>();

    // Admissions
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<ApplicationDocument> ApplicationDocuments => Set<ApplicationDocument>();
    public DbSet<AdmissionEvent> AdmissionEvents => Set<AdmissionEvent>();
    public DbSet<AdmissionFormConfig> AdmissionForms => Set<AdmissionFormConfig>();

    // Academics
    public DbSet<ClassSubject> ClassSubjects => Set<ClassSubject>();
    public DbSet<Period> Periods => Set<Period>();
    public DbSet<TimetableSlot> TimetableSlots => Set<TimetableSlot>();
    public DbSet<Substitution> Substitutions => Set<Substitution>();

    // Attendance
    public DbSet<AbsenceCode> AbsenceCodes => Set<AbsenceCode>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();

    // Exams
    public DbSet<GradingScheme> GradingSchemes => Set<GradingScheme>();
    public DbSet<AssessmentPlan> AssessmentPlans => Set<AssessmentPlan>();
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<Score> Scores => Set<Score>();

    // Fees
    public DbSet<FeeHead> FeeHeads => Set<FeeHead>();
    public DbSet<FeeStructure> FeeStructures => Set<FeeStructure>();
    public DbSet<FeeStructureItem> FeeStructureItems => Set<FeeStructureItem>();
    public DbSet<Installment> Installments => Set<Installment>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<StudentDiscount> StudentDiscounts => Set<StudentDiscount>();
    public DbSet<FeeAccount> FeeAccounts => Set<FeeAccount>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CreditNote> CreditNotes => Set<CreditNote>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    // Payroll
    public DbSet<SchoolErp.Api.Modules.Payroll.PayComponent> PayComponents => Set<SchoolErp.Api.Modules.Payroll.PayComponent>();
    public DbSet<SchoolErp.Api.Modules.Payroll.PayrollRun> PayrollRuns => Set<SchoolErp.Api.Modules.Payroll.PayrollRun>();
    public DbSet<SchoolErp.Api.Modules.Payroll.Payslip> Payslips => Set<SchoolErp.Api.Modules.Payroll.Payslip>();

    // Communication
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<MessageThread> MessageThreads => Set<MessageThread>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ConsentForm> ConsentForms => Set<ConsentForm>();
    public DbSet<Consent> Consents => Set<Consent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite has no decimal type; store as REAL so SUM/ORDER BY translate. PostgreSQL keeps numeric.
        if (Database.ProviderName?.Contains("Sqlite") == true)
            builder.Properties<decimal>().HaveConversion<double>();
        // Timestamps are stored and returned as UTC (NFR-L10N-02); the client formats them per campus locale.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var type in b.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(TenantEntity).IsAssignableFrom(t)).ToList())
            GetType().GetMethod(nameof(ConfigureTenantEntity), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(type).Invoke(this, [b]);

        b.Entity<Tenant>().HasIndex(t => t.Slug).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email);
        b.Entity<AuditEvent>().HasIndex(a => new { a.TenantId, a.At });
        b.Entity<SupportAccessRequest>().HasIndex(a => a.TenantId);
        b.Entity<AttendanceRecord>().HasIndex(a => new { a.TenantId, a.SectionId, a.Date });
        b.Entity<Enrollment>().HasIndex(a => new { a.TenantId, a.SectionId });
        b.Entity<Score>().HasIndex(a => new { a.TenantId, a.AssessmentId });
        b.Entity<Invoice>().HasIndex(a => new { a.TenantId, a.StudentId });
        b.Entity<Notification>().HasIndex(a => a.Status);

        var enc = new EncryptedConverter();
        b.Entity<Student>().Property(s => s.NationalId).HasConversion(enc);
        b.Entity<Student>().Property(s => s.MedicalNotes).HasConversion(enc);
        b.Entity<Student>().Property(s => s.SpecialNeeds).HasConversion(enc);
        b.Entity<Staff>().Property(s => s.BankAccount).HasConversion(enc);
        b.Entity<Staff>().Property(s => s.TaxId).HasConversion(enc);
        b.Entity<SchoolErp.Api.Modules.Payroll.Payslip>().HasIndex(p => new { p.TenantId, p.PayrollRunId });
    }

    private void ConfigureTenantEntity<T>(ModelBuilder b) where T : TenantEntity
    {
        b.Entity<T>().Property(e => e.Id).ValueGeneratedNever();
        b.Entity<T>().HasIndex(e => e.TenantId);
        b.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId && !e.IsDeleted);
    }

    private static readonly HashSet<string> Masked = ["PasswordHash", "MfaSecret", "NationalId", "MedicalNotes", "SpecialNeeds", "CodeHash", "BankAccount", "TaxId"];
    private static readonly HashSet<Type> NotAudited = [typeof(Notification), typeof(OtpCode)];

    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var audits = new List<AuditEvent>();
        foreach (var e in ChangeTracker.Entries<TenantEntity>().ToList())
        {
            string? action = null;
            switch (e.State)
            {
                case EntityState.Added:
                    // The tenant always comes from the request context, never from the payload.
                    if (CurrentTenantId != Guid.Empty) e.Entity.TenantId = CurrentTenantId;
                    e.Entity.CreatedAt = now;
                    action = "create";
                    break;
                case EntityState.Modified:
                    e.Property(x => x.TenantId).IsModified = false;
                    e.Entity.UpdatedAt = now;
                    action = "update";
                    break;
                case EntityState.Deleted:
                    e.State = EntityState.Unchanged;
                    e.Entity.IsDeleted = true;
                    e.Entity.UpdatedAt = now;
                    action = "delete";
                    break;
            }
            if (action is null || SuppressAudit || NotAudited.Contains(e.Entity.GetType())) continue;

            var props = e.Properties.Where(p => p.Metadata.Name is not ("TenantId" or "CreatedAt" or "UpdatedAt" or "IsDeleted")).ToList();
            object? Show(string name, object? v) => Masked.Contains(name) && v is not null ? "***" : v;
            Dictionary<string, object?>? before = null, after = null;
            if (action == "create") after = props.ToDictionary(p => p.Metadata.Name, p => Show(p.Metadata.Name, p.CurrentValue));
            else if (action == "delete") before = props.ToDictionary(p => p.Metadata.Name, p => Show(p.Metadata.Name, p.OriginalValue));
            else
            {
                var changed = props.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)).ToList();
                if (changed.Count == 0) continue;
                before = changed.ToDictionary(p => p.Metadata.Name, p => Show(p.Metadata.Name, p.OriginalValue));
                after = changed.ToDictionary(p => p.Metadata.Name, p => Show(p.Metadata.Name, p.CurrentValue));
            }
            audits.Add(new AuditEvent
            {
                TenantId = e.Entity.TenantId, ActorId = user.UserId, ActorName = user.Name, ViaSupportAccess = user.IsSupportSession,
                Action = action, EntityType = e.Entity.GetType().Name, EntityId = e.Entity.Id.ToString(),
                BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
                AfterJson = after is null ? null : JsonSerializer.Serialize(after), Ip = user.Ip, At = now
            });
        }
        AuditEvents.AddRange(audits);
        return await base.SaveChangesAsync(ct);
    }

    public override int SaveChanges() => SaveChangesAsync().GetAwaiter().GetResult();

    /// <summary>Records a non-CRUD event such as a data export or login.</summary>
    public void Audit(string action, string entityType, string? entityId = null, object? detail = null) =>
        AuditEvents.Add(new AuditEvent
        {
            TenantId = CurrentTenantId, ActorId = user.UserId, ActorName = user.Name, ViaSupportAccess = user.IsSupportSession,
            Action = action, EntityType = entityType, EntityId = entityId,
            AfterJson = detail is null ? null : JsonSerializer.Serialize(detail), Ip = user.Ip
        });
}
