using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Identity;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Admin;

public static class AdminEndpoints
{
    public record UserRequest(string Email, string DisplayName, string? Password, Guid RoleId, Guid? CampusId, Guid? StaffId, string? Phone, bool IsActive = true, string Locale = "en");
    public record TenantUpdate(string Name);
    public record ImportError(int Row, string Message);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/tenant", async (AppDbContext db) => await db.Tenants.FirstAsync(t => t.Id == db.CurrentTenantId)).RequireStaff();
        app.MapPut("/api/admin/tenant", async (TenantUpdate req, AppDbContext db) =>
        {
            var t = await db.Tenants.FirstAsync(x => x.Id == db.CurrentTenantId);
            t.Name = req.Name;
            db.Audit("update", "Tenant", t.Id.ToString(), new { t.Name });
            await db.SaveChangesAsync();
            return t;
        }).Require(Perm.AdminSettings);

        app.MapCrud<Campus>("/api/campuses", null, Perm.AdminSettings);
        app.MapCrud<AcademicYear>("/api/academic-years", null, Perm.AdminSettings);
        app.MapCrud<Term>("/api/terms", null, Perm.AdminSettings);
        app.MapCrud<GradeLevel>("/api/grade-levels", null, Perm.AdminSettings);
        app.MapCrud<Section>("/api/sections", null, Perm.AdminSettings);
        app.MapCrud<Subject>("/api/subjects", null, Perm.AdminSettings);
        app.MapCrud<CustomFieldDefinition>("/api/custom-fields", null, Perm.AdminSettings);

        app.MapGet("/api/admin/permissions", () => new { permissions = Perm.All, scopes = Scopes.All }).Require(Perm.AdminUsers);

