using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Exams;

public record ReportComponent(string Name, decimal? Marks, decimal MaxMarks, decimal Weight, bool Absent);
public record ReportSubject(string Subject, string? Teacher, decimal Credits, List<ReportComponent> Components, decimal? Percent, string? Grade, decimal? Points, string? Descriptor, bool? Passed);
public record ReportCard(Guid EnrollmentId, string School, string Campus, string Curriculum, string Locale, string Year, string Term, string Student, string? NativeName, string AdmissionNumber,
    string Section, int RollNumber, List<ReportSubject> Subjects, decimal? OverallPercent, decimal? Gpa, int AttendanceTotal, int AttendanceAttended);

public class ReportCardService(AppDbContext db)
{
    /// <summary>Builds report cards for the given enrollments. publishedOnly is what parents and students see.</summary>
    public async Task<List<ReportCard>> Build(List<Guid> enrollmentIds, Guid termId, bool publishedOnly)
    {
        var term = await db.Terms.FirstOrDefaultAsync(t => t.Id == termId);
        if (term is null) return [];
        var year = await db.AcademicYears.FirstAsync(y => y.Id == term.AcademicYearId);
        var campus = await db.Campuses.FirstAsync(c => c.Id == year.CampusId);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == db.CurrentTenantId);
        var enrollments = await db.Enrollments.Where(e => enrollmentIds.Contains(e.Id)).OrderBy(e => e.RollNumber).ToListAsync();
        var sectionIds = enrollments.Select(e => e.SectionId).Distinct().ToList();
        var studentIds = enrollments.Select(e => e.StudentId).ToList();
        var students = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var sections = await db.Sections.Where(s => sectionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var grades = await db.GradeLevels.ToDictionaryAsync(x => x.Id, x => x.Name);

        var plansQ = db.AssessmentPlans.Where(p => p.TermId == termId && sectionIds.Contains(p.SectionId));
        plansQ = publishedOnly ? plansQ.Where(p => p.Status == PlanStatus.Published) : plansQ.Where(p => p.Status == PlanStatus.Published || p.Status == PlanStatus.Approved);
        var plans = await plansQ.ToListAsync();
        var planIds = plans.Select(p => p.Id).ToList();
        var assessments = await db.Assessments.Where(a => planIds.Contains(a.AssessmentPlanId)).OrderBy(a => a.Order).ToListAsync();
        var assessmentIds = assessments.Select(a => a.Id).ToList();
        var scores = await db.Scores.Where(s => assessmentIds.Contains(s.AssessmentId) && enrollmentIds.Contains(s.EnrollmentId)).ToListAsync();
        var schemeIds = plans.Select(p => p.GradingSchemeId).Distinct().ToList();
        var schemes = (await db.GradingSchemes.Where(s => schemeIds.Contains(s.Id)).ToListAsync()).ToDictionary(s => s.Id, s => GradingEngine.Parse(s.DefinitionJson));
        var csIds = plans.Select(p => p.ClassSubjectId).ToList();
        var classSubjects = await db.ClassSubjects.Where(c => csIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        var subjects = await db.Subjects.Where(s => s.CampusId == campus.Id).ToDictionaryAsync(s => s.Id);
        var staff = await db.Staff.ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName);
        var attendance = await db.AttendanceRecords.Where(a => enrollmentIds.Contains(a.EnrollmentId) && a.Date >= term.StartDate && a.Date <= term.EndDate).ToListAsync();

        var cards = new List<ReportCard>();
        foreach (var e in enrollments)
        {
            var student = students[e.StudentId];
            var section = sections[e.SectionId];
            var rows = new List<ReportSubject>();
            foreach (var plan in plans.Where(p => p.SectionId == e.SectionId))
            {
                if (!classSubjects.TryGetValue(plan.ClassSubjectId, out var cs) || !subjects.TryGetValue(cs.SubjectId, out var subject) || !schemes.TryGetValue(plan.GradingSchemeId, out var def)) continue;
                var comps = assessments.Where(a => a.AssessmentPlanId == plan.Id).Select(a =>
                {
                    var s = scores.FirstOrDefault(x => x.AssessmentId == a.Id && x.EnrollmentId == e.Id);
                    return (a, s);
                }).ToList();
                var result = GradingEngine.Compute(def, comps.Select(c => new ComponentScore(c.a.Weight, c.a.MaxMarks, c.s?.Marks, c.s?.IsAbsent ?? false, c.s?.IsExcused ?? false)));
                rows.Add(new ReportSubject(subject.Name, cs.TeacherId is { } t ? staff.GetValueOrDefault(t) : null, subject.Credits,
                    comps.Select(c => new ReportComponent(c.a.Name, c.s?.Marks, c.a.MaxMarks, c.a.Weight, c.s?.IsAbsent ?? false)).ToList(),
                    result.Percent, result.Grade, result.Points, result.Descriptor, result.Passed));
            }
            var graded = rows.Where(r => r.Percent is not null).ToList();
            var att = attendance.Where(a => a.EnrollmentId == e.Id).ToList();
            cards.Add(new ReportCard(e.Id, tenant.Name, campus.Name, campus.Curriculum, campus.Locale, year.Name, term.Name, student.FullName(), student.NativeName, student.AdmissionNumber,
                $"{grades.GetValueOrDefault(section.GradeLevelId)} {section.Name}", e.RollNumber, rows.OrderBy(r => r.Subject).ToList(),
                graded.Count == 0 ? null : Math.Round(graded.Average(r => r.Percent!.Value), 1), GradingEngine.Gpa(rows.Select(r => (r.Points, r.Credits))),
                att.Count, att.Count(a => a.Category is "present" or "late")));
        }
        return cards;
    }
}

