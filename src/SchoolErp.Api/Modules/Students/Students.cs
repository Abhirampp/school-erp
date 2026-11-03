using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Modules.Identity;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Students;

/// <summary>Shared enrollment steps used by admissions, bulk import and the student screens.</summary>
public class StudentService(AppDbContext db)
{
    public async Task<string> NextAdmissionNumber()
    {
        var count = await db.Students.IgnoreQueryFilters().CountAsync(s => s.TenantId == db.CurrentTenantId);
        return $"ADM-{DateTime.UtcNow.Year}-{count + 1 + _issued++:D4}";
    }
    private int _issued;

    /// <summary>Finds a guardian by email or creates one, with a parent portal login (OTP sign-in).</summary>
    public async Task<Guardian> FindOrCreateGuardian(string name, string? email, string? phone)
    {
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        var g = email is null ? null : db.Guardians.Local.FirstOrDefault(x => x.Email == email) ?? await db.Guardians.FirstOrDefaultAsync(x => x.Email == email);
        if (g is not null) return g;
        var parts = name.Trim().Split(' ', 2);
        g = new Guardian { FirstName = parts[0], LastName = parts.Length > 1 ? parts[1] : "", Email = email, Phone = phone };
        db.Add(g);
        // Sign-in identifiers are unique across the platform, so a login is only created for an unused email.
        if (email is not null && !await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email && !u.IsDeleted))
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Scope == Scopes.Children);
            if (role is not null)
            {
                var u = new User { Email = email, Phone = phone, DisplayName = name.Trim(), RoleId = role.Id, GuardianId = g.Id };
                u.PasswordHash = TokenService.Hasher.HashPassword(u, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
                db.Add(u);
            }
        }
        return g;
    }

    public async Task<FeeAccount> EnsureFeeAccount(Guid guardianId)
    {
        var acc = db.FeeAccounts.Local.FirstOrDefault(a => a.GuardianId == guardianId) ?? await db.FeeAccounts.FirstOrDefaultAsync(a => a.GuardianId == guardianId);
        if (acc is not null) return acc;
        var n = await db.FeeAccounts.IgnoreQueryFilters().CountAsync(a => a.TenantId == db.CurrentTenantId) + db.FeeAccounts.Local.Count + 1;
        acc = new FeeAccount { GuardianId = guardianId, Number = $"FA-{n:D5}" };
        db.Add(acc);
        return acc;
    }

    public async Task<Enrollment> Enroll(Student s, Section section)
    {
        var roll = (await db.Enrollments.Where(e => e.SectionId == section.Id).MaxAsync(e => (int?)e.RollNumber) ?? 0)
                   + db.Enrollments.Local.Count(e => e.SectionId == section.Id && db.Entry(e).State == EntityState.Added) + 1;
        var e = new Enrollment { StudentId = s.Id, SectionId = section.Id, AcademicYearId = section.AcademicYearId, RollNumber = roll, StartDate = DateOnly.FromDateTime(DateTime.UtcNow) };
        db.Add(e);
        return e;
    }
}

public static class StudentEndpoints
{
    public record StudentRequest(Guid CampusId, string FirstName, string LastName, string? NativeName, DateOnly? DateOfBirth, string? Gender, string? Email, string? Phone,
        string? Category, string? NationalId, string? MedicalNotes, string? SpecialNeeds, string? CustomFieldsJson, Guid? SectionId,
        string? GuardianName, string? GuardianEmail, string? GuardianPhone, string? Relationship);
    public record GuardianLink(string Name, string? Email, string? Phone, string Relationship, bool IsPrimary);
    public record TransferRequest(Guid SectionId);
    public record WithdrawRequest(string Reason, DateOnly? Date, bool Transfer);

    /// <summary>Restricts a student query to what the caller's scope allows (campus, or assigned sections for teachers).</summary>
    public static async Task<IQueryable<Student>> Visible(AppDbContext db, CurrentUser user)
    {
        IQueryable<Student> q = db.Students;
        if (user.CampusId is { } c) q = q.Where(s => s.CampusId == c);
        if (user.Scope == Scopes.Assigned)
        {
            var sectionIds = await AssignedSectionIds(db, user);
            var ids = await db.Enrollments.Where(e => sectionIds.Contains(e.SectionId)).Select(e => e.StudentId).ToListAsync();
            q = q.Where(s => ids.Contains(s.Id));
        }
        return q;
    }

