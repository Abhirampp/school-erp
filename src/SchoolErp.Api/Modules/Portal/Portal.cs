using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Academics;
using SchoolErp.Api.Modules.Attendance;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Portal;

/// <summary>Parent and student portal. Every endpoint checks the child is linked to the caller ("own children" / "self" scope).</summary>
public static class PortalEndpoints
{
    public record ConsentDecision(Guid ConsentFormId, Guid StudentId, string Decision);

    private static async Task<List<Guid>> MyStudentIds(AppDbContext db, CurrentUser user) =>
        user.GuardianId is { } g ? await db.StudentGuardians.Where(sg => sg.GuardianId == g).Select(sg => sg.StudentId).ToListAsync()
        : user.StudentId is { } s ? [s] : [];

    private static async Task<bool> IsMine(AppDbContext db, CurrentUser user, Guid studentId) => (await MyStudentIds(db, user)).Contains(studentId);

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/portal").Require(Perm.PortalParent, Perm.PortalStudent);

        g.MapGet("children", async (AppDbContext db, CurrentUser user) =>
        {
            var ids = await MyStudentIds(db, user);
            var students = await db.Students.Where(s => ids.Contains(s.Id)).ToListAsync();
            var enrollments = await db.Enrollments.Where(e => ids.Contains(e.StudentId) && e.Status == "active").ToListAsync();
            var names = await AcademicsEndpoints.SectionNames(db);
            var campuses = await db.Campuses.ToDictionaryAsync(c => c.Id, c => c.Name);
            return students.Select(s =>
            {
                var e = enrollments.Where(x => x.StudentId == s.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
                return new { s.Id, name = s.FullName(), s.NativeName, s.AdmissionNumber, s.Status, campus = campuses.GetValueOrDefault(s.CampusId), s.CampusId, section = e is null ? null : names.GetValueOrDefault(e.SectionId), enrollmentId = e?.Id, sectionId = e?.SectionId, academicYearId = e?.AcademicYearId };
            });
        });

        g.MapGet("students/{id:guid}/overview", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            if (!await IsMine(db, user, id)) return EndpointExtensions.Forbidden();
            var e = await db.Enrollments.Where(x => x.StudentId == id && x.Status == "active").OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
            var attendance = e is null ? [] : await db.AttendanceRecords.Where(a => a.EnrollmentId == e.Id).OrderByDescending(a => a.Date).ToListAsync();
            var slots = e is null ? [] : await db.TimetableSlots.Where(s => s.SectionId == e.SectionId).ToListAsync();
            var year = e is null ? null : await db.AcademicYears.FirstOrDefaultAsync(y => y.Id == e.AcademicYearId);
            var periods = year is null ? [] : await db.Periods.Where(p => p.CampusId == year.CampusId).OrderBy(p => p.Order).ToListAsync();
            var terms = e is null ? [] : await db.Terms.Where(t => t.AcademicYearId == e.AcademicYearId).OrderBy(t => t.Order).ToListAsync();
            var publishedTerms = e is null ? [] : await db.AssessmentPlans.Where(p => p.SectionId == e.SectionId && p.Status == PlanStatus.Published).Select(p => p.TermId).Distinct().ToListAsync();
            // Fees are for guardians; a student login sees none.
            var invoices = user.GuardianId is null ? [] : await db.Invoices.Where(i => i.StudentId == id && i.Status != "void" && i.Status != "carried").OrderBy(i => i.DueDate).ToListAsync();
            return Results.Ok(new
            {
                enrollmentId = e?.Id,
                attendance = AttendanceEndpoints.Summarise(attendance, "", e?.RollNumber ?? 0),
                recentAbsences = attendance.Where(a => a.Category != "present").Take(10).Select(a => new { a.Date, a.Code, a.Category, a.Remarks }),
                periods = periods.Select(p => new { p.Id, p.Name, p.StartTime, p.EndTime, p.IsBreak }),
                timetable = await AcademicsEndpoints.Describe(db, slots),
                terms = terms.Select(t => new { t.Id, t.Name, published = publishedTerms.Contains(t.Id) }),
                invoices = invoices.Select(i => new { i.Id, i.Number, i.Title, i.DueDate, i.Currency, i.Total, i.PaidTotal, balance = i.Total - i.PaidTotal, i.Status })
            });
        });