public static class ExamEndpoints
{
    public record SchemeRequest(Guid CampusId, Guid AcademicYearId, string Name, string ScaleType, SchemeDefinition Definition);
    public record PresetRequest(Guid CampusId, Guid AcademicYearId, string Preset);
    public record ComponentRequest(string Name, decimal Weight, decimal MaxMarks);
    public record PlanRequest(Guid TermId, Guid ClassSubjectId, Guid GradingSchemeId, string Name, DateTime? MarksDeadline, List<ComponentRequest> Components);
    public record ScoreEntry(Guid AssessmentId, Guid EnrollmentId, decimal? Marks, bool IsAbsent, bool IsExcused);
    public record MarksRequest(List<ScoreEntry> Scores);

    private static async Task<bool> SchemeInUse(AppDbContext db, Guid schemeId) =>
        await db.AssessmentPlans.AnyAsync(p => p.GradingSchemeId == schemeId && p.Status != PlanStatus.Open);

    private static async Task<bool> CanEnterMarks(AppDbContext db, CurrentUser user, AssessmentPlan plan) =>
        user.Scope != Scopes.Assigned || (user.StaffId is { } staff && await db.ClassSubjects.AnyAsync(c => c.Id == plan.ClassSubjectId && c.TeacherId == staff));

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/exams");

        // ---- Grading schemes ----
        g.MapGet("grading-schemes", async (Guid academicYearId, AppDbContext db) =>
            (await db.GradingSchemes.Where(s => s.AcademicYearId == academicYearId).OrderBy(s => s.Name).ThenBy(s => s.Version).ToListAsync())
            .Select(s => new { s.Id, s.CampusId, s.AcademicYearId, s.Name, s.ScaleType, s.Version, definition = GradingEngine.Parse(s.DefinitionJson) })).RequireStaff();

        g.MapGet("grading-schemes/presets", () => GradingEngine.Presets.Select(p => new { key = p.Key, p.Value.Name, p.Value.ScaleType, definition = p.Value.Def })).RequireStaff();

        g.MapPost("grading-schemes/from-preset", async (PresetRequest req, AppDbContext db) =>
        {
            if (!GradingEngine.Presets.TryGetValue(req.Preset, out var p)) return EndpointExtensions.Bad("Unknown preset.");
            var s = new GradingScheme { CampusId = req.CampusId, AcademicYearId = req.AcademicYearId, Name = p.Name, ScaleType = p.ScaleType, DefinitionJson = GradingEngine.Serialize(p.Def) };
            db.Add(s);
            await db.SaveChangesAsync();
            return Results.Ok(s);
        }).Require(Perm.ExamsManage);

        g.MapPost("grading-schemes", async (SchemeRequest req, AppDbContext db) =>
        {
            var errors = GradingEngine.Validate(req.Definition);
            if (errors.Count > 0) return EndpointExtensions.Bad(string.Join(" ", errors));
            var s = new GradingScheme { CampusId = req.CampusId, AcademicYearId = req.AcademicYearId, Name = req.Name, ScaleType = req.ScaleType, DefinitionJson = GradingEngine.Serialize(req.Definition) };
            db.Add(s);
            await db.SaveChangesAsync();
            return Results.Ok(s);
        }).Require(Perm.ExamsManage);

