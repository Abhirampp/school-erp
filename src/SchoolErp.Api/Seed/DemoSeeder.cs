using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Academics;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Admissions;
using SchoolErp.Api.Modules.Attendance;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Modules.Identity;
using SchoolErp.Api.Modules.Security;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Seed;

/// <summary>Demo data for local development: two tenants (to show isolation), two campuses, one full school year.</summary>
public static class DemoSeeder
{
    public static async Task Run(IServiceProvider services, string password)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var cu = sp.GetRequiredService<CurrentUser>();
        if (await db.Tenants.AnyAsync()) return;
        db.SuppressAudit = true;
        cu.Name = "seed";
        var rng = new Random(42);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Platform operator (no tenant).
        var platform = new User { Email = "platform@schoolerp.test", DisplayName = "Platform Support", IsPlatformAdmin = true };
        platform.PasswordHash = TokenService.Hasher.HashPassword(platform, password);
        db.Add(platform);
        await db.SaveChangesAsync();

        // A second, small tenant so cross-tenant isolation is visible.
        var (oak, _) = await TenantProvisioner.Create(db, cu, "Oakridge Academy", "oakridge", "eu", "standard", "admin@oakridge.test", "Oakridge Admin", password);
        cu.TenantId = oak.Id;
        var oakCampus = new Campus { Name = "Oakridge Main", Country = "GB", Currency = "GBP", TimeZone = "Europe/London", Locale = "en-GB", Curriculum = "Cambridge" };
        db.Add(oakCampus);
        db.Add(new Student { CampusId = oakCampus.Id, AdmissionNumber = "OAK-0001", FirstName = "Oliver", LastName = "Bennett" });
        await db.SaveChangesAsync();