        var roles = app.MapGroup("/api/roles").Require(Perm.AdminUsers);
        roles.MapGet("", async (AppDbContext db) => await db.Roles.OrderBy(r => r.CreatedAt).ToListAsync());
        roles.MapPost("", async (Role body, AppDbContext db) =>
        {
            if (Validate(body) is { } err) return EndpointExtensions.Bad(err);
            var r = new Role { Name = body.Name.Trim(), Scope = body.Scope, Permissions = body.Permissions };
            db.Add(r);
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });
        roles.MapPut("{id:guid}", async (Guid id, Role body, AppDbContext db) =>
        {
            var r = await db.Roles.FirstOrDefaultAsync(x => x.Id == id);
            if (r is null) return Results.NotFound();
            if (Validate(body) is { } err) return EndpointExtensions.Bad(err);
            r.Name = body.Name.Trim(); r.Scope = body.Scope; r.Permissions = body.Permissions;
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });
        roles.MapDelete("{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var r = await db.Roles.FirstOrDefaultAsync(x => x.Id == id);
            if (r is null) return Results.NotFound();
            if (await db.Users.AnyAsync(u => u.RoleId == id)) return EndpointExtensions.Bad("This role still has users. Move them to another role first.");
            db.Remove(r);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        var users = app.MapGroup("/api/users").Require(Perm.AdminUsers);
        users.MapGet("", async (AppDbContext db) =>
        {
            var roleNames = await db.Roles.ToDictionaryAsync(r => r.Id, r => r.Name);
            return (await db.Users.OrderBy(u => u.DisplayName).ToListAsync()).Select(u => new
            {
                u.Id, u.Email, u.Phone, u.DisplayName, u.RoleId, role = roleNames.GetValueOrDefault(u.RoleId), u.CampusId, u.StaffId, u.GuardianId, u.StudentId,
                u.IsActive, u.MfaEnabled, u.LastLoginAt, u.Locale
            });
        });
        users.MapPost("", async (UserRequest req, AppDbContext db) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            if (!email.Contains('@')) return EndpointExtensions.Bad("Enter a valid email address.");
            if ((req.Password?.Length ?? 0) < 10) return EndpointExtensions.Bad("Password must be at least 10 characters.");
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email && !u.IsDeleted)) return EndpointExtensions.Bad("That email is already in use.");
            if (!await db.Roles.AnyAsync(r => r.Id == req.RoleId)) return EndpointExtensions.Bad("Role not found.");
            var u = new User { Email = email, DisplayName = req.DisplayName.Trim(), RoleId = req.RoleId, CampusId = req.CampusId, StaffId = req.StaffId, Phone = req.Phone, Locale = req.Locale };
            u.PasswordHash = TokenService.Hasher.HashPassword(u, req.Password!);
            db.Add(u);
            await db.SaveChangesAsync();
            return Results.Ok(new { u.Id });
        });
        users.MapPut("{id:guid}", async (Guid id, UserRequest req, AppDbContext db) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            if (!await db.Roles.AnyAsync(r => r.Id == req.RoleId)) return EndpointExtensions.Bad("Role not found.");
            u.DisplayName = req.DisplayName.Trim(); u.RoleId = req.RoleId; u.CampusId = req.CampusId; u.StaffId = req.StaffId; u.Phone = req.Phone; u.IsActive = req.IsActive; u.Locale = req.Locale;
            if (!string.IsNullOrEmpty(req.Password))
            {
                if (req.Password.Length < 10) return EndpointExtensions.Bad("Password must be at least 10 characters.");
                u.PasswordHash = TokenService.Hasher.HashPassword(u, req.Password);
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        app.MapGet("/api/admin/import/{kind}/template", (string kind) => Results.Text(kind == "staff"
            ? "EmployeeNumber,FirstName,LastName,Email,Phone,Designation\nEMP-101,Asha,Verma,asha.verma@example.com,+919800000001,Teacher\n"
            : "AdmissionNumber,FirstName,LastName,NativeName,DateOfBirth,Gender,Grade,Section,GuardianName,GuardianEmail,GuardianPhone,Relationship\n,Riya,Sharma,,2014-05-18,F,Grade 5,A,Neha Sharma,neha.sharma@example.com,+919800000002,mother\n",
            "text/csv")).Require(Perm.AdminImport);

        // Bulk import with validation; dryRun=true (the default) reports problems without writing anything (FR-01-04).
        app.MapPost("/api/admin/import/{kind}", async (string kind, Guid campusId, Guid? academicYearId, bool? dryRun, HttpRequest http, AppDbContext db, StudentService svc, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(campusId)) return EndpointExtensions.Forbidden();
            using var reader = new StreamReader(http.Body);
            var rows = Csv.Parse(await reader.ReadToEndAsync());
            if (rows.Count == 0) return EndpointExtensions.Bad("The file has no data rows.");
            if (rows.Count > 5000) return EndpointExtensions.Bad("Import at most 5,000 rows at a time.");
            var errors = new List<ImportError>();
            var isDry = dryRun ?? true;
            var created = 0;
            string Get(Dictionary<string, string> r, string k) => r.GetValueOrDefault(k, "").Trim();

            if (kind == "staff")
            {
                var existing = (await db.Staff.Select(s => s.EmployeeNumber).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i]; var line = i + 2;
                    var no = Get(r, "EmployeeNumber");
                    if (no == "") { errors.Add(new(line, "EmployeeNumber is required.")); continue; }
                    if (Get(r, "FirstName") == "") { errors.Add(new(line, "FirstName is required.")); continue; }
                    if (!existing.Add(no)) { errors.Add(new(line, $"Employee number {no} already exists or is repeated.")); continue; }
                    if (!isDry) db.Add(new Staff { CampusId = campusId, EmployeeNumber = no, FirstName = Get(r, "FirstName"), LastName = Get(r, "LastName"), Email = Get(r, "Email").ToLowerInvariant(), Phone = Get(r, "Phone"), Designation = Get(r, "Designation") });
                    created++;
                }
            }
            else if (kind == "students")
            {
                if (academicYearId is null) return EndpointExtensions.Bad("Choose the academic year to enrol into.");
                var grades = await db.GradeLevels.Where(x => x.CampusId == campusId).ToListAsync();
                var sections = await db.Sections.Where(x => x.AcademicYearId == academicYearId).ToListAsync();
                var existing = (await db.Students.Select(s => s.AdmissionNumber).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i]; var line = i + 2;
                    var rowErrors = new List<string>();
                    if (Get(r, "FirstName") == "") rowErrors.Add("FirstName is required.");
                    DateOnly? dob = null;
                    if (Get(r, "DateOfBirth") is { Length: > 0 } d)
                    {
                        if (DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) dob = parsed;
                        else rowErrors.Add($"DateOfBirth '{d}' must be YYYY-MM-DD.");
                    }
                    var no = Get(r, "AdmissionNumber");
                    if (no != "" && !existing.Add(no)) rowErrors.Add($"Admission number {no} already exists or is repeated.");
                    var grade = grades.FirstOrDefault(x => x.Name.Equals(Get(r, "Grade"), StringComparison.OrdinalIgnoreCase));
                    if (grade is null) rowErrors.Add($"Grade '{Get(r, "Grade")}' does not exist in this campus.");
                    var section = grade is null ? null : sections.FirstOrDefault(x => x.GradeLevelId == grade.Id && x.Name.Equals(Get(r, "Section"), StringComparison.OrdinalIgnoreCase));
                    if (grade is not null && section is null) rowErrors.Add($"Section '{Get(r, "Section")}' does not exist in {grade.Name} for this year.");
                    if (Get(r, "GuardianEmail") is { Length: > 0 } ge && !ge.Contains('@')) rowErrors.Add("GuardianEmail is not a valid email.");
                    if (rowErrors.Count > 0) { errors.AddRange(rowErrors.Select(m => new ImportError(line, m))); continue; }
                    created++;
                    if (isDry) continue;
                    var s = new Student { CampusId = campusId, AdmissionNumber = no != "" ? no : await svc.NextAdmissionNumber(), FirstName = Get(r, "FirstName"), LastName = Get(r, "LastName"), NativeName = Get(r, "NativeName"), DateOfBirth = dob, Gender = Get(r, "Gender") };
                    db.Add(s);
                    await svc.Enroll(s, section!);
                    if (Get(r, "GuardianName") != "")
                    {
                        var guardian = await svc.FindOrCreateGuardian(Get(r, "GuardianName"), Get(r, "GuardianEmail"), Get(r, "GuardianPhone"));
                        db.Add(new StudentGuardian { StudentId = s.Id, GuardianId = guardian.Id, Relationship = Get(r, "Relationship") is { Length: > 0 } rel ? rel : "parent", IsPrimary = true });
                        await svc.EnsureFeeAccount(guardian.Id);
                    }
                }
            }
            else return EndpointExtensions.Bad("Unknown import type.");

            // All-or-nothing: a real run only commits when every row is valid.
            if (!isDry && errors.Count > 0) return Results.Ok(new { dryRun = false, committed = false, total = rows.Count, valid = created, imported = 0, errors });
            if (!isDry) { db.Audit("import", kind, null, new { rows = created }); await db.SaveChangesAsync(); }
            return Results.Ok(new { dryRun = isDry, committed = !isDry, total = rows.Count, valid = created, imported = isDry ? 0 : created, errors });
        }).Require(Perm.AdminImport);

        app.MapGet("/api/dashboard", async (Guid campusId, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(campusId)) return EndpointExtensions.Forbidden();
            var campus = await db.Campuses.FirstOrDefaultAsync(c => c.Id == campusId);
            if (campus is null) return Results.NotFound();
            var today = Clock.Today(campus.TimeZone);
            var year = await db.AcademicYears.Where(y => y.CampusId == campusId && y.Status == "active").OrderByDescending(y => y.StartDate).FirstOrDefaultAsync();
            var sectionIds = year is null ? [] : await db.Sections.Where(s => s.AcademicYearId == year.Id).Select(s => s.Id).ToListAsync();
            var enrolled = await db.Enrollments.CountAsync(e => sectionIds.Contains(e.SectionId) && e.Status == "active");
            var attendance = await db.AttendanceRecords.Where(a => sectionIds.Contains(a.SectionId) && a.Date == today).ToListAsync();
            var canFees = user.Has(Perm.FeesRead) || user.Has(Perm.FeesManage);
            var invoices = canFees ? await db.Invoices.Where(i => i.CampusId == campusId && i.Status != "void").ToListAsync() : [];
            // Chart series: attendance for the last 10 marked school days, class sizes, and the admissions pipeline.
            var recent = await db.AttendanceRecords.Where(a => sectionIds.Contains(a.SectionId) && a.Date <= today && a.Date >= today.AddDays(-30)).Select(a => new { a.Date, a.Category }).ToListAsync();
            var attendanceTrend = recent.GroupBy(a => a.Date).OrderByDescending(x => x.Key).Take(10).OrderBy(x => x.Key).Select(x => new
            {
                date = x.Key, total = x.Count(), present = x.Count(a => a.Category == "present"), late = x.Count(a => a.Category == "late"),
                absent = x.Count(a => a.Category is "authorized" or "unauthorized"),
                percent = Math.Round(100m * x.Count(a => a.Category is "present" or "late") / x.Count(), 1)
            }).ToList();
            var grades = await db.GradeLevels.Where(x => x.CampusId == campusId).ToDictionaryAsync(x => x.Id);
            var sections = year is null ? [] : await db.Sections.Where(s => s.AcademicYearId == year.Id).ToListAsync();
            var enrollmentSections = await db.Enrollments.Where(e => sectionIds.Contains(e.SectionId) && e.Status == "active").Select(e => e.SectionId).ToListAsync();
            var classSizes = sections.OrderBy(s => grades.TryGetValue(s.GradeLevelId, out var gl) ? gl.Order : 0).ThenBy(s => s.Name)
                .Select(s => new { label = $"{(grades.TryGetValue(s.GradeLevelId, out var gl) ? gl.Name : "")} {s.Name}", count = enrollmentSections.Count(id => id == s.Id), s.Capacity }).ToList();
            var stageCounts = user.Has(Perm.Admissions) ? (await db.Applications.Where(a => a.CampusId == campusId).Select(a => a.Stage).ToListAsync()).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count()) : null;
            return Results.Ok(new
            {
                attendanceTrend,
                classSizes,
                pipeline = stageCounts is null ? null : Admissions.Stages.All.Select(st => new { stage = st, count = stageCounts.GetValueOrDefault(st) }),
                year = year?.Name,
                students = enrolled,
                staff = await db.Staff.CountAsync(s => s.CampusId == campusId),
                sections = sectionIds.Count,
                applicationsOpen = user.Has(Perm.Admissions) ? await db.Applications.CountAsync(a => a.CampusId == campusId && a.Stage != "enrolled" && a.Stage != "rejected") : (int?)null,
                attendanceMarked = attendance.Count,
                absentToday = attendance.Count(a => a.Category is "authorized" or "unauthorized"),
                lateToday = attendance.Count(a => a.Category == "late"),
                fees = canFees ? new { invoiced = invoices.Sum(i => i.Total), collected = invoices.Sum(i => i.PaidTotal), outstanding = invoices.Sum(i => i.Total - i.PaidTotal), overdue = invoices.Where(i => i.DueDate < today).Sum(i => i.Total - i.PaidTotal) } : null,
                currency = campus.Currency
            });
        }).RequireStaff();
    }

    private static string? Validate(Role r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Role name is required.";
        if (!Scopes.All.Contains(r.Scope)) return "Unknown scope.";
        var perms = r.Permissions.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return perms.FirstOrDefault(p => !Perm.All.Contains(p)) is { } bad ? $"Unknown permission '{bad}'." : null;
    }
}