        // Once results depend on a scheme it is frozen: an edit creates the next version and leaves old results untouched.
        g.MapPut("grading-schemes/{id:guid}", async (Guid id, SchemeRequest req, AppDbContext db) =>
        {
            var s = await db.GradingSchemes.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var errors = GradingEngine.Validate(req.Definition);
            if (errors.Count > 0) return EndpointExtensions.Bad(string.Join(" ", errors));
            if (await SchemeInUse(db, id))
            {
                var next = new GradingScheme { CampusId = s.CampusId, AcademicYearId = s.AcademicYearId, Name = req.Name, ScaleType = req.ScaleType, Version = s.Version + 1, DefinitionJson = GradingEngine.Serialize(req.Definition) };
                db.Add(next);
                await db.SaveChangesAsync();
                return Results.Ok(new { next.Id, next.Version, newVersion = true });
            }
            s.Name = req.Name; s.ScaleType = req.ScaleType; s.DefinitionJson = GradingEngine.Serialize(req.Definition);
            await db.SaveChangesAsync();
            return Results.Ok(new { s.Id, s.Version, newVersion = false });
        }).Require(Perm.ExamsManage);

        // ---- Assessment plans ----
        g.MapGet("plans", async (Guid academicYearId, Guid? sectionId, Guid? termId, AppDbContext db, CurrentUser user) =>
        {
            var q = db.AssessmentPlans.Where(p => p.AcademicYearId == academicYearId);
            if (sectionId is { } s) q = q.Where(p => p.SectionId == s);
            if (termId is { } t) q = q.Where(p => p.TermId == t);
            if (user.Scope == Scopes.Assigned)
            {
                var mine = await db.ClassSubjects.Where(c => c.TeacherId == user.StaffId).Select(c => c.Id).ToListAsync();
                q = q.Where(p => mine.Contains(p.ClassSubjectId));
            }
            var plans = await q.OrderBy(p => p.CreatedAt).ToListAsync();
            var planIds = plans.Select(p => p.Id).ToList();
            var comps = await db.Assessments.Where(a => planIds.Contains(a.AssessmentPlanId)).OrderBy(a => a.Order).ToListAsync();
            var cs = await db.ClassSubjects.ToDictionaryAsync(c => c.Id);
            var subjects = await db.Subjects.ToDictionaryAsync(x => x.Id, x => x.Name);
            var sections = await Academics.AcademicsEndpoints.SectionNames(db);
            var terms = await db.Terms.ToDictionaryAsync(x => x.Id, x => x.Name);
            var schemes = await db.GradingSchemes.ToDictionaryAsync(x => x.Id, x => $"{x.Name} v{x.Version}");
            return plans.Select(p => new
            {
                p.Id, p.Name, p.Status, p.MarksDeadline, p.TermId, p.SectionId, p.ClassSubjectId, p.GradingSchemeId, p.ApprovedByName, p.ApprovedAt, p.PublishedAt,
                term = terms.GetValueOrDefault(p.TermId), section = sections.GetValueOrDefault(p.SectionId), scheme = schemes.GetValueOrDefault(p.GradingSchemeId),
                subject = cs.TryGetValue(p.ClassSubjectId, out var c) ? subjects.GetValueOrDefault(c.SubjectId) : null,
                components = comps.Where(a => a.AssessmentPlanId == p.Id).Select(a => new { a.Id, a.Name, a.Weight, a.MaxMarks })
            });
        }).Require(Perm.ExamsManage, Perm.ExamsMarks, Perm.ExamsApprove);

