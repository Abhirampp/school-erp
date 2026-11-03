using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Admissions;

public static class AdmissionEndpoints
{
    public record ApplicationRequest(Guid CampusId, Guid? GradeLevelId, string Kind, string FirstName, string LastName, DateOnly? DateOfBirth, string? Gender,
        string GuardianName, string? GuardianEmail, string? GuardianPhone, Dictionary<string, string>? FormData, string? Notes);
    public record StageRequest(string Stage, string? Notes);
    public record EventRequest(string Kind, DateTime ScheduledAt, string? Location);
    public record EnrollRequest(Guid SectionId);
    public record FormConfigRequest(Guid CampusId, string FieldsJson, string? Intro);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static void NotifyApplicant(AppDbContext db, Application a, string subject, string body)
    {
        if (!string.IsNullOrWhiteSpace(a.GuardianEmail))
            db.Notifications.Add(new Notification { Channel = "email", Address = a.GuardianEmail, Subject = subject, Body = body, Category = "admissions" });
    }

    private static string? Validate(ApplicationRequest r) =>
        string.IsNullOrWhiteSpace(r.FirstName) || string.IsNullOrWhiteSpace(r.GuardianName) ? "Applicant name and guardian name are required."
        : string.IsNullOrWhiteSpace(r.GuardianEmail) && string.IsNullOrWhiteSpace(r.GuardianPhone) ? "Give an email or phone number so the school can reply."
        : null;

