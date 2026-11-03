using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Modules.Rollover;
using SchoolErp.Api.Modules.Security;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Shared;

namespace SchoolErp.Tests;

/// <summary>A real (in-memory SQLite) database shared by contexts acting as different tenants.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    public SqliteConnection Connection => _connection;
    public TestDb()
    {
        _connection.Open();
        using var db = For(Guid.Empty);
        db.Database.EnsureCreated();
    }
    public AppDbContext For(Guid tenantId, string name = "tester") =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options, new CurrentUser { TenantId = tenantId, UserId = Guid.NewGuid(), Name = name });
    public void Dispose() => _connection.Dispose();
}

public class TenantIsolationTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static readonly Type[] ControlPlane = [typeof(Tenant), typeof(AuditEvent), typeof(SupportAccessRequest)];

    [Fact]
    public void Every_table_except_the_control_plane_is_tenant_owned_and_filtered()
    {
        using var test = new TestDb();
        using var db = test.For(A);
        foreach (var type in db.Model.GetEntityTypes())
        {
            if (ControlPlane.Contains(type.ClrType)) continue;
            Assert.True(typeof(TenantEntity).IsAssignableFrom(type.ClrType), $"{type.ClrType.Name} must derive from TenantEntity");
            Assert.True(type.GetDeclaredQueryFilters().Count > 0, $"{type.ClrType.Name} has no tenant query filter");
        }
    }

    /// <summary>The cross-tenant leak test (NFR-SEC-02): for every tenant-owned table, tenant A can never read tenant B's row.</summary>
    [Fact]
    public async Task No_table_leaks_rows_across_tenants()
    {
        using var test = new TestDb();
        List<Type> types;
        using (var db = test.For(A)) types = db.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(TenantEntity).IsAssignableFrom(t)).ToList();
        Assert.True(types.Count > 40);

        foreach (var tenant in new[] { A, B })
        {
            using var db = test.For(tenant);
            foreach (var type in types) db.Add(Activator.CreateInstance(type)!);
            await db.SaveChangesAsync();
        }

        using var asA = test.For(A);
        foreach (var type in types)
        {
            var rows = await Query(asA, type);
            Assert.True(rows.Count == 1, $"{type.Name}: tenant A sees {rows.Count} rows, expected 1");
            Assert.All(rows, r => Assert.Equal(A, r.TenantId));
        }
        Assert.All(await asA.AuditEvents.Where(a => a.TenantId == asA.CurrentTenantId).ToListAsync(), a => Assert.Equal(A, a.TenantId));
    }

    private static async Task<List<TenantEntity>> Query(AppDbContext db, Type type)
    {
        var method = typeof(TenantIsolationTests).GetMethod(nameof(All), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(type);
        return await (Task<List<TenantEntity>>)method.Invoke(null, [db])!;
    }
    private static async Task<List<TenantEntity>> All<T>(AppDbContext db) where T : TenantEntity => (await db.Set<T>().ToListAsync()).Cast<TenantEntity>().ToList();

    [Fact]
    public async Task Tenant_id_comes_from_the_caller_and_cannot_be_forged_or_changed()
    {
        using var test = new TestDb();
        Guid id;
        using (var db = test.For(A))
        {
            var campus = new Campus { Name = "Forged", TenantId = B }; // payload claims tenant B
            db.Add(campus);
            await db.SaveChangesAsync();
            id = campus.Id;
            Assert.Equal(A, campus.TenantId);

            campus.TenantId = B; campus.Name = "Moved";
            await db.SaveChangesAsync();
        }
        using var asB = test.For(B);
        Assert.Empty(await asB.Campuses.ToListAsync());
        using var asA = test.For(A);
        Assert.Equal(A, (await asA.Campuses.IgnoreQueryFilters().SingleAsync(c => c.Id == id)).TenantId);
    }

    [Fact]
    public async Task Deletes_are_soft_and_every_change_is_audited()
    {
        using var test = new TestDb();
        using var db = test.For(A, "Meera");
        var s = new Student { FirstName = "Riya", MedicalNotes = "Peanut allergy" };
        db.Add(s);
        await db.SaveChangesAsync();
        s.FirstName = "Ria";
        await db.SaveChangesAsync();
        db.Remove(s);
        await db.SaveChangesAsync();

        Assert.Empty(await db.Students.ToListAsync());
        Assert.True((await db.Students.IgnoreQueryFilters().SingleAsync()).IsDeleted);
        var audit = await db.AuditEvents.OrderBy(a => a.At).ToListAsync();
        Assert.Equal(["create", "update", "delete"], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.Equal("Meera", a.ActorName));
        Assert.Contains("\"FirstName\":\"Riya\"", audit[1].BeforeJson);
        Assert.Contains("\"FirstName\":\"Ria\"", audit[1].AfterJson);
        // Sensitive values never reach the audit log.
        Assert.DoesNotContain("Peanut", audit[0].AfterJson);
    }

    [Fact]
    public async Task Medical_data_is_encrypted_in_the_database()
    {
        using var test = new TestDb();
        using var db = test.For(A);
        db.Add(new Student { FirstName = "Riya", MedicalNotes = "Peanut allergy", NationalId = "1234-5678" });
        await db.SaveChangesAsync();
        var raw = await db.Database.SqlQueryRaw<string>("SELECT MedicalNotes || '|' || NationalId AS Value FROM Students").SingleAsync();
        Assert.DoesNotContain("Peanut", raw);
        Assert.DoesNotContain("1234", raw);
        db.ChangeTracker.Clear();
        Assert.Equal("Peanut allergy", (await db.Students.SingleAsync()).MedicalNotes);
    }
}