        g.MapPost("plans", async (PlanRequest req, AppDbContext db) =>
        {
            var cs = await db.ClassSubjects.FirstOrDefaultAsync(c => c.Id == req.ClassSubjectId);
            var term = await db.Terms.FirstOrDefaultAsync(t => t.Id == req.TermId);
            var scheme = await db.GradingSchemes.FirstOrDefaultAsync(s => s.Id == req.GradingSchemeId);
            if (cs is null || term is null || scheme is null) return EndpointExtensions.Bad("Choose a term, class subject and grading scheme.");
            if (term.AcademicYearId != cs.AcademicYearId || scheme.AcademicYearId != cs.AcademicYearId) return EndpointExtensions.Bad("Term, subject and grading scheme must belong to the same academic year.");
            if (req.Components.Count == 0 || req.Components.Any(c => c.Weight <= 0 || c.MaxMarks <= 0 || string.IsNullOrWhiteSpace(c.Name))) return EndpointExtensions.Bad("Each component needs a name, a weight above 0 and maximum marks above 0.");
            if (await db.AssessmentPlans.AnyAsync(p => p.TermId == req.TermId && p.ClassSubjectId == req.ClassSubjectId)) return EndpointExtensions.Bad("This subject already has an assessment plan for the term.");
            var plan = new AssessmentPlan { AcademicYearId = cs.AcademicYearId, TermId = req.TermId, ClassSubjectId = cs.Id, SectionId = cs.SectionId, GradingSchemeId = req.GradingSchemeId, Name = req.Name, MarksDeadline = req.MarksDeadline?.ToUniversalTime() };
            db.Add(plan);
            var order = 0;
            foreach (var c in req.Components) db.Add(new Assessment { AssessmentPlanId = plan.Id, Name = c.Name.Trim(), Weight = c.Weight, MaxMarks = c.MaxMarks, Order = order++ });
            await db.SaveChangesAsync();
            return Results.Ok(new { plan.Id });
        }).Require(Perm.ExamsManage);

