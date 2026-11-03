using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Academics;

public record Clash(string Kind, string Message, Guid SlotId);

/// <summary>Pure clash detection: a teacher or a room cannot be in two places in the same day and period.</summary>
public static class ClashDetector
{
    public static List<Clash> Find(TimetableSlot candidate, IEnumerable<TimetableSlot> others, Func<Guid, string> sectionName)
    {
        var clashes = new List<Clash>();
        foreach (var o in others.Where(o => o.Id != candidate.Id && o.Day == candidate.Day && o.PeriodId == candidate.PeriodId))
        {
            if (o.SectionId == candidate.SectionId) continue; // same cell: replaced, not a clash
            if (candidate.TeacherId is not null && o.TeacherId == candidate.TeacherId)
                clashes.Add(new("teacher", $"The teacher is already teaching {sectionName(o.SectionId)} in this period.", o.Id));
            if (!string.IsNullOrWhiteSpace(candidate.Room) && string.Equals(o.Room?.Trim(), candidate.Room.Trim(), StringComparison.OrdinalIgnoreCase))
                clashes.Add(new("room", $"Room {candidate.Room} is already used by {sectionName(o.SectionId)} in this period.", o.Id));
        }
        return clashes;
    }
}

public static class AcademicsEndpoints
{
    public record SlotRequest(Guid SectionId, Guid ClassSubjectId, Guid PeriodId, int Day, string? Room);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapCrud<ClassSubject>("/api/class-subjects", null, Perm.Academics);
        app.MapCrud<Period>("/api/periods", null, Perm.Academics);
        app.MapCrud<Substitution>("/api/substitutions", null, Perm.Academics);

        var g = app.MapGroup("/api/timetable");

        // Weekly grid for a section, or for a teacher across sections.
        g.MapGet("", async (Guid academicYearId, Guid? sectionId, Guid? teacherId, AppDbContext db) =>
        {
            var q = db.TimetableSlots.Where(s => s.AcademicYearId == academicYearId);
            if (sectionId is { } sec) q = q.Where(s => s.SectionId == sec);
            if (teacherId is { } t) q = q.Where(s => s.TeacherId == t);
            return await Describe(db, await q.ToListAsync());
        }).Require(Perm.TimetableRead, Perm.Academics);

        g.MapPut("", async (SlotRequest req, AppDbContext db) =>
        {
            var cs = await db.ClassSubjects.FirstOrDefaultAsync(c => c.Id == req.ClassSubjectId && c.SectionId == req.SectionId);
            if (cs is null) return EndpointExtensions.Bad("That subject is not mapped to this section.");
            if (req.Day is < 1 or > 7) return EndpointExtensions.Bad("Day must be 1 (Monday) to 7 (Sunday).");
            var all = await db.TimetableSlots.Where(s => s.AcademicYearId == cs.AcademicYearId && s.Day == req.Day && s.PeriodId == req.PeriodId).ToListAsync();
            var slot = all.FirstOrDefault(s => s.SectionId == req.SectionId) ?? new TimetableSlot { AcademicYearId = cs.AcademicYearId, SectionId = req.SectionId, PeriodId = req.PeriodId, Day = req.Day };
            slot.ClassSubjectId = cs.Id; slot.TeacherId = cs.TeacherId; slot.Room = string.IsNullOrWhiteSpace(req.Room) ? null : req.Room.Trim();
            var names = await SectionNames(db);
            var clashes = ClashDetector.Find(slot, all, id => names.GetValueOrDefault(id, "another class"));
            if (clashes.Count > 0) return Results.Json(new { title = "Timetable clash", detail = string.Join(" ", clashes.Select(c => c.Message)), clashes }, statusCode: 409);
            if (db.Entry(slot).State == EntityState.Detached) db.Add(slot);
            await db.SaveChangesAsync();
            return Results.Ok(slot);
        }).Require(Perm.Academics);

        g.MapDelete("{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var s = await db.TimetableSlots.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            db.Remove(s);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Require(Perm.Academics);

        // Whole-year audit, e.g. after reassigning a teacher on a class-subject.
        g.MapGet("clashes", async (Guid academicYearId, AppDbContext db) =>
        {
            var all = await db.TimetableSlots.Where(s => s.AcademicYearId == academicYearId).ToListAsync();
            var names = await SectionNames(db);
            return all.SelectMany(s => ClashDetector.Find(s, all, id => names.GetValueOrDefault(id, "another class"))
                .Select(c => new { slotId = s.Id, section = names.GetValueOrDefault(s.SectionId), s.Day, s.PeriodId, c.Kind, c.Message }));
        }).Require(Perm.Academics);
    }

    public static async Task<Dictionary<Guid, string>> SectionNames(AppDbContext db)
    {
        var grades = await db.GradeLevels.ToDictionaryAsync(x => x.Id, x => x.Name);
        return (await db.Sections.ToListAsync()).ToDictionary(s => s.Id, s => $"{grades.GetValueOrDefault(s.GradeLevelId)} {s.Name}");
    }

    public static async Task<IEnumerable<object>> Describe(AppDbContext db, List<TimetableSlot> slots)
    {
        var csIds = slots.Select(s => s.ClassSubjectId).ToList();
        var cs = await db.ClassSubjects.Where(c => csIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        var subjects = await db.Subjects.ToDictionaryAsync(s => s.Id, s => s.Name);
        var staff = await db.Staff.ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName);
        var names = await SectionNames(db);
        return slots.Select(s => new
        {
            s.Id, s.Day, s.PeriodId, s.SectionId, s.ClassSubjectId, s.Room, s.TeacherId,
            section = names.GetValueOrDefault(s.SectionId),
            subject = cs.TryGetValue(s.ClassSubjectId, out var c) ? subjects.GetValueOrDefault(c.SubjectId) : null,
            teacher = s.TeacherId is { } t ? staff.GetValueOrDefault(t) : null
        });
    }
}
