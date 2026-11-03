using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Academics;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Rollover;

public record RolloverRequest(Guid FromYearId, string Name, DateOnly StartDate, DateOnly EndDate);
public record PromotionLine(string FromGrade, string? ToGrade, int Students);
public record RolloverPreview(string FromYear, int Sections, int ClassSubjects, int FeeStructures, int GradingSchemes, List<PromotionLine> Promotions, int Graduating,
    decimal OutstandingBalance, int StudentsWithBalance, List<string> Blockers, List<string> Warnings);
public record RolloverResult(Guid NewYearId, int Promoted, int Graduated, int SectionsCreated, int BalancesCarried, decimal AmountCarried);

/// <summary>Year-end rollover (FR-YE-01): promote students, clone configuration, carry balances forward, archive the old year.</summary>
public class RolloverService(AppDbContext db)
{
    public async Task<RolloverPreview?> Preview(Guid fromYearId)
    {
        var year = await db.AcademicYears.FirstOrDefaultAsync(y => y.Id == fromYearId);
        if (year is null) return null;
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (year.Status != "active") blockers.Add($"{year.Name} is {year.Status}; only the active year can be rolled over.");
        var grades = await db.GradeLevels.Where(g => g.CampusId == year.CampusId).OrderBy(g => g.Order).ToListAsync();
        var sections = await db.Sections.Where(s => s.AcademicYearId == fromYearId).ToListAsync();
        var enrollments = await db.Enrollments.Where(e => e.AcademicYearId == fromYearId && e.Status == "active").ToListAsync();
        var promotions = new List<PromotionLine>();
        var graduating = 0;
        foreach (var g in grades)
        {
            var count = enrollments.Count(e => sections.Any(s => s.Id == e.SectionId && s.GradeLevelId == g.Id));
            if (count == 0) continue;
            var next = grades.FirstOrDefault(x => x.Order > g.Order);
            promotions.Add(new(g.Name, next?.Name, count));
            if (next is null) graduating += count;
        }
        var pending = await db.AssessmentPlans.CountAsync(p => p.AcademicYearId == fromYearId && p.Status != PlanStatus.Published);
        if (pending > 0) warnings.Add($"{pending} assessment plan(s) are not published. They will be archived as they are.");
        var open = await db.Invoices.Where(i => i.AcademicYearId == fromYearId && (i.Status == "open" || i.Status == "partial")).ToListAsync();
        return new(year.Name, sections.Count, await db.ClassSubjects.CountAsync(c => c.AcademicYearId == fromYearId), await db.FeeStructures.CountAsync(f => f.AcademicYearId == fromYearId),
            await db.GradingSchemes.CountAsync(s => s.AcademicYearId == fromYearId), promotions, graduating, open.Sum(i => i.Total - i.PaidTotal), open.Select(i => i.StudentId).Distinct().Count(), blockers, warnings);
    }