        var (tenant, _) = await TenantProvisioner.Create(db, cu, "Greenfield International Schools", "greenfield", "india", "premium", "admin@greenfield.test", "Meera Nair", password);
        cu.TenantId = tenant.Id;
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name);
        var svc = sp.GetRequiredService<StudentService>();
        var fees = sp.GetRequiredService<FeeService>();

        // ---------- Campus 1: Bengaluru, CBSE ----------
        var blr = new Campus { Name = "Greenfield Bengaluru", Country = "IN", Currency = "INR", TimeZone = "Asia/Kolkata", Locale = "en-IN", Curriculum = "CBSE", LateFinePercent = 2, LateFineGraceDays = 7 };
        db.Add(blr);
        var year = new AcademicYear { CampusId = blr.Id, Name = "2026-27", StartDate = new(2026, 4, 1), EndDate = new(2027, 3, 31), Status = "active" };
        db.Add(year);
        var term1 = new Term { AcademicYearId = year.Id, Name = "Term 1", Order = 1, StartDate = new(2026, 4, 1), EndDate = new(2026, 9, 30) };
        var term2 = new Term { AcademicYearId = year.Id, Name = "Term 2", Order = 2, StartDate = new(2026, 10, 1), EndDate = new(2027, 3, 31) };
        db.AddRange(term1, term2);

        var staffNames = new[] { ("Asha", "Verma", "English"), ("Rahul", "Iyer", "Mathematics"), ("Priya", "Menon", "Science"), ("Imran", "Khan", "Social Studies"), ("Sunita", "Rao", "Hindi") };
        var staff = staffNames.Select((n, i) => new Staff { CampusId = blr.Id, EmployeeNumber = $"EMP-{101 + i}", FirstName = n.Item1, LastName = n.Item2, Designation = $"{n.Item3} teacher", Department = n.Item3 == "Hindi" || n.Item3 == "English" ? "Languages" : n.Item3 == "Social Studies" ? "Humanities" : "Science and Maths", Qualification = i % 2 == 0 ? "M.A., B.Ed." : "M.Sc., B.Ed.", JoinedOn = new DateOnly(2016 + i, 6, 1), Email = $"{n.Item1.ToLower()}.{n.Item2.ToLower()}@greenfield.test", BankName = "State Bank of India", BankAccount = $"3012004{i:D5}", TaxId = $"ABCPV{1230 + i}K" }).ToList();
        db.AddRange(staff);

        var grades = new[] { "Grade 4", "Grade 5", "Grade 6" }.Select((n, i) => new GradeLevel { CampusId = blr.Id, Name = n, Order = i + 4 }).ToList();
        db.AddRange(grades);
        var sections = new List<Section>
        {
            new() { AcademicYearId = year.Id, GradeLevelId = grades[0].Id, Name = "A", ClassTeacherId = staff[4].Id },
            new() { AcademicYearId = year.Id, GradeLevelId = grades[1].Id, Name = "A", ClassTeacherId = staff[0].Id },
            new() { AcademicYearId = year.Id, GradeLevelId = grades[1].Id, Name = "B", ClassTeacherId = staff[1].Id },
            new() { AcademicYearId = year.Id, GradeLevelId = grades[2].Id, Name = "A", ClassTeacherId = staff[2].Id },
        };
        db.AddRange(sections);
        var subjects = new[] { ("ENG", "English"), ("MAT", "Mathematics"), ("SCI", "Science"), ("SST", "Social Studies"), ("HIN", "Hindi") }
            .Select(s => new Subject { CampusId = blr.Id, Code = s.Item1, Name = s.Item2, Credits = 1 }).ToList();
        db.AddRange(subjects);
        var classSubjects = sections.SelectMany(sec => subjects.Select((sub, i) => new ClassSubject { AcademicYearId = year.Id, SectionId = sec.Id, SubjectId = sub.Id, TeacherId = staff[i].Id })).ToList();
        db.AddRange(classSubjects);

        var periods = new List<Period>();
        var start = new TimeOnly(8, 30);
        for (var i = 0; i < 7; i++)
        {
            var isBreak = i == 3;
            var len = isBreak ? 20 : 45;
            periods.Add(new Period { CampusId = blr.Id, Name = isBreak ? "Break" : $"Period {(i < 3 ? i + 1 : i)}", Order = i, StartTime = start, EndTime = start.AddMinutes(len), IsBreak = isBreak });
            start = start.AddMinutes(len);
        }
        db.AddRange(periods);
        // A clash-free timetable: section k takes subject (period + day + k) mod 5, so no teacher is double-booked.
        var teaching = periods.Where(p => !p.IsBreak).ToList();
        for (var k = 0; k < sections.Count; k++)
            for (var day = 1; day <= 5; day++)
                for (var p = 0; p < 5; p++)
                {
                    var cs = classSubjects.First(c => c.SectionId == sections[k].Id && c.SubjectId == subjects[(p + day + k) % 5].Id);
                    db.Add(new TimetableSlot { AcademicYearId = year.Id, SectionId = sections[k].Id, ClassSubjectId = cs.Id, PeriodId = teaching[p].Id, Day = day, TeacherId = cs.TeacherId, Room = $"R{k + 1}0{1}" });
                }

        var codes = new[] { ("P", "Present", "present", false), ("A", "Absent", "unauthorized", true), ("L", "Late", "late", true), ("E", "Excused", "authorized", false), ("M", "Medical leave", "authorized", false) };
        db.AddRange(codes.Select(c => new AbsenceCode { CampusId = blr.Id, Code = c.Item1, Name = c.Item2, Category = c.Item3, NotifyParents = c.Item4 }));
        db.Add(new CustomFieldDefinition { EntityType = "student", Key = "bloodGroup", Label = "Blood group", FieldType = "text" });
        db.Add(new CustomFieldDefinition { EntityType = "student", Key = "busRoute", Label = "Bus route", FieldType = "text" });
        db.Add(new AdmissionFormConfig { CampusId = blr.Id, Intro = "Thank you for your interest in Greenfield Bengaluru. Applications for 2026-27 are open.", FieldsJson = """[{"key":"previousSchool","label":"Previous school","type":"text","required":true},{"key":"homeLanguage","label":"Language spoken at home","type":"text","required":false},{"key":"needsTransport","label":"Needs school transport","type":"checkbox","required":false}]""" });
        await db.SaveChangesAsync();

        // Staff logins
        async Task AddUser(string email, string name, string role, Guid? campusId, Guid? staffId)
        {
            var u = new User { Email = email, DisplayName = name, RoleId = roles[role].Id, CampusId = campusId, StaffId = staffId };
            u.PasswordHash = TokenService.Hasher.HashPassword(u, password);
            db.Add(u);
            await Task.CompletedTask;
        }
        await AddUser("principal@greenfield.test", "Dr. Kavita Rao", "Principal", blr.Id, null);
        await AddUser("accountant@greenfield.test", "Joseph Mathew", "Accountant", blr.Id, null);
        foreach (var s in staff) await AddUser(s.Email!, s.FullName(), "Teacher", blr.Id, s.Id);

        // Students and families
        string[] first = ["Aarav", "Diya", "Kabir", "Ananya", "Vihaan", "Ishita", "Arjun", "Saanvi", "Rohan", "Myra", "Aditya", "Kiara", "Dev", "Tara", "Neil", "Zoya", "Yash", "Riya", "Ayaan", "Meera", "Karan", "Nisha", "Om", "Pooja", "Sahil", "Anika", "Varun", "Leela", "Reyansh", "Aisha", "Tanvi", "Krish"];
        string[] last = ["Sharma", "Patel", "Reddy", "Nair", "Gupta", "Singh", "Das", "Mehta", "Joshi", "Bose", "Kapoor", "Pillai", "Shetty", "Chopra", "Fernandes", "Hegde"];
        var allStudents = new List<Student>();
        for (var i = 0; i < first.Length; i++)
        {
            var section = sections[i % sections.Count];
            var gradeOrder = grades.First(g => g.Id == section.GradeLevelId).Order;
            // Every second family has two children at the school (siblings share a guardian).
            var family = last[(i / 2) % last.Length];
            var s = new Student
            {
                CampusId = blr.Id, AdmissionNumber = $"GF-{2020 + i % 5}-{i + 1:D4}", FirstName = first[i], LastName = family, Gender = i % 2 == 0 ? "M" : "F",
                DateOfBirth = new DateOnly(2026 - gradeOrder - 6, 1 + i % 12, 1 + i % 27), CustomFieldsJson = $$"""{"bloodGroup":"{{new[] { "A+", "B+", "O+", "AB+" }[i % 4]}}"}"""
            };
            if (i == 0) { s.MedicalNotes = "Asthma. Carries a reliever inhaler; may need it after sport."; s.NationalId = "XXXX-XXXX-4821"; s.NativeName = "आरव शर्मा"; }
            if (i == 5) s.SpecialNeeds = "Dyslexia support plan: extra time in written assessments.";
            db.Add(s); allStudents.Add(s);
            var guardianFirst = new[] { "Neha", "Rajesh", "Lakshmi", "Thomas", "Anita", "Harpreet", "Mita", "Sanjay", "Deepa", "Arun", "Pallavi", "Suresh", "Kavya", "Vikram", "Maria", "Ganesh" }[(i / 2) % 16];
            var guardian = await svc.FindOrCreateGuardian($"{guardianFirst} {family}", $"{guardianFirst.ToLower()}.{family.ToLower()}@example.com", $"+9198000{(i / 2):D5}");
            db.Add(new StudentGuardian { StudentId = s.Id, GuardianId = guardian.Id, Relationship = "parent", IsPrimary = true });
            await svc.EnsureFeeAccount(guardian.Id);
            await svc.Enroll(s, section);
            await db.SaveChangesAsync();
        }
        // Give the demo parent a password too, so both sign-in methods can be tried.
        var demoParent = await db.Users.FirstAsync(u => u.Email == "neha.sharma@example.com");
        demoParent.PasswordHash = TokenService.Hasher.HashPassword(demoParent, password);
        var enrollments = await db.Enrollments.ToListAsync();

        // Attendance for the last 10 school days
        var teacherUser = await db.Users.FirstAsync(u => u.StaffId == staff[0].Id);
        var days = Enumerable.Range(1, 16).Select(d => today.AddDays(-d)).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Take(10).ToList();
        foreach (var day in days)
            foreach (var e in enrollments)
            {
                var roll = rng.Next(100);
                var (code, cat) = roll < 90 ? ("P", "present") : roll < 94 ? ("L", "late") : roll < 97 ? ("A", "unauthorized") : ("M", "authorized");
                db.Add(new AttendanceRecord { EnrollmentId = e.Id, StudentId = e.StudentId, SectionId = e.SectionId, Date = day, Code = code, Category = cat, MarkedById = teacherUser.Id, MarkedAt = DateTime.UtcNow, AlertSent = code is "A" or "L" });
            }
        db.Add(new LeaveRequest { StaffId = staff[2].Id, FromDate = today.AddDays(7), ToDate = today.AddDays(8), Reason = "Family function" });

        // Payroll: a salary structure per teacher (Basic plus allowances; PF and professional tax deducted).
        foreach (var (s, i) in staff.Select((s, i) => (s, i)))
        {
            var rules = new[] { ("Basic", "earning", "fixed", 42000m + i * 4000), ("House rent allowance", "earning", "percent", 40m), ("Transport allowance", "earning", "fixed", 2400m),
                                ("Provident fund", "deduction", "percent", 12m), ("Professional tax", "deduction", "fixed", 200m) };
            db.AddRange(rules.Select((r, n) => new SchoolErp.Api.Modules.Payroll.PayComponent { StaffId = s.Id, Name = r.Item1, Kind = r.Item2, Type = r.Item3, Amount = r.Item4, Order = n }));
        }
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        db.Add(new LeaveRequest { StaffId = staff[3].Id, FromDate = lastMonth.AddDays(9), ToDate = lastMonth.AddDays(10), Reason = "Personal", Unpaid = true, Status = "approved", DecidedByName = "Dr. Kavita Rao", DecidedAt = DateTime.UtcNow.AddDays(-30) });

        // Grading: CBSE and GPA schemes from presets (pure configuration)
        GradingScheme Scheme(string key, Guid campusId, Guid yearId) { var p = GradingEngine.Presets[key]; var s = new GradingScheme { CampusId = campusId, AcademicYearId = yearId, Name = p.Name, ScaleType = p.ScaleType, DefinitionJson = GradingEngine.Serialize(p.Def) }; db.Add(s); return s; }
        var cbse = Scheme("cbse", blr.Id, year.Id);
        Scheme("gpa", blr.Id, year.Id);

        // Term 1 assessment plans for every section; Grade 5 A shows each workflow stage.
        var g5a = sections[1];
        foreach (var sec in sections)
            foreach (var (cs, i) in classSubjects.Where(c => c.SectionId == sec.Id).Select((c, i) => (c, i)))
            {
                var status = sec.Id != g5a.Id ? PlanStatus.Published : i switch { 0 or 1 => PlanStatus.Published, 2 => PlanStatus.Approved, 3 => PlanStatus.Submitted, _ => PlanStatus.Open };
                var plan = new AssessmentPlan { AcademicYearId = year.Id, TermId = term1.Id, ClassSubjectId = cs.Id, SectionId = sec.Id, GradingSchemeId = cbse.Id, Name = $"Term 1 {subjects[i].Name}", Status = status };
                if (status is PlanStatus.Approved or PlanStatus.Published) { plan.ApprovedByName = "Dr. Kavita Rao"; plan.ApprovedAt = DateTime.UtcNow.AddDays(-12); }
                if (status == PlanStatus.Published) plan.PublishedAt = DateTime.UtcNow.AddDays(-10);
                db.Add(plan);
                var comps = new[] { ("Periodic test", 20m, 40m), ("Notebook", 10m, 10m), ("Half-yearly exam", 70m, 80m) }
                    .Select((c, n) => new Assessment { AssessmentPlanId = plan.Id, Name = c.Item1, Weight = c.Item2, MaxMarks = c.Item3, Order = n }).ToList();
                db.AddRange(comps);
                foreach (var e in enrollments.Where(e => e.SectionId == sec.Id))
                {
                    var ability = 0.45 + rng.NextDouble() * 0.5;
                    foreach (var c in comps)
                    {
                        // The open plan is left half entered so marks entry has something to do.
                        if (status == PlanStatus.Open && c.Order == 2) continue;
                        var marks = Math.Round((decimal)Math.Clamp(ability + (rng.NextDouble() - 0.5) * 0.2, 0.2, 1.0) * c.MaxMarks * 2) / 2;
                        db.Add(new Score { AssessmentId = c.Id, EnrollmentId = e.Id, Marks = marks });
                    }
                }
            }
        await db.SaveChangesAsync();

        // Fees: heads, a structure per grade, discounts, invoices, some payments
        var heads = new[] { ("TUI", "Tuition"), ("TRN", "Transport"), ("ACT", "Activities") }.Select(h => new FeeHead { CampusId = blr.Id, Code = h.Item1, Name = h.Item2 }).ToList();
        db.AddRange(heads);
        foreach (var (grade, n) in grades.Select((g, n) => (g, n)))
        {
            var fs = new FeeStructure { AcademicYearId = year.Id, GradeLevelId = grade.Id, Name = $"{grade.Name} annual fees", Currency = "INR" };
            db.Add(fs);
            db.Add(new FeeStructureItem { FeeStructureId = fs.Id, FeeHeadId = heads[0].Id, Amount = 90000 + n * 10000 });
            db.Add(new FeeStructureItem { FeeStructureId = fs.Id, FeeHeadId = heads[1].Id, Amount = 24000 });
            db.Add(new FeeStructureItem { FeeStructureId = fs.Id, FeeHeadId = heads[2].Id, Amount = 10000 });
            db.Add(new Installment { FeeStructureId = fs.Id, Name = "Installment 1", Percent = 40, Order = 0, DueDate = new(2026, 4, 15) });
            db.Add(new Installment { FeeStructureId = fs.Id, Name = "Installment 2", Percent = 30, Order = 1, DueDate = new(2026, 8, 15) });
            db.Add(new Installment { FeeStructureId = fs.Id, Name = "Installment 3", Percent = 30, Order = 2, DueDate = new(2026, 12, 15) });
        }
        db.Add(new Discount { Name = "Sibling concession", Kind = "sibling", Type = "percent", Value = 10, AutoApplySibling = true });
        var merit = new Discount { Name = "Merit scholarship", Kind = "scholarship", Type = "percent", Value = 25 };
        db.Add(merit);
        db.Add(new StudentDiscount { StudentId = allStudents[3].Id, DiscountId = merit.Id, AcademicYearId = year.Id });
        await db.SaveChangesAsync();
        await fees.GenerateInvoices(year.Id, null, null);
        var invoices = await db.Invoices.ToListAsync();
        var installmentOrder = await db.Installments.ToDictionaryAsync(i => i.Id, i => i.Order);
        foreach (var inv in invoices)
        {
            var order = installmentOrder[inv.InstallmentId!.Value];
            var studentIndex = allStudents.FindIndex(s => s.Id == inv.StudentId);
            // Everyone paid installment 1 except a few; most paid installment 2; installment 3 is not due yet.
            var pay = order == 0 ? studentIndex % 9 != 4 : order == 1 && studentIndex % 4 != 1;
            if (!pay) continue;
            var p = new Payment { InvoiceId = inv.Id, Amount = studentIndex % 7 == 3 && order == 1 ? Math.Round(inv.Total / 2) : inv.Total, Currency = inv.Currency, Method = studentIndex % 3 == 0 ? "cash" : "bank" };
            db.Add(p);
            await fees.Settle(p, inv);
        }
        await db.SaveChangesAsync();

        // Admissions pipeline
        var applicants = new[] { ("Ira", "Kulkarni", Stages.Enquiry), ("Samar", "Bhatt", Stages.Applied), ("Naina", "Sethi", Stages.Applied), ("Rehan", "Ali", Stages.Assessed), ("Tia", "D'Souza", Stages.Offered), ("Manav", "Goel", Stages.Waitlisted), ("Esha", "Roy", Stages.Rejected) };
        foreach (var (a, i) in applicants.Select((a, i) => (a, i)))
            db.Add(new Application
            {
                CampusId = blr.Id, AcademicYearId = year.Id, GradeLevelId = grades[i % 3].Id, Stage = a.Item3, Source = i % 3 == 0 ? "walk-in" : "web", FirstName = a.Item1, LastName = a.Item2,
                DateOfBirth = new DateOnly(2016, 1 + i, 10), GuardianName = $"Parent of {a.Item1}", GuardianEmail = $"{a.Item1.ToLower()}.family@example.com", GuardianPhone = $"+9197000000{i:D2}",
                FormDataJson = """{"previousSchool":"Sunrise Primary","homeLanguage":"Kannada"}"""
            });

        // Communication
        db.Add(new Announcement { CampusId = blr.Id, Title = "Parent-teacher meeting on Saturday", Body = "Term 1 report cards will be discussed. Slots are 15 minutes; please arrive on time.", AudienceType = "campus", Channels = "inapp,email", SentByName = "Dr. Kavita Rao", RecipientCount = 16 });
        db.Add(new ConsentForm { CampusId = blr.Id, Title = "Science museum field trip", Purpose = "Off-campus trip and bus travel", Body = "Grade 5 will visit the Visvesvaraya Museum. Students travel by school bus with four teachers. I give permission for my child to take part.", AudienceType = "grade", AudienceId = grades[1].Id, DueDate = today.AddDays(10) });
        db.Add(new ConsentForm { CampusId = blr.Id, Title = "Photography and media", Purpose = "Use of photographs in the school newsletter", Body = "I allow the school to use photographs of my child in the printed newsletter. Photographs are never used for advertising.", AudienceType = "campus" });
        var thread = new MessageThread { StudentId = allStudents[0].Id, StaffUserId = teacherUser.Id, GuardianUserId = demoParent.Id, Subject = "Reading practice at home", LastMessageAt = DateTime.UtcNow.AddDays(-1) };
        db.Add(thread);
        db.Add(new Message { ThreadId = thread.Id, SenderUserId = teacherUser.Id, SenderName = teacherUser.DisplayName, Body = "Aarav is doing well. Fifteen minutes of reading aloud each evening would help his fluency." });
        db.Add(new Message { ThreadId = thread.Id, SenderUserId = demoParent.Id, SenderName = demoParent.DisplayName, Body = "Thank you, we will start tonight. Could you suggest a book list?" });
        await db.SaveChangesAsync();

        // ---------- Campus 2: Dubai, IB, Arabic (right-to-left) ----------
        var dxb = new Campus { Name = "Greenfield Dubai", Country = "AE", Currency = "AED", TimeZone = "Asia/Dubai", Locale = "ar-AE", Curriculum = "IB" };
        db.Add(dxb);
        var dYear = new AcademicYear { CampusId = dxb.Id, Name = "2026-27", StartDate = new(2026, 9, 1), EndDate = new(2027, 6, 30), Status = "active" };
        var dTerm = new Term { AcademicYearId = dYear.Id, Name = "الفصل الأول", Order = 1, StartDate = new(2026, 9, 1), EndDate = new(2026, 12, 18) };
        var dGrade = new GradeLevel { CampusId = dxb.Id, Name = "MYP 2", Order = 7 };
        var dSection = new Section { AcademicYearId = dYear.Id, GradeLevelId = dGrade.Id, Name = "A" };
        db.AddRange(dYear, dTerm, dGrade, dSection);
        db.AddRange(codes.Select(c => new AbsenceCode { CampusId = dxb.Id, Code = c.Item1, Name = c.Item2, Category = c.Item3, NotifyParents = c.Item4 }));
        var dSubjects = new[] { ("ARA", "اللغة العربية"), ("MAT", "الرياضيات"), ("SCI", "العلوم") }.Select(s => new Subject { CampusId = dxb.Id, Code = s.Item1, Name = s.Item2 }).ToList();
        db.AddRange(dSubjects);
        var ib = Scheme("ib", dxb.Id, dYear.Id);
        var dStudents = new[] { ("Omar", "Al Mansoori", "عمر المنصوري"), ("Layla", "Hassan", "ليلى حسن"), ("Yusuf", "Al Farsi", "يوسف الفارسي"), ("Mariam", "Saeed", "مريم سعيد") };
        var dEnrollments = new List<Enrollment>();
        foreach (var (n, i) in dStudents.Select((n, i) => (n, i)))
        {
            var s = new Student { CampusId = dxb.Id, AdmissionNumber = $"GD-2026-{i + 1:D4}", FirstName = n.Item1, LastName = n.Item2, NativeName = n.Item3, DateOfBirth = new DateOnly(2013, 2 + i, 12) };
            db.Add(s);
            var guardian = await svc.FindOrCreateGuardian($"Parent {n.Item2}", $"parent.{n.Item1.ToLower()}@example.ae", $"+97150000{i:D4}");
            db.Add(new StudentGuardian { StudentId = s.Id, GuardianId = guardian.Id, IsPrimary = true });
            dEnrollments.Add(await svc.Enroll(s, dSection));
            await db.SaveChangesAsync();
        }
        foreach (var sub in dSubjects)
        {
            var cs = new ClassSubject { AcademicYearId = dYear.Id, SectionId = dSection.Id, SubjectId = sub.Id };
            var plan = new AssessmentPlan { AcademicYearId = dYear.Id, TermId = dTerm.Id, ClassSubjectId = cs.Id, SectionId = dSection.Id, GradingSchemeId = ib.Id, Name = sub.Name, Status = PlanStatus.Published, ApprovedByName = "Meera Nair", ApprovedAt = DateTime.UtcNow, PublishedAt = DateTime.UtcNow };
            var comps = new[] { ("المعيار أ", 50m, 8m), ("المعيار ب", 50m, 8m) }.Select((c, n) => new Assessment { AssessmentPlanId = plan.Id, Name = c.Item1, Weight = c.Item2, MaxMarks = c.Item3, Order = n }).ToList();
            db.AddRange(cs, plan); db.AddRange(comps);
            foreach (var e in dEnrollments) foreach (var c in comps) db.Add(new Score { AssessmentId = c.Id, EnrollmentId = e.Id, Marks = rng.Next(3, 9) });
        }
        await db.SaveChangesAsync();

        // Last month's payroll for Bengaluru, already approved and paid.
        cu.TenantId = tenant.Id;
        var run = new SchoolErp.Api.Modules.Payroll.PayrollRun { CampusId = blr.Id, Month = $"{lastMonth:yyyy-MM}", Currency = "INR", Status = "paid", ApprovedByName = "Joseph Mathew", ApprovedAt = DateTime.UtcNow.AddDays(-12), PaidAt = DateTime.UtcNow.AddDays(-11) };
        db.Add(run);
        await SchoolErp.Api.Modules.Payroll.PayrollEndpoints.Calculate(db, run);
        await db.SaveChangesAsync();
        var lop = await db.Payslips.Where(p => p.PayrollRunId == run.Id).SumAsync(p => p.UnpaidLeaveDeduction);
        fees.Post(blr.Id, "INR", $"Payroll {run.Month}", "payroll", run.Id, ("6000 Salaries expense", run.Gross - lop, 0), ("2200 Salaries payable", 0, run.Net), ("2210 Payroll deductions payable", 0, run.Deductions - lop));
        fees.Post(blr.Id, "INR", $"Salary payments {run.Month}", "payroll-payment", run.Id, ("2200 Salaries payable", run.Net, 0), ("1000 Bank", 0, run.Net));
        await db.SaveChangesAsync();

        // Seeded notices are history, not mail to send on first start.
        foreach (var n in await db.Notifications.IgnoreQueryFilters().ToListAsync()) { n.Status = "sent"; n.SentAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }
}
