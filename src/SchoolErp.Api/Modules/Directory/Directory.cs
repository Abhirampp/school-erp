using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Academics;
using SchoolErp.Api.Modules.Attendance;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Directory;

/// <summary>Staff (teacher) records and profiles, and class (section) details.</summary>
public static class DirectoryEndpoints
{
    public record StaffRequest(Guid CampusId, string EmployeeNumber, string FirstName, string LastName, string? NativeName, string? Email, string? Phone,
        string? Designation, string? Department, string? Qualification, DateOnly? JoinedOn, string? Status);

    /// <summary>Everything a staff list may show. Bank and tax details are deliberately absent.</summary>
    private static object Public(Staff s) => new
    {
        s.Id, s.CampusId, s.EmployeeNumber, s.FirstName, s.LastName, s.NativeName, s.Email, s.Phone, s.Designation, s.Department, s.Qualification, s.JoinedOn, s.Status
    };

    private static void Apply(Staff s, StaffRequest r)
    {
        s.CampusId = r.CampusId; s.EmployeeNumber = r.EmployeeNumber.Trim(); s.FirstName = r.FirstName.Trim(); s.LastName = (r.LastName ?? "").Trim(); s.NativeName = r.NativeName;
        s.Email = r.Email?.Trim().ToLowerInvariant(); s.Phone = r.Phone; s.Designation = r.Designation; s.Department = r.Department; s.Qualification = r.Qualification; s.JoinedOn = r.JoinedOn;
        if (r.Status is "active" or "left") s.Status = r.Status;
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        // ---- Staff ----
        var staff = app.MapGroup("/api/staff");

        staff.MapGet("", async (Guid? campusId, AppDbContext db) =>
        {
            var q = db.Staff.AsQueryable();
            if (campusId is { } c) q = q.Where(s => s.CampusId == c);
            return (await q.OrderBy(s => s.FirstName).ThenBy(s => s.LastName).ToListAsync()).Select(Public);
        }).RequireStaff();

        staff.MapPost("", async (StaffRequest req, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(req.CampusId)) return EndpointExtensions.Forbidden();
            if (string.IsNullOrWhiteSpace(req.EmployeeNumber) || string.IsNullOrWhiteSpace(req.FirstName)) return EndpointExtensions.Bad("Employee number and first name are required.");
            if (await db.Staff.AnyAsync(s => s.EmployeeNumber == req.EmployeeNumber.Trim())) return EndpointExtensions.Bad("That employee number is already used.");
            var s = new Staff();
            Apply(s, req);
            db.Add(s);
            await db.SaveChangesAsync();
            return Results.Ok(Public(s));
        }).Require(Perm.AdminUsers);

        staff.MapPut("{id:guid}", async (Guid id, StaffRequest req, AppDbContext db) =>
        {
            var s = await db.Staff.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            if (await db.Staff.AnyAsync(x => x.Id != id && x.EmployeeNumber == req.EmployeeNumber.Trim())) return EndpointExtensions.Bad("That employee number is already used.");
            Apply(s, req);
            await db.SaveChangesAsync();
            return Results.Ok(Public(s));
        }).Require(Perm.AdminUsers);