    public static async Task<List<Guid>> AssignedSectionIds(AppDbContext db, CurrentUser user)
    {
        if (user.StaffId is not { } staff) return [];
        var ids = await db.ClassSubjects.Where(c => c.TeacherId == staff).Select(c => c.SectionId).ToListAsync();
        ids.AddRange(await db.Sections.Where(s => s.ClassTeacherId == staff).Select(s => s.Id).ToListAsync());
        return ids.Distinct().ToList();
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/students");

        g.MapGet("", async (Guid? campusId, Guid? sectionId, string? search, string? status, AppDbContext db, CurrentUser user) =>
        {
            var q = await Visible(db, user);
            if (campusId is { } c) q = q.Where(s => s.CampusId == c);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(s => s.Status == status);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                q = q.Where(s => s.FirstName.ToLower().Contains(term) || s.LastName.ToLower().Contains(term) || s.AdmissionNumber.ToLower().Contains(term));
            }
            if (sectionId is { } sec)
            {
                var ids = await db.Enrollments.Where(e => e.SectionId == sec && e.Status == "active").Select(e => e.StudentId).ToListAsync();
                q = q.Where(s => ids.Contains(s.Id));
            }
            var students = await q.OrderBy(s => s.FirstName).ThenBy(s => s.LastName).Take(500).ToListAsync();
            var studentIds = students.Select(s => s.Id).ToList();
            var enrollments = await db.Enrollments.Where(e => studentIds.Contains(e.StudentId) && e.Status == "active").ToListAsync();
            var sections = await db.Sections.ToDictionaryAsync(s => s.Id);
            var grades = await db.GradeLevels.ToDictionaryAsync(x => x.Id, x => x.Name);
            return students.Select(s =>
            {
                var e = enrollments.Where(x => x.StudentId == s.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
                var sectionName = e is not null && sections.TryGetValue(e.SectionId, out var se) ? $"{grades.GetValueOrDefault(se.GradeLevelId)} {se.Name}" : null;
                return new { s.Id, s.AdmissionNumber, s.FirstName, s.LastName, s.NativeName, s.Gender, s.DateOfBirth, s.Status, s.CampusId, section = sectionName, sectionId = e?.SectionId, enrollmentId = e?.Id, rollNumber = e?.RollNumber };
            });
        }).Require(Perm.StudentsRead);

        g.MapGet("{id:guid}", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var s = await (await Visible(db, user)).FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var links = await db.StudentGuardians.Where(x => x.StudentId == id).ToListAsync();
            var guardianIds = links.Select(l => l.GuardianId).ToList();
            var guardians = await db.Guardians.Where(x => guardianIds.Contains(x.Id)).ToListAsync();
            // Siblings share at least one guardian.
            var siblingIds = await db.StudentGuardians.Where(x => guardianIds.Contains(x.GuardianId) && x.StudentId != id).Select(x => x.StudentId).Distinct().ToListAsync();
            var siblings = await db.Students.Where(x => siblingIds.Contains(x.Id)).Select(x => new { x.Id, x.FirstName, x.LastName, x.AdmissionNumber }).ToListAsync();
            var enrollments = await db.Enrollments.Where(e => e.StudentId == id).OrderByDescending(e => e.CreatedAt).ToListAsync();
            var sections = await db.Sections.ToDictionaryAsync(x => x.Id);
            var grades = await db.GradeLevels.ToDictionaryAsync(x => x.Id, x => x.Name);
            var years = await db.AcademicYears.ToDictionaryAsync(x => x.Id, x => x.Name);
            var docs = await db.StudentDocuments.Where(d => d.StudentId == id).Select(d => new { d.Id, d.Kind, d.FileName, d.Size, d.CreatedAt }).ToListAsync();
            var medical = user.Has(Perm.StudentsMedical);
            return Results.Ok(new
            {
                student = new
                {
                    s.Id, s.CampusId, s.AdmissionNumber, s.FirstName, s.LastName, s.NativeName, s.DateOfBirth, s.Gender, s.Email, s.Phone, s.Category, s.Status, s.LeftOn, s.LeavingReason,
                    s.CustomFieldsJson, hasPhoto = s.PhotoPath != null,
                    // Medical, special-needs and ID fields are only returned to authorised roles (FR-03-04).
                    nationalId = medical ? s.NationalId : null, medicalNotes = medical ? s.MedicalNotes : null, specialNeeds = medical ? s.SpecialNeeds : null,
                    canSeeMedical = medical
                },
                guardians = guardians.Select(x => { var l = links.First(k => k.GuardianId == x.Id); return new { x.Id, x.FirstName, x.LastName, x.Email, x.Phone, l.Relationship, l.IsPrimary }; }),
                siblings,
                enrollments = enrollments.Select(e => { sections.TryGetValue(e.SectionId, out var se); return new { e.Id, e.Status, e.RollNumber, e.StartDate, e.EndDate, year = years.GetValueOrDefault(e.AcademicYearId), section = se is null ? null : $"{grades.GetValueOrDefault(se.GradeLevelId)} {se.Name}", e.SectionId, e.AcademicYearId }; }),
                documents = docs
            });
        }).Require(Perm.StudentsRead);

