using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Attendance;

public static class AttendanceEndpoints
{
    public record Mark(Guid EnrollmentId, string Code, string? Remarks);
    /// <summary>MarkedAt lets a device that was offline report when the register was actually taken.</summary>
    public record SheetRequest(Guid SectionId, DateOnly Date, Guid? PeriodId, List<Mark> Marks, DateTime? MarkedAt);
    public record LeaveCreate(DateOnly FromDate, DateOnly ToDate, string Reason, bool Unpaid = false);
    public record LeaveDecision(bool Approve);

    private static async Task<bool> CanAccessSection(AppDbContext db, CurrentUser user, Guid sectionId) =>
        user.Scope != Scopes.Assigned || (await StudentEndpoints.AssignedSectionIds(db, user)).Contains(sectionId);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapCrud<AbsenceCode>("/api/absence-codes", null, Perm.AdminSettings);

        var g = app.MapGroup("/api/attendance");

        g.MapGet("sheet", async (Guid sectionId, DateOnly date, Guid? periodId, AppDbContext db, CurrentUser user) =>
        {
            if (!await CanAccessSection(db, user, sectionId)) return EndpointExtensions.Forbidden("You are not assigned to this section.");
            var enrollments = await db.Enrollments.Where(e => e.SectionId == sectionId && e.Status == "active").OrderBy(e => e.RollNumber).ToListAsync();
            var studentIds = enrollments.Select(e => e.StudentId).ToList();
            var students = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
            var records = await db.AttendanceRecords.Where(a => a.SectionId == sectionId && a.Date == date && a.PeriodId == periodId).ToListAsync();
            return Results.Ok(enrollments.Select(e =>
            {
                var r = records.FirstOrDefault(x => x.EnrollmentId == e.Id);
                return new { enrollmentId = e.Id, e.StudentId, e.RollNumber, name = students[e.StudentId].FullName(), code = r?.Code, remarks = r?.Remarks, markedAt = r?.MarkedAt };
            }));
        }).Require(Perm.AttendanceMark, Perm.AttendanceRead);

        // Idempotent upsert, so an offline device can safely re-send the same register when it reconnects.
        g.MapPost("sheet", async (SheetRequest req, AppDbContext db, Notifier notifier, CurrentUser user) =>
        {
            if (!await CanAccessSection(db, user, req.SectionId)) return EndpointExtensions.Forbidden("You are not assigned to this section.");
            var section = await db.Sections.FirstOrDefaultAsync(s => s.Id == req.SectionId);
            if (section is null) return Results.NotFound();
            var year = await db.AcademicYears.FirstAsync(y => y.Id == section.AcademicYearId);
            if (year.Status == "archived") return EndpointExtensions.Bad("This academic year is archived.");
            var campus = await db.Campuses.FirstAsync(c => c.Id == year.CampusId);
            if (req.Date > Clock.Today(campus.TimeZone)) return EndpointExtensions.Bad("Attendance cannot be marked for a future date.");
            var codes = await db.AbsenceCodes.Where(c => c.CampusId == year.CampusId).ToDictionaryAsync(c => c.Code);
            var enrollments = await db.Enrollments.Where(e => e.SectionId == req.SectionId && e.Status == "active").ToDictionaryAsync(e => e.Id);
            var existing = await db.AttendanceRecords.Where(a => a.SectionId == req.SectionId && a.Date == req.Date && a.PeriodId == req.PeriodId).ToListAsync();
            var alertFor = new List<(Guid student, AbsenceCode code)>();
            foreach (var m in req.Marks)
            {
                if (!enrollments.TryGetValue(m.EnrollmentId, out var e)) return EndpointExtensions.Bad("A student in this register is not enrolled in the section.");
                if (!codes.TryGetValue(m.Code, out var code)) return EndpointExtensions.Bad($"Unknown attendance code '{m.Code}'.");
                var r = existing.FirstOrDefault(x => x.EnrollmentId == m.EnrollmentId);
                if (r is null) db.Add(r = new AttendanceRecord { EnrollmentId = e.Id, StudentId = e.StudentId, SectionId = req.SectionId, Date = req.Date, PeriodId = req.PeriodId });
                r.Code = m.Code; r.Category = code.Category; r.Remarks = m.Remarks; r.MarkedById = user.UserId; r.MarkedAt = req.MarkedAt?.ToUniversalTime() ?? DateTime.UtcNow;
                // Alert once per absence; a correction back to present does not re-alert.
                if (code.NotifyParents && !r.AlertSent) { alertFor.Add((e.StudentId, code)); r.AlertSent = true; }
            }
            var alertIds = alertFor.Select(a => a.student).ToList();
            var names = await db.Students.Where(s => alertIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FirstName);
            foreach (var (student, code) in alertFor)
                await notifier.ToGuardiansOf([student], "Attendance alert", $"{names.GetValueOrDefault(student)} was marked {code.Name.ToLowerInvariant()} on {req.Date:yyyy-MM-dd}.", "attendance", "inapp", "sms", "email");
            await db.SaveChangesAsync();
            return Results.Ok(new { saved = req.Marks.Count, alerts = alertFor.Count });
        }).Require(Perm.AttendanceMark);