        // Only published results are ever visible here (FR-06-05).
        g.MapGet("students/{id:guid}/report-card", async (Guid id, Guid termId, string? format, string? lang, AppDbContext db, CurrentUser user, ReportCardService svc) =>
        {
            if (!await IsMine(db, user, id)) return EndpointExtensions.Forbidden();
            var enrollmentIds = await db.Enrollments.Where(x => x.StudentId == id).Select(x => x.Id).ToListAsync();
            var term = await db.Terms.FirstOrDefaultAsync(t => t.Id == termId);
            if (term is null) return Results.NotFound();
            var mine = await db.Enrollments.Where(x => x.StudentId == id && x.AcademicYearId == term.AcademicYearId).OrderByDescending(x => x.CreatedAt).Select(x => x.Id).Take(1).ToListAsync();
            var cards = await svc.Build(mine, termId, publishedOnly: true);
            if (cards.Count == 0) return Results.NotFound();
            return format == "pdf" ? Results.File(Pdf.ReportCards(cards, lang ?? cards[0].Locale), "application/pdf", "report-card.pdf") : Results.Ok(cards[0]);
        });

        var parent = app.MapGroup("/api/portal").Require(Perm.PortalParent);

        parent.MapGet("invoices/{id:guid}", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
            if (invoice is null || !await IsMine(db, user, invoice.StudentId)) return Results.NotFound();
            return Results.Ok(await FeeEndpoints.InvoiceDetail(db, id));
        });

        parent.MapPost("invoices/{id:guid}/pay", async (Guid id, AppDbContext db, CurrentUser user, FeeService fees) =>
        {
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
            if (invoice is null || !await IsMine(db, user, invoice.StudentId)) return Results.NotFound();
            var (url, error) = await fees.StartOnlinePayment(invoice);
            return error is null ? Results.Ok(new { url }) : EndpointExtensions.Bad(error);
        });

        parent.MapGet("payments/{id:guid}/receipt", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var p = await db.Payments.FirstOrDefaultAsync(x => x.Id == id && x.ReceiptNumber != null);
            if (p is null) return Results.NotFound();
            var invoice = await db.Invoices.FirstAsync(i => i.Id == p.InvoiceId);
            if (!await IsMine(db, user, invoice.StudentId)) return Results.NotFound();
            return Results.File(await FeeEndpoints.Receipt(db, p), "application/pdf", $"{p.ReceiptNumber}.pdf");
        });

        parent.MapGet("consents", async (AppDbContext db, CurrentUser user) =>
        {
            var ids = await MyStudentIds(db, user);
            var students = await db.Students.Where(s => ids.Contains(s.Id) && s.Status == "active").ToListAsync();
            var consents = await db.Consents.Where(c => ids.Contains(c.StudentId)).ToListAsync();
            var result = new List<object>();
            foreach (var s in students)
            {
                var forms = await db.ConsentForms.Where(f => f.CampusId == s.CampusId).OrderByDescending(f => f.CreatedAt).ToListAsync();
                foreach (var f in forms)
                {
                    if (f.AudienceType != "campus" && !(await Audience.StudentIds(db, f.CampusId, f.AudienceType, f.AudienceId)).Contains(s.Id)) continue;
                    var c = consents.Where(x => x.ConsentFormId == f.Id && x.StudentId == s.Id).OrderByDescending(x => x.DecidedAt).FirstOrDefault();
                    result.Add(new { formId = f.Id, f.Title, f.Body, f.Purpose, f.DueDate, studentId = s.Id, student = s.FullName(), consentId = c?.Id, decision = c?.WithdrawnAt != null ? "withdrawn" : c?.Decision, c?.DecidedAt });
                }
            }
            return result;
        });

        parent.MapPost("consents", async (ConsentDecision req, AppDbContext db, CurrentUser user) =>
        {
            if (req.Decision is not ("granted" or "declined")) return EndpointExtensions.Bad("Decision must be granted or declined.");
            if (!await IsMine(db, user, req.StudentId) || !await db.ConsentForms.AnyAsync(f => f.Id == req.ConsentFormId)) return EndpointExtensions.Forbidden();
            // A new row per decision keeps the full history of what was agreed and when.
            var c = new Consent { ConsentFormId = req.ConsentFormId, StudentId = req.StudentId, GuardianId = user.GuardianId!.Value, GrantedByName = user.Name, Decision = req.Decision, DecidedAt = DateTime.UtcNow, Ip = user.Ip };
            db.Add(c);
            await db.SaveChangesAsync();
            return Results.Ok(new { c.Id });
        });

        parent.MapPost("consents/{id:guid}/withdraw", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var c = await db.Consents.FirstOrDefaultAsync(x => x.Id == id && x.GuardianId == user.GuardianId);
            if (c is null) return Results.NotFound();
            c.WithdrawnAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }
}