    public async Task<(RolloverResult? result, string? error)> Run(RolloverRequest req)
    {
        var from = await db.AcademicYears.FirstOrDefaultAsync(y => y.Id == req.FromYearId);
        if (from is null) return (null, "Academic year not found.");
        if (from.Status != "active") return (null, "Only the active year can be rolled over.");
        if (string.IsNullOrWhiteSpace(req.Name) || req.EndDate <= req.StartDate) return (null, "Give the new year a name and valid dates.");
        if (req.StartDate <= from.EndDate) return (null, "The new year must start after the current year ends.");
        if (await db.AcademicYears.AnyAsync(y => y.CampusId == from.CampusId && y.Name == req.Name)) return (null, "A year with that name already exists.");

        // Everything below is saved by one SaveChanges call, so the rollover is all-or-nothing.
        var to = new AcademicYear { CampusId = from.CampusId, Name = req.Name.Trim(), StartDate = req.StartDate, EndDate = req.EndDate, Status = "active" };
        db.Add(to);
        var shift = req.StartDate.DayNumber - from.StartDate.DayNumber;
        DateOnly Shift(DateOnly d) => d.AddDays(shift);

        foreach (var t in await db.Terms.Where(t => t.AcademicYearId == from.Id).ToListAsync())
            db.Add(new Term { AcademicYearId = to.Id, Name = t.Name, Order = t.Order, StartDate = Shift(t.StartDate), EndDate = Shift(t.EndDate) });

        // Clone sections, subject mapping and timetable.
        var oldSections = await db.Sections.Where(s => s.AcademicYearId == from.Id).ToListAsync();
        var sectionMap = new Dictionary<Guid, Section>();
        foreach (var s in oldSections)
        {
            var copy = new Section { AcademicYearId = to.Id, GradeLevelId = s.GradeLevelId, Name = s.Name, Capacity = s.Capacity, ClassTeacherId = s.ClassTeacherId };
            db.Add(copy); sectionMap[s.Id] = copy;
        }
        var csMap = new Dictionary<Guid, Guid>();
        foreach (var c in await db.ClassSubjects.Where(c => c.AcademicYearId == from.Id).ToListAsync())
        {
            if (!sectionMap.TryGetValue(c.SectionId, out var sec)) continue;
            var copy = new ClassSubject { AcademicYearId = to.Id, SectionId = sec.Id, SubjectId = c.SubjectId, TeacherId = c.TeacherId };
            db.Add(copy); csMap[c.Id] = copy.Id;
        }
        foreach (var slot in await db.TimetableSlots.Where(s => s.AcademicYearId == from.Id).ToListAsync())
            if (sectionMap.TryGetValue(slot.SectionId, out var sec) && csMap.TryGetValue(slot.ClassSubjectId, out var cs))
                db.Add(new TimetableSlot { AcademicYearId = to.Id, SectionId = sec.Id, ClassSubjectId = cs, PeriodId = slot.PeriodId, Day = slot.Day, TeacherId = slot.TeacherId, Room = slot.Room });

        // Clone the latest version of each grading scheme and the fee structures.
        foreach (var grp in (await db.GradingSchemes.Where(s => s.AcademicYearId == from.Id).ToListAsync()).GroupBy(s => s.Name))
        {
            var s = grp.OrderByDescending(x => x.Version).First();
            db.Add(new GradingScheme { CampusId = s.CampusId, AcademicYearId = to.Id, Name = s.Name, ScaleType = s.ScaleType, Version = 1, DefinitionJson = s.DefinitionJson });
        }
        var oldStructures = await db.FeeStructures.Where(f => f.AcademicYearId == from.Id).ToListAsync();
        var structureIds = oldStructures.Select(f => f.Id).ToList();
        var oldItems = await db.FeeStructureItems.Where(i => structureIds.Contains(i.FeeStructureId)).ToListAsync();
        var oldInstallments = await db.Installments.Where(i => structureIds.Contains(i.FeeStructureId)).ToListAsync();
        foreach (var f in oldStructures)
        {
            var copy = new FeeStructure { AcademicYearId = to.Id, GradeLevelId = f.GradeLevelId, Name = f.Name, Category = f.Category, Currency = f.Currency };
            db.Add(copy);
            foreach (var i in oldItems.Where(i => i.FeeStructureId == f.Id)) db.Add(new FeeStructureItem { FeeStructureId = copy.Id, FeeHeadId = i.FeeHeadId, Amount = i.Amount });
            foreach (var i in oldInstallments.Where(i => i.FeeStructureId == f.Id)) db.Add(new Installment { FeeStructureId = copy.Id, Name = i.Name, Percent = i.Percent, Order = i.Order, DueDate = Shift(i.DueDate) });
        }

        // Promote each active student to the next grade; the top grade graduates.
        var grades = await db.GradeLevels.Where(g => g.CampusId == from.CampusId).OrderBy(g => g.Order).ToListAsync();
        var enrollments = await db.Enrollments.Where(e => e.AcademicYearId == from.Id && e.Status == "active").ToListAsync();
        var studentIds = enrollments.Select(e => e.StudentId).ToList();
        var studentMap = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var rolls = new Dictionary<Guid, int>();
        int promoted = 0, graduated = 0, sectionsCreated = sectionMap.Count;
        foreach (var e in enrollments.OrderBy(e => studentMap[e.StudentId].FirstName).ThenBy(e => studentMap[e.StudentId].LastName))
        {
            var oldSection = oldSections.First(s => s.Id == e.SectionId);
            var grade = grades.First(g => g.Id == oldSection.GradeLevelId);
            var next = grades.FirstOrDefault(g => g.Order > grade.Order);
            e.EndDate = from.EndDate;
            if (next is null)
            {
                e.Status = "completed";
                var s = studentMap[e.StudentId];
                s.Status = "graduated"; s.LeftOn = from.EndDate; s.LeavingReason = "Completed final grade";
                graduated++;
                continue;
            }
            // Keep the class together: same section letter in the next grade, creating it if that grade had none.
            var target = sectionMap.Values.FirstOrDefault(s => s.GradeLevelId == next.Id && s.Name == oldSection.Name) ?? sectionMap.Values.FirstOrDefault(s => s.GradeLevelId == next.Id);
            if (target is null)
            {
                target = new Section { AcademicYearId = to.Id, GradeLevelId = next.Id, Name = oldSection.Name, Capacity = oldSection.Capacity };
                db.Add(target); sectionMap[Guid.NewGuid()] = target; sectionsCreated++;
            }
            e.Status = "promoted";
            rolls[target.Id] = rolls.GetValueOrDefault(target.Id) + 1;
            db.Add(new Enrollment { StudentId = e.StudentId, SectionId = target.Id, AcademicYearId = to.Id, RollNumber = rolls[target.Id], StartDate = req.StartDate });
            promoted++;
        }

        // Carry unpaid balances forward as one opening invoice per student. Receivables in the ledger are unchanged.
        var open = await db.Invoices.Where(i => i.AcademicYearId == from.Id && (i.Status == "open" || i.Status == "partial")).ToListAsync();
        var seq = await db.Invoices.IgnoreQueryFilters().CountAsync(i => i.TenantId == db.CurrentTenantId);
        int carried = 0; decimal amount = 0;
        foreach (var grp in open.GroupBy(i => i.StudentId))
        {
            var balance = grp.Sum(i => i.Total - i.PaidTotal);
            if (balance <= 0) continue;
            var first = grp.First();
            var invoice = new Invoice
            {
                FeeAccountId = first.FeeAccountId, StudentId = grp.Key, CampusId = from.CampusId, AcademicYearId = to.Id, Number = $"INV-{DateTime.UtcNow.Year}-{++seq:D5}",
                Title = $"Balance brought forward from {from.Name}", IssueDate = req.StartDate, DueDate = req.StartDate, Currency = first.Currency, Subtotal = balance
            };
            FeeCalculator.Recalculate(invoice);
            db.Add(invoice);
            db.Add(new InvoiceLine { InvoiceId = invoice.Id, Kind = "opening", Description = $"Unpaid balance from {string.Join(", ", grp.Select(i => i.Number))}", Amount = balance });
            foreach (var i in grp) i.Status = "carried";
            carried++; amount += balance;
        }

        from.Status = "archived";
        db.Audit("rollover", "AcademicYear", from.Id.ToString(), new { to = to.Name, promoted, graduated, carried, amount });
        await db.SaveChangesAsync();
        return (new RolloverResult(to.Id, promoted, graduated, sectionsCreated, carried, amount), null);
    }
}

public static class RolloverEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/rollover").Require(Perm.Rollover);
        g.MapGet("preview", async (Guid fromYearId, RolloverService svc) => await svc.Preview(fromYearId) is { } p ? Results.Ok(p) : Results.NotFound());
        g.MapPost("run", async (RolloverRequest req, RolloverService svc) =>
        {
            var (result, error) = await svc.Run(req);
            return error is null ? Results.Ok(result) : EndpointExtensions.Bad(error);
        });
    }
}