        g.MapGet("summary", async (Guid sectionId, DateOnly from, DateOnly to, AppDbContext db, CurrentUser user) =>
        {
            if (!await CanAccessSection(db, user, sectionId)) return EndpointExtensions.Forbidden();
            var enrollments = await db.Enrollments.Where(e => e.SectionId == sectionId).OrderBy(e => e.RollNumber).ToListAsync();
            var studentIds = enrollments.Select(e => e.StudentId).ToList();
            var students = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
            var records = await db.AttendanceRecords.Where(a => a.SectionId == sectionId && a.Date >= from && a.Date <= to).ToListAsync();
            return Results.Ok(enrollments.Select(e => Summarise(records.Where(r => r.EnrollmentId == e.Id), students[e.StudentId].FullName(), e.RollNumber)));
        }).Require(Perm.AttendanceRead, Perm.AttendanceMark);

        var leave = app.MapGroup("/api/leave-requests");
        leave.MapGet("", async (AppDbContext db, CurrentUser user) =>
        {
            var q = db.LeaveRequests.AsQueryable();
            if (!user.Has(Perm.LeaveApprove)) q = q.Where(l => l.StaffId == user.StaffId);
            var list = await q.OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync();
            var staff = await db.Staff.ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName);
            return list.Select(l => new { l.Id, l.StaffId, staff = staff.GetValueOrDefault(l.StaffId), l.FromDate, l.ToDate, l.Reason, l.Unpaid, l.Status, l.DecidedByName, l.DecidedAt });
        }).RequireStaff();
        leave.MapPost("", async (LeaveCreate req, AppDbContext db, CurrentUser user) =>
        {
            if (user.StaffId is not { } staffId) return EndpointExtensions.Bad("Your login is not linked to a staff record.");
            if (req.ToDate < req.FromDate) return EndpointExtensions.Bad("The end date is before the start date.");
            var l = new LeaveRequest { StaffId = staffId, FromDate = req.FromDate, ToDate = req.ToDate, Reason = req.Reason, Unpaid = req.Unpaid };
            db.Add(l);
            await db.SaveChangesAsync();
            return Results.Ok(l);
        }).RequireStaff();
        leave.MapPost("{id:guid}/decision", async (Guid id, LeaveDecision req, AppDbContext db, Notifier notifier, CurrentUser user) =>
        {
            var l = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            if (l.Status != "pending") return EndpointExtensions.Bad("This request has already been decided.");
            l.Status = req.Approve ? "approved" : "rejected"; l.DecidedByName = user.Name; l.DecidedAt = DateTime.UtcNow;
            if (await db.Users.FirstOrDefaultAsync(u => u.StaffId == l.StaffId) is { } u) notifier.ToUser(u.Id, $"Leave {l.Status}", $"Your leave for {l.FromDate:yyyy-MM-dd} to {l.ToDate:yyyy-MM-dd} was {l.Status}.", "leave");
            await db.SaveChangesAsync();
            return Results.Ok(l);
        }).Require(Perm.LeaveApprove);
    }

    public static object Summarise(IEnumerable<AttendanceRecord> records, string name, int roll)
    {
        var list = records.ToList();
        int Count(string c) => list.Count(r => r.Category == c);
        var total = list.Count;
        var attended = Count("present") + Count("late");
        return new { name, rollNumber = roll, total, present = Count("present"), late = Count("late"), authorized = Count("authorized"), unauthorized = Count("unauthorized"), percent = total == 0 ? (decimal?)null : Math.Round(100m * attended / total, 1) };
    }
}
