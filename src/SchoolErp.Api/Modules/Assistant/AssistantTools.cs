using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Academics;
using SchoolErp.Api.Modules.Attendance;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Assistant;

public record AssistantToolDef(string Name, string Description, string SchemaJson);

/// <summary>
/// The read-only data tools the assistant can call. Every tool runs as the signed-in user: the tenant filter,
/// campus scope, teacher scope and permissions apply exactly as on the normal screens, so the assistant can never
/// show someone more than their own screens would.
/// </summary>
public class AssistantTools(AppDbContext db, CurrentUser user)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ClassArg = """{"type":"string","description":"Class name such as 'Grade 5 A' or '5A'"}""";

    public static readonly AssistantToolDef[] Definitions =
    [
        new("school_overview", "Headline numbers for each campus the user can see: active academic year, enrolled students, staff, classes, open applications and today's attendance.",
            """{"type":"object","properties":{},"required":[]}"""),
        new("find_students", "Search students by name or admission number, optionally within one class or by status. Returns up to 50 matches with class and roll number.",
            $$$"""{"type":"object","properties":{"query":{"type":"string","description":"Part of a name or admission number; empty lists everyone"},"class_name":{{{ClassArg}}},"status":{"type":"string","enum":["active","withdrawn","transferred","graduated"]}},"required":[]}"""),
        new("student_details", "Everything about one student the user may see: profile, class, guardians, attendance over the last 30 days, recent absences, latest results by subject, and outstanding fees.",
            """{"type":"object","properties":{"student":{"type":"string","description":"Student name or admission number"}},"required":["student"]}"""),
        new("class_details", "One class: class teacher, enrolment against capacity, subjects with their teachers, attendance rate over the last 30 days, and each student's attendance.",
            $$$"""{"type":"object","properties":{"class_name":{{{ClassArg}}}},"required":["class_name"]}"""),
        new("attendance_report", "Attendance totals per class for a date or a date range (default: today), with the names of absent and late students when one day is asked for.",
            $$$"""{"type":"object","properties":{"from":{"type":"string","description":"Start date, YYYY-MM-DD"},"to":{"type":"string","description":"End date, YYYY-MM-DD"},"class_name":{{{ClassArg}}}},"required":[]}"""),
        new("fee_report", "Fee totals (invoiced, collected, outstanding, overdue) and the students with the largest unpaid balances, for the whole campus or one class.",
            $$$"""{"type":"object","properties":{"class_name":{{{ClassArg}}},"overdue_only":{"type":"boolean","description":"Only count invoices past their due date"}},"required":[]}"""),
        new("exam_results", "Exam results for a class: each subject's plan status, class average, grade spread, and the top and bottom students. Includes approved and published results.",
            $$$"""{"type":"object","properties":{"class_name":{{{ClassArg}}},"term":{"type":"string","description":"Term name such as 'Term 1'; default is the latest term with results"},"subject":{"type":"string"}},"required":["class_name"]}"""),
        new("staff_lookup", "Find staff by name, subject or designation, or list everyone. For a single match also returns subjects and classes taught, periods per week, class teacher role and recent leave.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Name, subject or designation; empty lists all staff"}},"required":[]}"""),
        new("admissions_report", "Admissions pipeline: number of applications at each stage and the most recent applications.",
            """{"type":"object","properties":{},"required":[]}"""),
        new("payroll_report", "Payroll run for a month: status, totals and each staff member's gross, deductions and net pay.",
            """{"type":"object","properties":{"month":{"type":"string","description":"Month as YYYY-MM; default is the latest run"}},"required":[]}"""),
    ];

    /// <summary>Runs a tool and returns its result as JSON text. Unknown tools and missing permissions come back as an error object.</summary>
    public async Task<(string Json, bool IsError)> Run(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        string? S(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
        bool B(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;
        try
        {
            object result = name switch
            {
                "school_overview" => await Overview(),
                "find_students" => await FindStudents(S("query"), S("class_name"), S("status")),
                "student_details" => await StudentDetails(S("student") ?? ""),
                "class_details" => await ClassDetails(S("class_name") ?? ""),
                "attendance_report" => await AttendanceReport(S("from"), S("to"), S("class_name")),
                "fee_report" => await FeeReport(S("class_name"), B("overdue_only")),
                "exam_results" => await ExamResults(S("class_name") ?? "", S("term"), S("subject")),
                "staff_lookup" => await StaffLookup(S("query")),
                "admissions_report" => await Admissions(),
                "payroll_report" => await Payroll(S("month")),
                _ => new ToolError($"Unknown tool '{name}'."),
            };
            return (JsonSerializer.Serialize(result, Json), result is ToolError);
        }
        catch (DeniedException e) { return (JsonSerializer.Serialize(new ToolError(e.Message), Json), true); }
    }

    private record ToolError(string Error);
    private class DeniedException(string message) : Exception(message);

    private void Need(string what, params string[] perms)
    {
        if (!perms.Any(user.Has)) throw new DeniedException($"The signed-in user does not have permission to see {what}.");
    }

    // ---- shared lookups ----

    private async Task<List<Admin.Campus>> Campuses() =>
        (await db.Campuses.ToListAsync()).Where(c => user.CanAccessCampus(c.Id)).ToList();

    private record ClassRef(Admin.Section Section, string Label, Guid CampusId, Guid YearId);

    /// <summary>Classes in the active year of every campus the user can see. A teacher sees only the classes they teach.</summary>
    private async Task<List<ClassRef>> ActiveClasses()
    {
        var campuses = await Campuses();
        var campusIds = campuses.Select(c => c.Id).ToList();
        var years = await db.AcademicYears.Where(y => y.Status == "active" && campusIds.Contains(y.CampusId)).ToListAsync();
        var yearIds = years.Select(y => y.Id).ToList();
        var sections = await db.Sections.Where(s => yearIds.Contains(s.AcademicYearId)).ToListAsync();
        if (user.Scope == Scopes.Assigned)
        {
            var mine = await StudentEndpoints.AssignedSectionIds(db, user);
            sections = sections.Where(s => mine.Contains(s.Id)).ToList();
        }
        var grades = await db.GradeLevels.ToDictionaryAsync(g => g.Id);
        var multi = campuses.Count > 1;
        return sections.Select(s =>
        {
            var year = years.First(y => y.Id == s.AcademicYearId);
            var label = $"{(grades.TryGetValue(s.GradeLevelId, out var g) ? g.Name : "")} {s.Name}";
            if (multi) label += $" ({campuses.First(c => c.Id == year.CampusId).Name})";
            return new ClassRef(s, label, year.CampusId, year.Id);
        }).OrderBy(c => grades.TryGetValue(c.Section.GradeLevelId, out var g) ? g.Order : 0).ThenBy(c => c.Label).ToList();
    }

    private static string Norm(string s) => new string(s.ToLowerInvariant().Replace("grade", "").Replace("class", "").Where(char.IsLetterOrDigit).ToArray());

    private async Task<ClassRef> FindClass(string name)
    {
        var classes = await ActiveClasses();
        var wanted = Norm(name);
        var match = classes.Where(c => Norm(c.Label).StartsWith(wanted) || Norm(c.Label.Split(" (")[0]) == wanted).ToList();
        if (match.Count == 1) return match[0];
        if (match.Count > 1)
        {
            var exact = match.Where(c => Norm(c.Label.Split(" (")[0]) == wanted).ToList();
            if (exact.Count == 1) return exact[0];
            throw new DeniedException($"'{name}' matches several classes: {string.Join(", ", match.Select(c => c.Label))}. Ask which one.");
        }
        throw new DeniedException($"No class called '{name}' that this user can see. Classes: {string.Join(", ", classes.Select(c => c.Label))}.");
    }

    private DateOnly Today(Guid? campusId, List<Admin.Campus> campuses) =>
        Clock.Today(campuses.FirstOrDefault(c => c.Id == campusId)?.TimeZone ?? campuses.FirstOrDefault()?.TimeZone);

    private static decimal? Rate(IEnumerable<AttendanceRecord> records)
    {
        var list = records.ToList();
        return list.Count == 0 ? null : Math.Round(100m * list.Count(a => a.Category is "present" or "late") / list.Count, 1);
    }

    // ---- tools ----

    private async Task<object> Overview()
    {
        var campuses = await Campuses();
        var result = new List<object>();
        foreach (var c in campuses)
        {
            var year = await db.AcademicYears.Where(y => y.CampusId == c.Id && y.Status == "active").OrderByDescending(y => y.StartDate).FirstOrDefaultAsync();
            var sectionIds = year is null ? [] : await db.Sections.Where(s => s.AcademicYearId == year.Id).Select(s => s.Id).ToListAsync();
            var today = Clock.Today(c.TimeZone);
            var att = await db.AttendanceRecords.Where(a => sectionIds.Contains(a.SectionId) && a.Date == today).ToListAsync();
            result.Add(new
            {
                campus = c.Name, c.Country, c.Currency, c.Curriculum, academicYear = year?.Name, today,
                enrolledStudents = await db.Enrollments.CountAsync(e => sectionIds.Contains(e.SectionId) && e.Status == "active"),
                activeStaff = await db.Staff.CountAsync(s => s.CampusId == c.Id && s.Status == "active"),
                classes = sectionIds.Count,
                openApplications = user.Has(Perm.Admissions) ? await db.Applications.CountAsync(a => a.CampusId == c.Id && a.Stage != "enrolled" && a.Stage != "rejected") : (int?)null,
                attendanceToday = new { marked = att.Count, absent = att.Count(a => a.Category is "authorized" or "unauthorized"), late = att.Count(a => a.Category == "late") }
            });
        }
        return new { campuses = result };
    }

    private async Task<object> FindStudents(string? query, string? className, string? status)
    {
        Need("student records", Perm.StudentsRead);
        var q = await StudentEndpoints.Visible(db, user);
        if (status is not null) q = q.Where(s => s.Status == status);
        if (query is not null)
        {
            var t = query.ToLower();
            q = q.Where(s => s.FirstName.ToLower().Contains(t) || s.LastName.ToLower().Contains(t) || (s.FirstName + " " + s.LastName).ToLower().Contains(t) || s.AdmissionNumber.ToLower().Contains(t));
        }
        ClassRef? cls = className is null ? null : await FindClass(className);
        if (cls is not null)
        {
            var ids = await db.Enrollments.Where(e => e.SectionId == cls.Section.Id && e.Status == "active").Select(e => e.StudentId).ToListAsync();
            q = q.Where(s => ids.Contains(s.Id));
        }
        var students = await q.OrderBy(s => s.FirstName).ThenBy(s => s.LastName).Take(50).ToListAsync();
        var classes = (await ActiveClasses()).ToDictionary(c => c.Section.Id, c => c.Label);
        var studentIds = students.Select(s => s.Id).ToList();
        var enrollments = await db.Enrollments.Where(e => studentIds.Contains(e.StudentId) && e.Status == "active").ToListAsync();
        return new
        {
            count = students.Count, truncated = students.Count == 50,
            students = students.Select(s =>
            {
                var e = enrollments.FirstOrDefault(x => x.StudentId == s.Id);
                return new { name = s.FullName(), s.AdmissionNumber, s.Status, @class = e is null ? null : classes.GetValueOrDefault(e.SectionId), rollNumber = e?.RollNumber };
            })
        };
    }

    private async Task<object> StudentDetails(string who)
    {
        Need("student records", Perm.StudentsRead);
        var t = who.ToLower();
        var matches = await (await StudentEndpoints.Visible(db, user))
            .Where(s => s.AdmissionNumber.ToLower() == t || (s.FirstName + " " + s.LastName).ToLower().Contains(t) || s.FirstName.ToLower() == t).Take(6).ToListAsync();
        if (matches.Count == 0) return new ToolError($"No student matching '{who}' that this user can see.");
        if (matches.Count > 1) return new { ambiguous = true, matches = matches.Select(s => new { name = s.FullName(), s.AdmissionNumber }) };
        var s = matches[0];
        var enrollment = await db.Enrollments.Where(e => e.StudentId == s.Id && e.Status == "active").OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync();
        var classes = (await ActiveClasses()).ToDictionary(c => c.Section.Id, c => c.Label);
        var guardianIds = await db.StudentGuardians.Where(x => x.StudentId == s.Id).Select(x => x.GuardianId).ToListAsync();
        var guardians = await db.Guardians.Where(g => guardianIds.Contains(g.Id)).ToListAsync();
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var att = enrollment is null ? [] : await db.AttendanceRecords.Where(a => a.EnrollmentId == enrollment.Id && a.Date >= since).OrderByDescending(a => a.Date).ToListAsync();

        object? results = null;
        if (enrollment is not null && (user.Has(Perm.ExamsManage) || user.Has(Perm.ExamsApprove) || user.Has(Perm.ExamsMarks)))
        {
            var cards = await new ReportCardService(db).Build([enrollment.Id], await LatestTermWithResults(enrollment.SectionId) ?? Guid.Empty, publishedOnly: false);
            results = cards.FirstOrDefault() is { } card ? new { card.Term, card.OverallPercent, card.Gpa, subjects = card.Subjects.Select(x => new { x.Subject, x.Percent, x.Grade }) } : null;
        }
        object? fees = null;
        if (user.Has(Perm.FeesRead) || user.Has(Perm.FeesManage))
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var open = await db.Invoices.Where(i => i.StudentId == s.Id && (i.Status == "open" || i.Status == "partial")).ToListAsync();
            fees = new { outstanding = open.Sum(i => i.Total - i.PaidTotal), overdue = open.Where(i => i.DueDate < today).Sum(i => i.Total - i.PaidTotal), currency = open.FirstOrDefault()?.Currency, openInvoices = open.Select(i => new { i.Number, i.Title, i.DueDate, balance = i.Total - i.PaidTotal }) };
        }
        var medical = user.Has(Perm.StudentsMedical);
        return new
        {
            name = s.FullName(), s.NativeName, s.AdmissionNumber, s.Status, s.DateOfBirth, s.Gender,
            @class = enrollment is null ? null : classes.GetValueOrDefault(enrollment.SectionId), rollNumber = enrollment?.RollNumber,
            guardians = guardians.Select(g => new { name = g.FullName(), g.Email, g.Phone }),
            attendanceLast30Days = new { ratePercent = Rate(att), days = att.Count, absences = att.Where(a => a.Category != "present").Take(10).Select(a => new { a.Date, a.Code, a.Category }) },
            latestResults = results,
            fees,
            medicalNotes = medical ? s.MedicalNotes : null, specialNeeds = medical ? s.SpecialNeeds : null,
            note = medical ? null : "Medical and special-needs information is hidden for this user's role."
        };
    }

    private async Task<Guid?> LatestTermWithResults(Guid sectionId)
    {
        var termIds = await db.AssessmentPlans.Where(p => p.SectionId == sectionId && (p.Status == PlanStatus.Approved || p.Status == PlanStatus.Published)).Select(p => p.TermId).Distinct().ToListAsync();
        return (await db.Terms.Where(t => termIds.Contains(t.Id)).OrderByDescending(t => t.Order).FirstOrDefaultAsync())?.Id;
    }

    private async Task<object> ClassDetails(string name)
    {
        var cls = await FindClass(name);
        var staff = await db.Staff.ToDictionaryAsync(x => x.Id, x => x.FullName());
        var subjects = await db.Subjects.ToDictionaryAsync(x => x.Id, x => x.Name);
        var cs = await db.ClassSubjects.Where(c => c.SectionId == cls.Section.Id).ToListAsync();
        var slots = await db.TimetableSlots.Where(t => t.SectionId == cls.Section.Id).Select(t => t.ClassSubjectId).ToListAsync();
        var enrollments = await db.Enrollments.Where(e => e.SectionId == cls.Section.Id && e.Status == "active").OrderBy(e => e.RollNumber).ToListAsync();
        var studentIds = enrollments.Select(e => e.StudentId).ToList();
        var students = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var att = await db.AttendanceRecords.Where(a => a.SectionId == cls.Section.Id && a.Date >= since).ToListAsync();
        return new
        {
            @class = cls.Label, classTeacher = cls.Section.ClassTeacherId is { } t ? staff.GetValueOrDefault(t) : null,
            enrolled = enrollments.Count, capacity = cls.Section.Capacity,
            subjects = cs.Select(c => new { subject = subjects.GetValueOrDefault(c.SubjectId), teacher = c.TeacherId is { } x ? staff.GetValueOrDefault(x) : null, periodsPerWeek = slots.Count(id => id == c.Id) }).OrderBy(x => x.subject),
            attendanceRateLast30Days = Rate(att),
            students = user.Has(Perm.StudentsRead)
                ? enrollments.Select(e => new { e.RollNumber, name = students[e.StudentId].FullName(), students[e.StudentId].AdmissionNumber, attendanceRate = Rate(att.Where(a => a.EnrollmentId == e.Id)) })
                : null
        };
    }

    private async Task<object> AttendanceReport(string? from, string? to, string? className)
    {
        Need("attendance", Perm.AttendanceRead, Perm.AttendanceMark);
        var campuses = await Campuses();
        var classes = className is null ? await ActiveClasses() : [await FindClass(className)];
        var start = DateOnly.TryParse(from, out var f) ? f : Today(classes.FirstOrDefault()?.CampusId, campuses);
        var end = DateOnly.TryParse(to, out var e2) ? e2 : start;
        if (end < start) (start, end) = (end, start);
        if (end.DayNumber - start.DayNumber > 366) return new ToolError("Ask for at most one year at a time.");
        var ids = classes.Select(c => c.Section.Id).ToList();
        var records = await db.AttendanceRecords.Where(a => ids.Contains(a.SectionId) && a.Date >= start && a.Date <= end).ToListAsync();
        var oneDay = start == end;
        Dictionary<Guid, string> names = [];
        if (oneDay && user.Has(Perm.StudentsRead))
        {
            var who = records.Where(r => r.Category != "present").Select(r => r.StudentId).ToList();
            names = await db.Students.Where(s => who.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName);
        }
        return new
        {
            from = start, to = end,
            classes = classes.Select(c =>
            {
                var r = records.Where(x => x.SectionId == c.Section.Id).ToList();
                return new
                {
                    @class = c.Label, marked = r.Count, present = r.Count(x => x.Category == "present"), late = r.Count(x => x.Category == "late"),
                    authorisedAbsence = r.Count(x => x.Category == "authorized"), unauthorisedAbsence = r.Count(x => x.Category == "unauthorized"), ratePercent = Rate(r),
                    notPresent = oneDay ? r.Where(x => x.Category != "present").Select(x => new { name = names.GetValueOrDefault(x.StudentId), x.Code, x.Category }) : null
                };
            }),
            note = records.Count == 0 ? "No attendance has been marked for these dates." : null
        };
    }

    private async Task<object> FeeReport(string? className, bool overdueOnly)
    {
        Need("fees", Perm.FeesRead, Perm.FeesManage);
        var campuses = await Campuses();
        var campusIds = campuses.Select(c => c.Id).ToList();
        var q = db.Invoices.Where(i => campusIds.Contains(i.CampusId) && i.Status != "void" && i.Status != "carried");
        string scope = "all campuses this user can see";
        if (className is not null)
        {
            var cls = await FindClass(className);
            var ids = await db.Enrollments.Where(e => e.SectionId == cls.Section.Id && e.Status == "active").Select(e => e.StudentId).ToListAsync();
            q = q.Where(i => ids.Contains(i.StudentId) && i.AcademicYearId == cls.YearId);
            scope = cls.Label;
        }
        var invoices = await q.ToListAsync();
        var today = Today(null, campuses);
        if (overdueOnly) invoices = invoices.Where(i => i.DueDate < today && i.Total > i.PaidTotal).ToList();
        var studentIds = invoices.Select(i => i.StudentId).Distinct().ToList();
        var students = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        return new
        {
            scope, overdueOnly,
            byCurrency = invoices.GroupBy(i => i.Currency).Select(g => new
            {
                currency = g.Key, invoiced = g.Sum(i => i.Total), collected = g.Sum(i => i.PaidTotal), outstanding = g.Sum(i => i.Total - i.PaidTotal),
                overdue = g.Where(i => i.DueDate < today).Sum(i => i.Total - i.PaidTotal), invoices = g.Count()
            }),
            largestBalances = invoices.Where(i => i.Total > i.PaidTotal).GroupBy(i => i.StudentId).Select(g => new
            {
                student = students.TryGetValue(g.Key, out var s) ? s.FullName() : null, admissionNumber = students.TryGetValue(g.Key, out var s2) ? s2.AdmissionNumber : null,
                balance = g.Sum(i => i.Total - i.PaidTotal), overdue = g.Where(i => i.DueDate < today).Sum(i => i.Total - i.PaidTotal), currency = g.First().Currency,
                oldestDue = g.Min(i => i.DueDate)
            }).OrderByDescending(x => x.balance).Take(20)
        };
    }

    private async Task<object> ExamResults(string className, string? termName, string? subjectName)
    {
        Need("exam results", Perm.ExamsManage, Perm.ExamsApprove, Perm.ExamsMarks);
        var cls = await FindClass(className);
        var terms = await db.Terms.Where(t => t.AcademicYearId == cls.YearId).OrderBy(t => t.Order).ToListAsync();
        var latest = await LatestTermWithResults(cls.Section.Id);
        var term = termName is null ? terms.FirstOrDefault(t => t.Id == latest) ?? terms.FirstOrDefault()
            : terms.FirstOrDefault(t => Norm(t.Name) == Norm(termName));
        if (term is null) return new ToolError($"No term called '{termName}'. Terms: {string.Join(", ", terms.Select(t => t.Name))}.");
        var plans = await db.AssessmentPlans.Where(p => p.SectionId == cls.Section.Id && p.TermId == term.Id).ToListAsync();
        var subjects = await db.Subjects.ToDictionaryAsync(s => s.Id, s => s.Name);
        var cs = await db.ClassSubjects.Where(c => c.SectionId == cls.Section.Id).ToDictionaryAsync(c => c.Id, c => c.SubjectId);
        var enrollmentIds = await db.Enrollments.Where(e => e.SectionId == cls.Section.Id && e.Status == "active").Select(e => e.Id).ToListAsync();
        var cards = await new ReportCardService(db).Build(enrollmentIds, term.Id, publishedOnly: false);
        var rows = cards.SelectMany(c => c.Subjects.Select(s => (student: c.Student, s.Subject, s.Percent, s.Grade)))
            .Where(r => subjectName is null || r.Subject.Contains(subjectName, StringComparison.OrdinalIgnoreCase)).ToList();
        return new
        {
            @class = cls.Label, term = term.Name,
            plans = plans.Select(p => new { subject = cs.TryGetValue(p.ClassSubjectId, out var sid) ? subjects.GetValueOrDefault(sid) : null, p.Status })
                .Where(p => subjectName is null || (p.subject ?? "").Contains(subjectName, StringComparison.OrdinalIgnoreCase)),
            subjects = rows.GroupBy(r => r.Subject).Select(g => new
            {
                subject = g.Key, students = g.Count(), averagePercent = g.Any(x => x.Percent is not null) ? Math.Round(g.Where(x => x.Percent is not null).Average(x => x.Percent!.Value), 1) : (decimal?)null,
                grades = g.GroupBy(x => x.Grade ?? "none").OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count())
            }),
            topStudents = cards.Where(c => c.OverallPercent is not null).OrderByDescending(c => c.OverallPercent).Take(5).Select(c => new { c.Student, c.OverallPercent }),
            bottomStudents = cards.Where(c => c.OverallPercent is not null).OrderBy(c => c.OverallPercent).Take(5).Select(c => new { c.Student, c.OverallPercent }),
            note = "Only approved and published results are included; open and submitted plans are not graded yet."
        };
    }

    private async Task<object> StaffLookup(string? query)
    {
        if (user.Scope is Scopes.Children or Scopes.Self) throw new DeniedException("The signed-in user does not have permission to see staff records.");
        var campusIds = (await Campuses()).Select(c => c.Id).ToList();
        var all = await db.Staff.Where(s => campusIds.Contains(s.CampusId)).OrderBy(s => s.FirstName).ToListAsync();
        var subjects = await db.Subjects.ToDictionaryAsync(x => x.Id, x => x.Name);
        var classes = await ActiveClasses();
        var yearIds = classes.Select(c => c.YearId).Distinct().ToList();
        var cs = await db.ClassSubjects.Where(c => yearIds.Contains(c.AcademicYearId)).ToListAsync();
        var slots = await db.TimetableSlots.Where(t => yearIds.Contains(t.AcademicYearId)).Select(t => t.TeacherId).ToListAsync();
        var matches = all;
        if (query is not null)
        {
            var t = query.ToLower();
            matches = all.Where(s => s.FullName().ToLower().Contains(t) || (s.Designation ?? "").ToLower().Contains(t) || (s.Department ?? "").ToLower().Contains(t)
                || cs.Any(c => c.TeacherId == s.Id && subjects.GetValueOrDefault(c.SubjectId, "").ToLower().Contains(t))).ToList();
        }
        var labels = (await AcademicsEndpoints.SectionNames(db));
        object Row(Students.Staff s) => new
        {
            name = s.FullName(), s.EmployeeNumber, s.Designation, s.Department, s.Status, s.Email, s.Phone,
            subjects = cs.Where(c => c.TeacherId == s.Id).Select(c => subjects.GetValueOrDefault(c.SubjectId)).Distinct(),
            periodsPerWeek = slots.Count(x => x == s.Id),
            classTeacherOf = classes.Where(c => c.Section.ClassTeacherId == s.Id).Select(c => c.Label)
        };
        if (matches.Count == 1)
        {
            var s = matches[0];
            var leave = await db.LeaveRequests.Where(l => l.StaffId == s.Id).OrderByDescending(l => l.FromDate).Take(5).ToListAsync();
            return new
            {
                staff = Row(s), s.Qualification, s.JoinedOn,
                teaching = cs.Where(c => c.TeacherId == s.Id).Select(c => new { @class = labels.GetValueOrDefault(c.SectionId), subject = subjects.GetValueOrDefault(c.SubjectId) }),
                recentLeave = user.Has(Perm.LeaveApprove) || user.Has(Perm.Payroll) || user.StaffId == s.Id ? leave.Select(l => new { l.FromDate, l.ToDate, l.Reason, l.Unpaid, l.Status }) : null
            };
        }
        return new { count = matches.Count, staff = matches.Take(60).Select(Row) };
    }

    private async Task<object> Admissions()
    {
        Need("admissions", Perm.Admissions);
        var campusIds = (await Campuses()).Select(c => c.Id).ToList();
        var apps = await db.Applications.Where(a => campusIds.Contains(a.CampusId)).OrderByDescending(a => a.CreatedAt).ToListAsync();
        var grades = await db.GradeLevels.ToDictionaryAsync(g => g.Id, g => g.Name);
        return new
        {
            byStage = SchoolErp.Api.Modules.Admissions.Stages.All.ToDictionary(s => s, s => apps.Count(a => a.Stage == s)),
            recent = apps.Take(10).Select(a => new { name = $"{a.FirstName} {a.LastName}", a.Stage, a.Source, grade = a.GradeLevelId is { } g ? grades.GetValueOrDefault(g) : null, received = DateOnly.FromDateTime(a.CreatedAt) })
        };
    }

    private async Task<object> Payroll(string? month)
    {
        Need("payroll", Perm.Payroll);
        var campusIds = (await Campuses()).Select(c => c.Id).ToList();
        var runs = await db.PayrollRuns.Where(r => campusIds.Contains(r.CampusId)).OrderByDescending(r => r.Month).ToListAsync();
        var run = month is null ? runs.FirstOrDefault() : runs.FirstOrDefault(r => r.Month == month);
        if (run is null) return new ToolError(month is null ? "No payroll runs yet." : $"No payroll run for {month}. Runs: {string.Join(", ", runs.Select(r => r.Month))}.");
        var slips = await db.Payslips.Where(p => p.PayrollRunId == run.Id).OrderBy(p => p.EmployeeNumber).ToListAsync();
        return new
        {
            run.Month, run.Status, run.Currency, run.StaffCount, run.Gross, run.Deductions, run.Net, run.ApprovedByName, run.PaidAt,
            otherMonths = runs.Where(r => r.Id != run.Id).Select(r => new { r.Month, r.Status, r.Net }),
            staff = slips.Select(p => new { p.StaffName, p.EmployeeNumber, p.Gross, p.Deductions, p.UnpaidLeaveDays, p.Net })
        };
    }
}