public class RolloverTests
{
    [Fact]
    public async Task Rollover_promotes_graduates_clones_config_carries_balances_and_archives()
    {
        using var test = new TestDb();
        var tenant = Guid.NewGuid();
        using var db = test.For(tenant);
        var campus = new Campus { Name = "Main" };
        var year = new AcademicYear { CampusId = campus.Id, Name = "2026-27", StartDate = new(2026, 4, 1), EndDate = new(2027, 3, 31), Status = "active" };
        var g1 = new GradeLevel { CampusId = campus.Id, Name = "Grade 9", Order = 9 };
        var g2 = new GradeLevel { CampusId = campus.Id, Name = "Grade 10", Order = 10 };
        var s1 = new Section { AcademicYearId = year.Id, GradeLevelId = g1.Id, Name = "A" };
        var s2 = new Section { AcademicYearId = year.Id, GradeLevelId = g2.Id, Name = "A" };
        var junior = new Student { CampusId = campus.Id, FirstName = "Junior" };
        var senior = new Student { CampusId = campus.Id, FirstName = "Senior" };
        var leaver = new Student { CampusId = campus.Id, FirstName = "Leaver", Status = "withdrawn" };
        var structure = new FeeStructure { AcademicYearId = year.Id, GradeLevelId = g1.Id, Name = "G9", Currency = "INR" };
        db.AddRange(campus, year, g1, g2, s1, s2, junior, senior, leaver, structure,
            new Term { AcademicYearId = year.Id, Name = "Term 1", StartDate = new(2026, 4, 1), EndDate = new(2026, 9, 30) },
            new Enrollment { StudentId = junior.Id, SectionId = s1.Id, AcademicYearId = year.Id },
            new Enrollment { StudentId = senior.Id, SectionId = s2.Id, AcademicYearId = year.Id },
            new Enrollment { StudentId = leaver.Id, SectionId = s1.Id, AcademicYearId = year.Id, Status = "withdrawn" },
            new Installment { FeeStructureId = structure.Id, Name = "Full", Percent = 100, DueDate = new(2026, 4, 15) },
            new FeeStructureItem { FeeStructureId = structure.Id, Amount = 1000 },
            new GradingScheme { CampusId = campus.Id, AcademicYearId = year.Id, Name = "CBSE", Version = 1 },
            new GradingScheme { CampusId = campus.Id, AcademicYearId = year.Id, Name = "CBSE", Version = 2, DefinitionJson = "{\"passMark\":35}" },
            new Invoice { StudentId = junior.Id, CampusId = campus.Id, AcademicYearId = year.Id, Number = "INV-1", Subtotal = 1000, Total = 1000, PaidTotal = 400, Status = "partial", Currency = "INR" },
            new Invoice { StudentId = junior.Id, CampusId = campus.Id, AcademicYearId = year.Id, Number = "INV-2", Subtotal = 500, Total = 500, PaidTotal = 500, Status = "paid", Currency = "INR" });
        await db.SaveChangesAsync();

        var svc = new RolloverService(db);
        var preview = await svc.Preview(year.Id);
        Assert.Empty(preview!.Blockers);
        Assert.Equal(1, preview.Graduating);
        Assert.Equal(600m, preview.OutstandingBalance);

        var (result, error) = await svc.Run(new RolloverRequest(year.Id, "2027-28", new(2027, 4, 1), new(2028, 3, 31)));
        Assert.Null(error);
        Assert.Equal((1, 1, 1, 600m), (result!.Promoted, result.Graduated, result.BalancesCarried, result.AmountCarried));

        db.ChangeTracker.Clear();
        var years = await db.AcademicYears.ToDictionaryAsync(y => y.Name, y => y.Status);
        Assert.Equal("archived", years["2026-27"]);
        Assert.Equal("active", years["2027-28"]);

        // Junior moved up to Grade 10 A in the new year; the old enrollment is kept as history.
        var juniorEnrollments = await db.Enrollments.Where(e => e.StudentId == junior.Id).ToListAsync();
        var current = Assert.Single(juniorEnrollments, e => e.Status == "active");
        Assert.Equal(result.NewYearId, current.AcademicYearId);
        Assert.Equal(g2.Id, (await db.Sections.SingleAsync(s => s.Id == current.SectionId)).GradeLevelId);
        Assert.Single(juniorEnrollments, e => e.Status == "promoted");

        Assert.Equal("graduated", (await db.Students.SingleAsync(s => s.Id == senior.Id)).Status);
        Assert.DoesNotContain(await db.Enrollments.ToListAsync(), e => e.StudentId == leaver.Id && e.AcademicYearId == result.NewYearId);

        // Configuration cloned with dates moved a year on; only the latest scheme version is carried.
        Assert.Equal(2, await db.Sections.CountAsync(s => s.AcademicYearId == result.NewYearId));
        Assert.Equal(new DateOnly(2027, 4, 1), (await db.Terms.SingleAsync(t => t.AcademicYearId == result.NewYearId)).StartDate);
        var newStructure = await db.FeeStructures.SingleAsync(f => f.AcademicYearId == result.NewYearId);
        Assert.Equal(new DateOnly(2027, 4, 15), (await db.Installments.SingleAsync(i => i.FeeStructureId == newStructure.Id)).DueDate);
        var scheme = await db.GradingSchemes.SingleAsync(s => s.AcademicYearId == result.NewYearId);
        Assert.Equal((1, "{\"passMark\":35}"), (scheme.Version, scheme.DefinitionJson));

        // The unpaid 600 becomes one opening invoice; the paid invoice is untouched.
        var opening = await db.Invoices.SingleAsync(i => i.AcademicYearId == result.NewYearId);
        Assert.Equal((600m, "open", junior.Id), (opening.Total, opening.Status, opening.StudentId));
        Assert.Equal("carried", (await db.Invoices.SingleAsync(i => i.Number == "INV-1")).Status);
        Assert.Equal("paid", (await db.Invoices.SingleAsync(i => i.Number == "INV-2")).Status);

        // An archived year cannot be rolled over again.
        Assert.NotNull((await svc.Run(new RolloverRequest(year.Id, "2028-29", new(2028, 4, 1), new(2029, 3, 31)))).error);
    }
}
