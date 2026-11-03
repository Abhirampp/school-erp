using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Assistant;
using SchoolErp.Api.Modules.Attendance;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Tests;

public class AssistantToolTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoArgs = new Dictionary<string, JsonElement>();
    private static IReadOnlyDictionary<string, JsonElement> Args(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>One campus, one class of two students, one teacher, attendance and an overdue invoice.</summary>
    private static async Task<(TestDb test, Guid tenant, Guid staffId)> Seed()
    {
        var test = new TestDb();
        var tenant = Guid.NewGuid();
        using var db = test.For(tenant);
        var campus = new Campus { Name = "Main", TimeZone = "UTC", Currency = "INR" };
        var year = new AcademicYear { CampusId = campus.Id, Name = "2026-27", StartDate = new(2026, 4, 1), EndDate = new(2027, 3, 31), Status = "active" };
        var grade = new GradeLevel { CampusId = campus.Id, Name = "Grade 5", Order = 5 };
        var teacher = new Staff { CampusId = campus.Id, EmployeeNumber = "E1", FirstName = "Asha", LastName = "Verma", Designation = "English teacher" };
        var section = new Section { AcademicYearId = year.Id, GradeLevelId = grade.Id, Name = "A", ClassTeacherId = teacher.Id };
        var riya = new Student { CampusId = campus.Id, AdmissionNumber = "A1", FirstName = "Riya", LastName = "Shah", MedicalNotes = "Asthma" };
        var dev = new Student { CampusId = campus.Id, AdmissionNumber = "A2", FirstName = "Dev", LastName = "Rao" };
        var e1 = new Enrollment { StudentId = riya.Id, SectionId = section.Id, AcademicYearId = year.Id, RollNumber = 1 };
        var e2 = new Enrollment { StudentId = dev.Id, SectionId = section.Id, AcademicYearId = year.Id, RollNumber = 2 };
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.AddRange(campus, year, grade, teacher, section, riya, dev, e1, e2,
            new AttendanceRecord { EnrollmentId = e1.Id, StudentId = riya.Id, SectionId = section.Id, Date = today, Code = "A", Category = "unauthorized" },
            new AttendanceRecord { EnrollmentId = e2.Id, StudentId = dev.Id, SectionId = section.Id, Date = today, Code = "P", Category = "present" },
            new Invoice { StudentId = riya.Id, CampusId = campus.Id, AcademicYearId = year.Id, Number = "INV-1", Total = 5000, PaidTotal = 1000, Status = "partial", DueDate = today.AddDays(-10), Currency = "INR" });
        await db.SaveChangesAsync();
        return (test, tenant, teacher.Id);
    }

    private static AssistantTools As(TestDb test, Guid tenant, string scope, Guid? staffId, params string[] perms)
    {
        var user = new CurrentUser { TenantId = tenant, UserId = Guid.NewGuid(), Name = "tester", Scope = scope, StaffId = staffId, Permissions = [.. perms] };
        return new AssistantTools(new SchoolErp.Api.Data.AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<SchoolErp.Api.Data.AppDbContext>()
            .UseSqlite(test.Connection).Options, user), user);
    }

    [Fact]
    public void Every_tool_has_a_valid_object_schema()
    {
        foreach (var d in AssistantTools.Definitions)
        {
            using var doc = JsonDocument.Parse(d.SchemaJson);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            var props = doc.RootElement.GetProperty("properties");
            Assert.All(doc.RootElement.GetProperty("required").EnumerateArray(), r => Assert.True(props.TryGetProperty(r.GetString()!, out _), $"{d.Name}: required '{r}' is not a property"));
        }
    }

    [Fact]
    public async Task Class_names_are_matched_loosely_and_attendance_lists_absentees()
    {
        var (test, tenant, _) = await Seed();
        using var _t = test;
        var tools = As(test, tenant, Scopes.Tenant, null, Perm.AttendanceRead, Perm.StudentsRead);
        var (json, isError) = await tools.Run("attendance_report", Args(new { class_name = "5a" }));
        Assert.False(isError, json);
        Assert.Contains("\"unauthorisedAbsence\":1", json);
        Assert.Contains("Riya Shah", json);
    }

    [Fact]
    public async Task Fees_need_the_fee_permission()
    {
        var (test, tenant, _) = await Seed();
        using var _t = test;
        var (denied, isError) = await As(test, tenant, Scopes.Tenant, null, Perm.StudentsRead).Run("fee_report", NoArgs);
        Assert.True(isError);
        Assert.Contains("permission", denied);
        var (json, ok) = await As(test, tenant, Scopes.Tenant, null, Perm.FeesRead).Run("fee_report", Args(new { overdue_only = true }));
        Assert.False(ok, json);
        Assert.Contains("\"outstanding\":4000", json);
        Assert.Contains("Riya Shah", json);
    }

    [Fact]
    public async Task Medical_notes_only_reach_roles_allowed_to_see_them()
    {
        var (test, tenant, _) = await Seed();
        using var _t = test;
        var (plain, _) = await As(test, tenant, Scopes.Tenant, null, Perm.StudentsRead).Run("student_details", Args(new { student = "Riya" }));
        Assert.DoesNotContain("Asthma", plain);
        var (medical, _) = await As(test, tenant, Scopes.Tenant, null, Perm.StudentsRead, Perm.StudentsMedical).Run("student_details", Args(new { student = "A1" }));
        Assert.Contains("Asthma", medical);
    }

    [Fact]
    public async Task A_teacher_only_sees_classes_they_teach()
    {
        var (test, tenant, teacherId) = await Seed();
        using var _t = test;
        var (own, ownError) = await As(test, tenant, Scopes.Assigned, teacherId, Perm.StudentsRead).Run("class_details", Args(new { class_name = "Grade 5 A" }));
        Assert.False(ownError, own);
        var (other, otherError) = await As(test, tenant, Scopes.Assigned, Guid.NewGuid(), Perm.StudentsRead).Run("class_details", Args(new { class_name = "Grade 5 A" }));
        Assert.True(otherError, other);
    }

    [Fact]
    public async Task Another_tenant_sees_nothing()
    {
        var (test, _, _) = await Seed();
        using var _t = test;
        var (json, isError) = await As(test, Guid.NewGuid(), Scopes.Tenant, null, Perm.StudentsRead).Run("find_students", NoArgs);
        Assert.False(isError, json);
        Assert.Contains("\"count\":0", json);
    }
}