        staff.MapDelete("{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var s = await db.Staff.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            if (await db.ClassSubjects.AnyAsync(c => c.TeacherId == id) || await db.Sections.AnyAsync(x => x.ClassTeacherId == id))
                return EndpointExtensions.Bad("This person still teaches or is a class teacher. Reassign their classes, or mark them as left instead.");
            db.Remove(s);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Require(Perm.AdminUsers);

        // Directory with teaching load, for the staff list screen.
        staff.MapGet("directory", async (Guid campusId, Guid? academicYearId, AppDbContext db) =>
        {
            var list = await db.Staff.Where(s => s.CampusId == campusId).OrderBy(s => s.FirstName).ThenBy(s => s.LastName).ToListAsync();
            var names = await AcademicsEndpoints.SectionNames(db);
            var sections = academicYearId is { } y ? await db.Sections.Where(s => s.AcademicYearId == y).ToListAsync() : [];
            var cs = academicYearId is { } y2 ? await db.ClassSubjects.Where(c => c.AcademicYearId == y2).ToListAsync() : [];
            var slots = academicYearId is { } y3 ? await db.TimetableSlots.Where(t => t.AcademicYearId == y3).Select(t => t.TeacherId).ToListAsync() : [];
            var subjects = await db.Subjects.ToDictionaryAsync(x => x.Id, x => x.Name);
            var logins = (await db.Users.Where(u => u.StaffId != null).Select(u => u.StaffId).ToListAsync()).ToHashSet();
            return list.Select(s => new
            {
                staff = Public(s),
                classTeacherOf = sections.Where(x => x.ClassTeacherId == s.Id).Select(x => names.GetValueOrDefault(x.Id)).ToList(),
                subjects = cs.Where(c => c.TeacherId == s.Id).Select(c => subjects.GetValueOrDefault(c.SubjectId)).Distinct().ToList(),
                classes = cs.Count(c => c.TeacherId == s.Id),
                periodsPerWeek = slots.Count(t => t == s.Id),
                hasLogin = logins.Contains(s.Id)
            });
        }).RequireStaff();

        staff.MapGet("{id:guid}/profile", async (Guid id, Guid? academicYearId, AppDbContext db, CurrentUser user) =>
        {
            var s = await db.Staff.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var names = await AcademicsEndpoints.SectionNames(db);
            var subjects = await db.Subjects.ToDictionaryAsync(x => x.Id, x => x.Name);
            var cs = academicYearId is { } y ? await db.ClassSubjects.Where(c => c.AcademicYearId == y && c.TeacherId == id).ToListAsync() : [];
            var slots = academicYearId is { } y2 ? await db.TimetableSlots.Where(t => t.AcademicYearId == y2 && t.TeacherId == id).ToListAsync() : [];
            var classTeacherOf = academicYearId is { } y3 ? await db.Sections.Where(x => x.AcademicYearId == y3 && x.ClassTeacherId == id).Select(x => x.Id).ToListAsync() : [];
            var login = await db.Users.FirstOrDefaultAsync(u => u.StaffId == id);
            var role = login is null ? null : await db.Roles.FirstOrDefaultAsync(r => r.Id == login.RoleId);
            var canPay = user.Has(Perm.Payroll);
            var self = user.StaffId == id;
            return Results.Ok(new
            {
                staff = Public(s),
                bank = canPay ? new { s.BankName, s.BankAccount, s.TaxId } : null,
                classTeacherOf = classTeacherOf.Select(x => new { id = x, name = names.GetValueOrDefault(x) }),
                teaching = cs.Select(c => new { classSubjectId = c.Id, sectionId = c.SectionId, section = names.GetValueOrDefault(c.SectionId), subject = subjects.GetValueOrDefault(c.SubjectId), periods = slots.Count(t => t.ClassSubjectId == c.Id) })
                    .OrderBy(x => x.section).ToList(),
                periodsPerWeek = slots.Count,
                login = login is null ? null : new { login.Email, role = role?.Name, login.LastLoginAt, login.MfaEnabled, login.IsActive },
                leave = (self || user.Has(Perm.LeaveApprove) || canPay)
                    ? (await db.LeaveRequests.Where(l => l.StaffId == id).OrderByDescending(l => l.FromDate).Take(20).ToListAsync()).Select(l => new { l.Id, l.FromDate, l.ToDate, l.Reason, l.Unpaid, l.Status })
                    : null,
                payslips = canPay
                    ? (await db.Payslips.Where(p => p.StaffId == id).OrderByDescending(p => p.Month).Take(12).ToListAsync()).Select(p => new { p.Id, p.Month, p.Gross, p.Deductions, p.Net })
                    : null
            });
        }).RequireStaff();

        // ---- Classes (sections) ----
        app.MapGet("/api/classes", async (Guid academicYearId, AppDbContext db) =>
        {
            var sections = await db.Sections.Where(s => s.AcademicYearId == academicYearId).ToListAsync();
            var ids = sections.Select(s => s.Id).ToList();
            var grades = await db.GradeLevels.ToDictionaryAsync(g => g.Id);
            var staff = await db.Staff.ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName);
            var enrolled = await db.Enrollments.Where(e => ids.Contains(e.SectionId) && e.Status == "active").Select(e => e.SectionId).ToListAsync();
            var subjects = await db.ClassSubjects.Where(c => ids.Contains(c.SectionId)).Select(c => c.SectionId).ToListAsync();
            var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
            var attendance = await db.AttendanceRecords.Where(a => ids.Contains(a.SectionId) && a.Date >= since).Select(a => new { a.SectionId, a.Category }).ToListAsync();
            return sections.OrderBy(s => grades.TryGetValue(s.GradeLevelId, out var g) ? g.Order : 0).ThenBy(s => s.Name).Select(s =>
            {
                var att = attendance.Where(a => a.SectionId == s.Id).ToList();
                return new
                {
                    s.Id, s.Name, s.Capacity, s.GradeLevelId, grade = grades.TryGetValue(s.GradeLevelId, out var g) ? g.Name : "",
                    s.ClassTeacherId, classTeacher = s.ClassTeacherId is { } t ? staff.GetValueOrDefault(t) : null,
                    enrolled = enrolled.Count(x => x == s.Id), subjects = subjects.Count(x => x == s.Id),
                    attendancePercent = att.Count == 0 ? (decimal?)null : Math.Round(100m * att.Count(a => a.Category is "present" or "late") / att.Count, 1)
                };
            });
        }).RequireStaff();