        g.MapDelete("plans/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var plan = await db.AssessmentPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan is null) return Results.NotFound();
            if (plan.Status != PlanStatus.Open) return EndpointExtensions.Bad("Only an open plan can be deleted.");
            db.Remove(plan);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Require(Perm.ExamsManage);

        // ---- Marks entry ----
        g.MapGet("plans/{id:guid}/marks", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var plan = await db.AssessmentPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan is null) return Results.NotFound();
            if (!await CanEnterMarks(db, user, plan)) return EndpointExtensions.Forbidden("You do not teach this subject.");
            var comps = await db.Assessments.Where(a => a.AssessmentPlanId == id).OrderBy(a => a.Order).ToListAsync();
            var compIds = comps.Select(c => c.Id).ToList();
            var scores = await db.Scores.Where(s => compIds.Contains(s.AssessmentId)).ToListAsync();
            var enrollments = await db.Enrollments.Where(e => e.SectionId == plan.SectionId && e.Status == "active").OrderBy(e => e.RollNumber).ToListAsync();
            var studentIds = enrollments.Select(e => e.StudentId).ToList();
            var students = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
            var def = GradingEngine.Parse((await db.GradingSchemes.FirstAsync(s => s.Id == plan.GradingSchemeId)).DefinitionJson);
            return Results.Ok(new
            {
                plan = new { plan.Id, plan.Name, plan.Status, plan.MarksDeadline },
                components = comps.Select(c => new { c.Id, c.Name, c.Weight, c.MaxMarks }),
                rows = enrollments.Select(e =>
                {
                    var mine = comps.Select(c => (c, s: scores.FirstOrDefault(x => x.AssessmentId == c.Id && x.EnrollmentId == e.Id))).ToList();
                    return new
                    {
                        enrollmentId = e.Id, e.RollNumber, name = students[e.StudentId].FullName(),
                        scores = mine.Select(m => new { assessmentId = m.c.Id, marks = m.s?.Marks, isAbsent = m.s?.IsAbsent ?? false, isExcused = m.s?.IsExcused ?? false }),
                        result = GradingEngine.Compute(def, mine.Select(m => new ComponentScore(m.c.Weight, m.c.MaxMarks, m.s?.Marks, m.s?.IsAbsent ?? false, m.s?.IsExcused ?? false)))
                    };
                })
            });
        }).Require(Perm.ExamsMarks, Perm.ExamsManage, Perm.ExamsApprove);

        g.MapPut("plans/{id:guid}/marks", async (Guid id, MarksRequest req, AppDbContext db, CurrentUser user) =>
        {
            var plan = await db.AssessmentPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan is null) return Results.NotFound();
            if (!await CanEnterMarks(db, user, plan)) return EndpointExtensions.Forbidden("You do not teach this subject.");
            if (plan.Status != PlanStatus.Open) return EndpointExtensions.Bad("Marks are locked. Ask the principal to reopen the plan.");
            if (plan.MarksDeadline is { } deadline && DateTime.UtcNow > deadline && !user.Has(Perm.ExamsManage)) return EndpointExtensions.Bad("The marks entry deadline has passed.");
            var comps = await db.Assessments.Where(a => a.AssessmentPlanId == id).ToDictionaryAsync(a => a.Id);
            var enrolled = (await db.Enrollments.Where(e => e.SectionId == plan.SectionId).Select(e => e.Id).ToListAsync()).ToHashSet();
            var compIds = comps.Keys.ToList();
            var existing = await db.Scores.Where(s => compIds.Contains(s.AssessmentId)).ToListAsync();
            foreach (var entry in req.Scores)
            {
                if (!comps.TryGetValue(entry.AssessmentId, out var comp) || !enrolled.Contains(entry.EnrollmentId)) return EndpointExtensions.Bad("A mark refers to a student or component outside this plan.");
                if (entry.Marks is { } m && (m < 0 || m > comp.MaxMarks)) return EndpointExtensions.Bad($"{comp.Name}: marks must be between 0 and {comp.MaxMarks}.");
                var s = existing.FirstOrDefault(x => x.AssessmentId == entry.AssessmentId && x.EnrollmentId == entry.EnrollmentId);
                if (s is null) { s = new Score { AssessmentId = entry.AssessmentId, EnrollmentId = entry.EnrollmentId }; db.Add(s); existing.Add(s); }
                s.Marks = entry.IsAbsent || entry.IsExcused ? null : entry.Marks; s.IsAbsent = entry.IsAbsent; s.IsExcused = entry.IsExcused;
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.ExamsMarks, Perm.ExamsManage);

        // ---- Workflow: open -> submitted (locked) -> approved -> published ----
        g.MapPost("plans/{id:guid}/submit", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var plan = await db.AssessmentPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan is null) return Results.NotFound();
            if (!await CanEnterMarks(db, user, plan)) return EndpointExtensions.Forbidden("You do not teach this subject.");
            if (plan.Status != PlanStatus.Open) return EndpointExtensions.Bad("This plan has already been submitted.");
            plan.Status = PlanStatus.Submitted;
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.ExamsMarks, Perm.ExamsManage);

        g.MapPost("plans/{id:guid}/{action:regex(^(approve|reopen|publish)$)}", async (Guid id, string action, AppDbContext db, Notifier notifier, CurrentUser user) =>
        {
            var plan = await db.AssessmentPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan is null) return Results.NotFound();
            switch (action)
            {
                case "approve":
                    if (plan.Status != PlanStatus.Submitted) return EndpointExtensions.Bad("Only submitted marks can be approved.");
                    plan.Status = PlanStatus.Approved; plan.ApprovedByName = user.Name; plan.ApprovedAt = DateTime.UtcNow;
                    break;
                case "reopen":
                    if (plan.Status == PlanStatus.Published) return EndpointExtensions.Bad("Published results cannot be reopened.");
                    plan.Status = PlanStatus.Open; plan.ApprovedByName = null; plan.ApprovedAt = null;
                    break;
                case "publish":
                    if (plan.Status != PlanStatus.Approved) return EndpointExtensions.Bad("Results must be approved before they are published.");
                    plan.Status = PlanStatus.Published; plan.PublishedAt = DateTime.UtcNow;
                    var students = await db.Enrollments.Where(e => e.SectionId == plan.SectionId && e.Status == "active").Select(e => e.StudentId).ToListAsync();
                    await notifier.ToGuardiansOf(students, "Results published", $"New results ({plan.Name}) are available in the parent portal.", "results", "inapp", "email");
                    break;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { plan.Status });
        }).Require(Perm.ExamsApprove);

        // ---- Report cards ----
        g.MapGet("report-cards", async (Guid sectionId, Guid termId, Guid? enrollmentId, AppDbContext db, ReportCardService svc) =>
        {
            var ids = enrollmentId is { } one ? [one] : await db.Enrollments.Where(e => e.SectionId == sectionId).Select(e => e.Id).ToListAsync();
            return await svc.Build(ids, termId, publishedOnly: false);
        }).Require(Perm.ExamsManage, Perm.ExamsApprove);

        g.MapGet("report-cards/pdf", async (Guid sectionId, Guid termId, Guid? enrollmentId, string? lang, AppDbContext db, ReportCardService svc) =>
        {
            var ids = enrollmentId is { } one ? [one] : await db.Enrollments.Where(e => e.SectionId == sectionId).Select(e => e.Id).ToListAsync();
            var cards = await svc.Build(ids, termId, publishedOnly: false);
            if (cards.Count == 0) return EndpointExtensions.Bad("No approved results to print yet.");
            db.Audit("export", "ReportCard", null, new { sectionId, termId, count = cards.Count });
            await db.SaveChangesAsync();
            return Results.File(Pdf.ReportCards(cards, lang ?? cards[0].Locale), "application/pdf", "report-cards.pdf");
        }).Require(Perm.ExamsManage, Perm.ExamsApprove);
    }
}