        g.MapPost("", async (StudentRequest req, AppDbContext db, StudentService svc, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(req.CampusId)) return EndpointExtensions.Forbidden();
            if (string.IsNullOrWhiteSpace(req.FirstName)) return EndpointExtensions.Bad("First name is required.");
            var s = new Student { CampusId = req.CampusId, AdmissionNumber = await svc.NextAdmissionNumber() };
            Apply(s, req, user);
            db.Add(s);
            if (!string.IsNullOrWhiteSpace(req.GuardianName))
            {
                var guardian = await svc.FindOrCreateGuardian(req.GuardianName, req.GuardianEmail, req.GuardianPhone);
                db.Add(new StudentGuardian { StudentId = s.Id, GuardianId = guardian.Id, Relationship = req.Relationship ?? "parent", IsPrimary = true });
                await svc.EnsureFeeAccount(guardian.Id);
            }
            if (req.SectionId is { } sectionId && await db.Sections.FirstOrDefaultAsync(x => x.Id == sectionId) is { } section) await svc.Enroll(s, section);
            await db.SaveChangesAsync();
            return Results.Ok(new { s.Id, s.AdmissionNumber });
        }).Require(Perm.StudentsWrite);

        g.MapPut("{id:guid}", async (Guid id, StudentRequest req, AppDbContext db, CurrentUser user) =>
        {
            var s = await (await Visible(db, user)).FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            Apply(s, req, user);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.StudentsWrite);

        g.MapPost("{id:guid}/guardians", async (Guid id, GuardianLink req, AppDbContext db, StudentService svc) =>
        {
            if (!await db.Students.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var guardian = await svc.FindOrCreateGuardian(req.Name, req.Email, req.Phone);
            if (!await db.StudentGuardians.AnyAsync(x => x.StudentId == id && x.GuardianId == guardian.Id))
                db.Add(new StudentGuardian { StudentId = id, GuardianId = guardian.Id, Relationship = req.Relationship, IsPrimary = req.IsPrimary });
            if (req.IsPrimary) await svc.EnsureFeeAccount(guardian.Id);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.StudentsWrite);

        g.MapPost("{id:guid}/documents", async (Guid id, IFormFile file, string? kind, AppDbContext db, FileStore files) =>
        {
            var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var (path, error) = await files.Save(db.CurrentTenantId, file);
            if (error is not null) return EndpointExtensions.Bad(error);
            if (kind == "photo") s.PhotoPath = path;
            else db.Add(new StudentDocument { StudentId = id, Kind = kind ?? "document", FileName = Path.GetFileName(file.FileName), StoragePath = path, ContentType = file.ContentType, Size = file.Length });
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.StudentsWrite).DisableAntiforgery();

        g.MapGet("{id:guid}/photo", async (Guid id, AppDbContext db, FileStore files, CurrentUser user) =>
        {
            var s = await (await Visible(db, user)).FirstOrDefaultAsync(x => x.Id == id);
            var full = s?.PhotoPath is null ? null : files.Resolve(db.CurrentTenantId, s.PhotoPath);
            return full is null ? Results.NotFound() : Results.File(full, "image/" + Path.GetExtension(full).TrimStart('.').Replace("jpg", "jpeg"));
        }).Require(Perm.StudentsRead);

        g.MapGet("documents/{docId:guid}", async (Guid docId, AppDbContext db, FileStore files) =>
        {
            var d = await db.StudentDocuments.FirstOrDefaultAsync(x => x.Id == docId);
            var full = d is null ? null : files.Resolve(db.CurrentTenantId, d.StoragePath);
            return full is null ? Results.NotFound() : Results.File(full, d!.ContentType, d.FileName);
        }).Require(Perm.StudentsRead);

        // Move to another section in the same year; history is kept (FR-03-02).
        g.MapPost("{id:guid}/transfer-section", async (Guid id, TransferRequest req, AppDbContext db, StudentService svc) =>
        {
            var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id);
            var section = await db.Sections.FirstOrDefaultAsync(x => x.Id == req.SectionId);
            if (s is null || section is null) return Results.NotFound();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var e in await db.Enrollments.Where(e => e.StudentId == id && e.AcademicYearId == section.AcademicYearId && e.Status == "active").ToListAsync())
            {
                if (e.SectionId == section.Id) return EndpointExtensions.Bad("The student is already in that section.");
                e.Status = "transferred"; e.EndDate = today;
            }
            await svc.Enroll(s, section);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.StudentsWrite);

        // Withdrawal or transfer out of the school.
        g.MapPost("{id:guid}/withdraw", async (Guid id, WithdrawRequest req, AppDbContext db) =>
        {
            var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var date = req.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
            s.Status = req.Transfer ? "transferred" : "withdrawn"; s.LeftOn = date; s.LeavingReason = req.Reason;
            foreach (var e in await db.Enrollments.Where(e => e.StudentId == id && e.Status == "active").ToListAsync()) { e.Status = "withdrawn"; e.EndDate = date; }
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.StudentsWrite);

        g.MapGet("{id:guid}/transfer-certificate", async (Guid id, AppDbContext db) =>
        {
            var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            if (s.Status is not ("withdrawn" or "transferred")) return EndpointExtensions.Bad("Record a withdrawal or transfer before issuing a transfer certificate.");
            var campus = await db.Campuses.FirstAsync(c => c.Id == s.CampusId);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == db.CurrentTenantId);
            var last = await db.Enrollments.Where(e => e.StudentId == id).OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync();
            string? lastClass = null;
            if (last is not null && await db.Sections.FirstOrDefaultAsync(x => x.Id == last.SectionId) is { } sec)
                lastClass = $"{(await db.GradeLevels.FirstOrDefaultAsync(x => x.Id == sec.GradeLevelId))?.Name} {sec.Name}";
            db.Audit("export", "TransferCertificate", id.ToString());
            await db.SaveChangesAsync();
            var pdf = Pdf.TransferCertificate(tenant.Name, campus, s, lastClass);
            return Results.File(pdf, "application/pdf", $"TC-{s.AdmissionNumber}.pdf");
        }).Require(Perm.StudentsWrite);

        app.MapGet("/api/guardians", async (AppDbContext db) => await db.Guardians.OrderBy(x => x.FirstName).Take(500).ToListAsync()).Require(Perm.StudentsRead);
    }

    private static void Apply(Student s, StudentRequest r, CurrentUser user)
    {
        s.FirstName = r.FirstName.Trim(); s.LastName = (r.LastName ?? "").Trim(); s.NativeName = r.NativeName; s.DateOfBirth = r.DateOfBirth; s.Gender = r.Gender;
        s.Email = r.Email; s.Phone = r.Phone; s.Category = r.Category;
        if (r.CustomFieldsJson is not null) s.CustomFieldsJson = r.CustomFieldsJson;
        // Callers without the medical permission cannot read these fields, so they must not overwrite them either.
        if (user.Has(Perm.StudentsMedical)) { s.NationalId = r.NationalId; s.MedicalNotes = r.MedicalNotes; s.SpecialNeeds = r.SpecialNeeds; }
    }
}