        app.MapGet("/api/classes/{id:guid}", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var s = await db.Sections.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var grade = await db.GradeLevels.FirstOrDefaultAsync(g => g.Id == s.GradeLevelId);
            var year = await db.AcademicYears.FirstAsync(y => y.Id == s.AcademicYearId);
            var staff = await db.Staff.ToDictionaryAsync(x => x.Id, x => x.FirstName + " " + x.LastName);
            var subjects = await db.Subjects.ToDictionaryAsync(x => x.Id, x => x.Name);
            var cs = await db.ClassSubjects.Where(c => c.SectionId == id).ToListAsync();
            var slots = await db.TimetableSlots.Where(t => t.SectionId == id).Select(t => t.ClassSubjectId).ToListAsync();
            var enrollments = await db.Enrollments.Where(e => e.SectionId == id && e.Status == "active").OrderBy(e => e.RollNumber).ToListAsync();
            var studentIds = enrollments.Select(e => e.StudentId).ToList();
            var students = user.Has(Perm.StudentsRead) ? await db.Students.Where(x => studentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id) : [];
            var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
            var att = await db.AttendanceRecords.Where(a => a.SectionId == id && a.Date >= since).ToListAsync();
            var plans = await db.AssessmentPlans.Where(p => p.SectionId == id).Select(p => p.Status).ToListAsync();
            return Results.Ok(new
            {
                section = new { s.Id, s.Name, s.Capacity, s.AcademicYearId, s.GradeLevelId, grade = grade?.Name, year = year.Name, classTeacherId = s.ClassTeacherId, classTeacher = s.ClassTeacherId is { } t ? staff.GetValueOrDefault(t) : null },
                students = enrollments.Where(e => students.ContainsKey(e.StudentId)).Select(e =>
                {
                    var st = students[e.StudentId];
                    var mine = att.Where(a => a.EnrollmentId == e.Id).ToList();
                    return new { st.Id, e.RollNumber, name = st.FullName(), st.AdmissionNumber, st.Gender, st.DateOfBirth, attendancePercent = mine.Count == 0 ? (decimal?)null : Math.Round(100m * mine.Count(a => a.Category is "present" or "late") / mine.Count, 1) };
                }),
                enrolled = enrollments.Count,
                subjects = cs.Select(c => new { c.Id, subject = subjects.GetValueOrDefault(c.SubjectId), teacherId = c.TeacherId, teacher = c.TeacherId is { } t ? staff.GetValueOrDefault(t) : null, periods = slots.Count(x => x == c.Id) })
                    .OrderBy(x => x.subject),
                attendance = AttendanceEndpoints.Summarise(att, "", 0),
                results = new { plans = plans.Count, published = plans.Count(p => p == "published"), awaitingApproval = plans.Count(p => p == "submitted") },
                gender = new { female = students.Values.Count(x => x.Gender == "F"), male = students.Values.Count(x => x.Gender == "M") }
            });
        }).RequireStaff();
    }
}