    private static async Task<Application> Create(AppDbContext db, ApplicationRequest r, string source)
    {
        var year = await db.AcademicYears.Where(y => y.CampusId == r.CampusId && y.Status != "archived").OrderByDescending(y => y.StartDate).FirstOrDefaultAsync();
        var a = new Application
        {
            CampusId = r.CampusId, GradeLevelId = r.GradeLevelId, AcademicYearId = year?.Id, Source = source,
            Stage = r.Kind == "enquiry" ? Stages.Enquiry : Stages.Applied,
            FirstName = r.FirstName.Trim(), LastName = (r.LastName ?? "").Trim(), DateOfBirth = r.DateOfBirth, Gender = r.Gender,
            GuardianName = r.GuardianName.Trim(), GuardianEmail = r.GuardianEmail?.Trim().ToLowerInvariant(), GuardianPhone = r.GuardianPhone?.Trim(),
            FormDataJson = JsonSerializer.Serialize(r.FormData ?? []), Notes = r.Notes
        };
        db.Add(a);
        return a;
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        // ---- Public (no login): the school's enquiry and application form ----
        var pub = app.MapGroup("/api/public/{slug}").RequireRateLimiting("public");

        pub.MapGet("", async (string slug, AppDbContext db, CurrentUser cu) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug && t.Status == "active");
            if (tenant is null) return Results.NotFound();
            cu.TenantId = tenant.Id;
            var campuses = await db.Campuses.ToListAsync();
            var grades = await db.GradeLevels.OrderBy(x => x.Order).ToListAsync();
            var forms = await db.AdmissionForms.ToListAsync();
            return Results.Ok(new
            {
                school = tenant.Name,
                campuses = campuses.Select(c => new
                {
                    c.Id, c.Name, c.Locale,
                    grades = grades.Where(x => x.CampusId == c.Id).Select(x => new { x.Id, x.Name }),
                    intro = forms.FirstOrDefault(f => f.CampusId == c.Id)?.Intro,
                    fieldsJson = forms.FirstOrDefault(f => f.CampusId == c.Id)?.FieldsJson ?? "[]"
                })
            });
        });

        // multipart/form-data: "data" (JSON ApplicationRequest) plus any number of files.
        pub.MapPost("applications", async (string slug, HttpRequest http, AppDbContext db, CurrentUser cu, FileStore files) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug && t.Status == "active");
            if (tenant is null) return Results.NotFound();
            cu.TenantId = tenant.Id; cu.Name = "public form";
            var form = await http.ReadFormAsync();
            ApplicationRequest? req;
            try { req = JsonSerializer.Deserialize<ApplicationRequest>(form["data"].ToString(), Web); } catch (JsonException) { req = null; }
            if (req is null) return EndpointExtensions.Bad("Invalid form.");
            if (Validate(req) is { } err) return EndpointExtensions.Bad(err);
            if (!await db.Campuses.AnyAsync(c => c.Id == req.CampusId)) return EndpointExtensions.Bad("Unknown campus.");
            if (form.Files.Count > 5) return EndpointExtensions.Bad("Attach at most 5 files.");
            var a = await Create(db, req, "web");
            foreach (var f in form.Files)
            {
                var (path, error) = await files.Save(tenant.Id, f);
                if (error is not null) return EndpointExtensions.Bad($"{f.FileName}: {error}");
                db.Add(new ApplicationDocument { ApplicationId = a.Id, FileName = Path.GetFileName(f.FileName), StoragePath = path, ContentType = f.ContentType, Size = f.Length });
            }
            NotifyApplicant(db, a, $"We received your {(a.Stage == Stages.Enquiry ? "enquiry" : "application")}", $"Thank you. {tenant.Name} has received your {(a.Stage == Stages.Enquiry ? "enquiry" : "application")} for {a.FirstName} {a.LastName}.");
            await db.SaveChangesAsync();
            return Results.Ok(new { reference = a.Id.ToString("N")[..8].ToUpperInvariant() });
        }).DisableAntiforgery();

        // ---- Staff ----
        var g = app.MapGroup("/api/admissions").Require(Perm.Admissions);

        g.MapGet("applications", async (Guid campusId, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(campusId)) return EndpointExtensions.Forbidden();
            var grades = await db.GradeLevels.ToDictionaryAsync(x => x.Id, x => x.Name);
            var list = await db.Applications.Where(a => a.CampusId == campusId).OrderByDescending(a => a.CreatedAt).Take(500).ToListAsync();
            return Results.Ok(list.Select(a => new { a.Id, a.Stage, a.Source, a.FirstName, a.LastName, a.DateOfBirth, a.GuardianName, a.GuardianEmail, a.GuardianPhone, a.GradeLevelId, grade = a.GradeLevelId is { } gl ? grades.GetValueOrDefault(gl) : null, a.CreatedAt, a.StudentId }));
        });

        g.MapGet("applications/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            return Results.Ok(new
            {
                application = a,
                documents = await db.ApplicationDocuments.Where(d => d.ApplicationId == id).Select(d => new { d.Id, d.FileName, d.Size }).ToListAsync(),
                events = await db.AdmissionEvents.Where(e => e.ApplicationId == id).OrderBy(e => e.ScheduledAt).ToListAsync()
            });
        });

        g.MapGet("documents/{docId:guid}", async (Guid docId, AppDbContext db, FileStore files) =>
        {
            var d = await db.ApplicationDocuments.FirstOrDefaultAsync(x => x.Id == docId);
            var full = d is null ? null : files.Resolve(db.CurrentTenantId, d.StoragePath);
            return full is null ? Results.NotFound() : Results.File(full, d!.ContentType, d.FileName);
        });

        // Walk-in enquiry or application captured by the front office.
        g.MapPost("applications", async (ApplicationRequest req, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(req.CampusId)) return EndpointExtensions.Forbidden();
            if (Validate(req) is { } err) return EndpointExtensions.Bad(err);
            var a = await Create(db, req, "walk-in");
            await db.SaveChangesAsync();
            return Results.Ok(a);
        });

        g.MapPost("applications/{id:guid}/stage", async (Guid id, StageRequest req, AppDbContext db) =>
        {
            var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            if (!Stages.All.Contains(req.Stage) || req.Stage == Stages.Enrolled) return EndpointExtensions.Bad("Use the enrol action to enrol an applicant.");
            if (a.Stage == Stages.Enrolled) return EndpointExtensions.Bad("This applicant is already enrolled.");
            a.Stage = req.Stage;
            if (req.Notes is not null) a.Notes = req.Notes;
            if (req.Stage is Stages.Offered or Stages.Waitlisted or Stages.Rejected)
                NotifyApplicant(db, a, "Update on your application", req.Stage switch
                {
                    Stages.Offered => $"We are pleased to offer {a.FirstName} a place. The admissions office will contact you with next steps.",
                    Stages.Waitlisted => $"{a.FirstName}'s application has been placed on our waiting list.",
                    _ => $"We are sorry that we cannot offer {a.FirstName} a place at this time."
                });
            await db.SaveChangesAsync();
            return Results.Ok(a);
        });

        g.MapPost("applications/{id:guid}/events", async (Guid id, EventRequest req, AppDbContext db) =>
        {
            var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            var e = new AdmissionEvent { ApplicationId = id, Kind = req.Kind, ScheduledAt = req.ScheduledAt.ToUniversalTime(), Location = req.Location };
            db.Add(e);
            NotifyApplicant(db, a, $"Your {req.Kind} is scheduled", $"{a.FirstName}'s {req.Kind} is scheduled for {e.ScheduledAt:yyyy-MM-dd HH:mm} UTC{(req.Location is null ? "" : " at " + req.Location)}.");
            await db.SaveChangesAsync();
            return Results.Ok(e);
        });

        // FR-02-05: enrolling creates the student, guardian login, fee account and enrollment in one transaction.
        g.MapPost("applications/{id:guid}/enroll", async (Guid id, EnrollRequest req, AppDbContext db, StudentService svc) =>
        {
            var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            if (a.Stage == Stages.Enrolled) return EndpointExtensions.Bad("This applicant is already enrolled.");
            var section = await db.Sections.FirstOrDefaultAsync(s => s.Id == req.SectionId);
            if (section is null) return EndpointExtensions.Bad("Choose a section.");
            var taken = await db.Enrollments.CountAsync(e => e.SectionId == section.Id && e.Status == "active");
            if (taken >= section.Capacity) return EndpointExtensions.Bad($"That section is full ({taken}/{section.Capacity}).");

            var student = new Student { CampusId = a.CampusId, AdmissionNumber = await svc.NextAdmissionNumber(), FirstName = a.FirstName, LastName = a.LastName, DateOfBirth = a.DateOfBirth, Gender = a.Gender, CustomFieldsJson = a.FormDataJson };
            db.Add(student);
            var guardian = await svc.FindOrCreateGuardian(a.GuardianName, a.GuardianEmail, a.GuardianPhone);
            db.Add(new StudentGuardian { StudentId = student.Id, GuardianId = guardian.Id, IsPrimary = true });
            var account = await svc.EnsureFeeAccount(guardian.Id);
            await svc.Enroll(student, section);
            foreach (var d in await db.ApplicationDocuments.Where(x => x.ApplicationId == id).ToListAsync())
                db.Add(new StudentDocument { StudentId = student.Id, Kind = "admission", FileName = d.FileName, StoragePath = d.StoragePath, ContentType = d.ContentType, Size = d.Size });
            a.Stage = Stages.Enrolled; a.StudentId = student.Id;
            NotifyApplicant(db, a, "Welcome", $"{a.FirstName} is now enrolled (admission no. {student.AdmissionNumber}). Sign in to the parent portal with a one-time code sent to this email address.");
            await db.SaveChangesAsync();
            return Results.Ok(new { studentId = student.Id, student.AdmissionNumber, feeAccount = account.Number });
        });

        g.MapGet("form", async (Guid campusId, AppDbContext db) =>
            await db.AdmissionForms.FirstOrDefaultAsync(f => f.CampusId == campusId) ?? new AdmissionFormConfig { CampusId = campusId });

        g.MapPut("form", async (FormConfigRequest req, AppDbContext db) =>
        {
            try { JsonDocument.Parse(req.FieldsJson); } catch (JsonException) { return EndpointExtensions.Bad("Fields must be valid JSON."); }
            var f = await db.AdmissionForms.FirstOrDefaultAsync(x => x.CampusId == req.CampusId);
            if (f is null) db.Add(f = new AdmissionFormConfig { CampusId = req.CampusId });
            f.FieldsJson = req.FieldsJson; f.Intro = req.Intro;
            await db.SaveChangesAsync();
            return Results.Ok(f);
        });
    }
}
