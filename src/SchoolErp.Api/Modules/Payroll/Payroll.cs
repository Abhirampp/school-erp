using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Payroll;

/// <summary>One line of a staff member's salary structure.</summary>
public class PayComponent : TenantEntity
{
    public Guid StaffId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>earning or deduction</summary>
    public string Kind { get; set; } = "earning";
    /// <summary>fixed (an amount per month) or percent (of the component named "Basic")</summary>
    public string Type { get; set; } = "fixed";
    public decimal Amount { get; set; }
    public int Order { get; set; }
}

/// <summary>A month's payroll for one campus: draft, then approved (expense posted), then paid (bank posted).</summary>
public class PayrollRun : TenantEntity
{
    public Guid CampusId { get; set; }
    /// <summary>yyyy-MM</summary>
    public string Month { get; set; } = "";
    /// <summary>draft, approved, paid</summary>
    public string Status { get; set; } = "draft";
    public string Currency { get; set; } = "INR";
    public int StaffCount { get; set; }
    public decimal Gross { get; set; }
    public decimal Deductions { get; set; }
    public decimal Net { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PaidAt { get; set; }
}

public class Payslip : TenantEntity
{
    public Guid PayrollRunId { get; set; }
    public Guid StaffId { get; set; }
    public string Month { get; set; } = "";
    public string StaffName { get; set; } = "";
    public string EmployeeNumber { get; set; } = "";
    public string? Designation { get; set; }
    public int DaysInMonth { get; set; }
    public decimal UnpaidLeaveDays { get; set; }
    public decimal UnpaidLeaveDeduction { get; set; }
    public decimal Gross { get; set; }
    public decimal Deductions { get; set; }
    public decimal Net { get; set; }
    /// <summary>[{name, kind, amount}]</summary>
    public string LinesJson { get; set; } = "[]";
}

public record PayRule(string Name, string Kind, string Type, decimal Amount);
public record PayLine(string Name, string Kind, decimal Amount);
public record PayResult(List<PayLine> Lines, decimal Gross, decimal Deductions, decimal Net, decimal UnpaidLeaveDeduction);

/// <summary>Pure salary arithmetic, kept free of the database so it can be unit tested.</summary>
public static class PayrollCalculator
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Earnings and deductions are fixed amounts or a percentage of the earning named "Basic".
    /// Unpaid leave removes gross pay pro rata to calendar days. Net pay never goes below zero.
    /// </summary>
    public static PayResult Compute(IReadOnlyList<PayRule> rules, int daysInMonth, decimal unpaidDays)
    {
        var basic = rules.Where(r => r.Kind == "earning" && r.Type == "fixed" && r.Name.Trim().Equals("Basic", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Amount);
        decimal Value(PayRule r) => R(r.Type == "percent" ? basic * r.Amount / 100 : r.Amount);

        var lines = rules.Where(r => r.Kind == "earning").Select(r => new PayLine(r.Name, "earning", Value(r))).ToList();
        var gross = lines.Sum(l => l.Amount);
        decimal lop = 0;
        if (unpaidDays > 0 && daysInMonth > 0)
        {
            lop = Math.Min(gross, R(gross * Math.Min(unpaidDays, daysInMonth) / daysInMonth));
            lines.Add(new PayLine($"Unpaid leave ({unpaidDays:0.#} days)", "deduction", lop));
        }
        foreach (var r in rules.Where(r => r.Kind == "deduction")) lines.Add(new PayLine(r.Name, "deduction", Value(r)));
        var deductions = lines.Where(l => l.Kind == "deduction").Sum(l => l.Amount);
        // Never pay a negative salary: total deductions are capped at gross pay.
        if (deductions > gross) deductions = gross;
        return new PayResult(lines, gross, deductions, gross - deductions, lop);
    }

    /// <summary>Calendar days of approved unpaid leave that fall inside the month.</summary>
    public static int UnpaidDaysIn(DateOnly monthStart, IEnumerable<(DateOnly From, DateOnly To)> leave)
    {
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var days = new HashSet<DateOnly>();
        foreach (var (from, to) in leave)
            for (var d = from > monthStart ? from : monthStart; d <= (to < monthEnd ? to : monthEnd); d = d.AddDays(1)) days.Add(d);
        return days.Count;
    }
}

public static class PayrollAccounts
{
    public const string Expense = "6000 Salaries expense", Payable = "2200 Salaries payable", DeductionsPayable = "2210 Payroll deductions payable";
}

public static class PayrollEndpoints
{
    public record RunRequest(Guid CampusId, string Month);
    public record BankDetails(string? BankName, string? BankAccount, string? TaxId);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static bool TryMonth(string month, out DateOnly start) =>
        DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start);

    /// <summary>(Re)builds every payslip in a draft run from current salary structures and approved unpaid leave.</summary>
    public static async Task Calculate(AppDbContext db, PayrollRun run)
    {
        TryMonth(run.Month, out var start);
        var days = DateTime.DaysInMonth(start.Year, start.Month);
        var end = start.AddMonths(1).AddDays(-1);
        db.RemoveRange(await db.Payslips.Where(p => p.PayrollRunId == run.Id).ToListAsync());
        var staff = await db.Staff.Where(s => s.CampusId == run.CampusId && s.Status == "active").OrderBy(s => s.EmployeeNumber).ToListAsync();
        var staffIds = staff.Select(s => s.Id).ToList();
        var components = await db.PayComponents.Where(c => staffIds.Contains(c.StaffId)).OrderBy(c => c.Order).ToListAsync();
        var leave = await db.LeaveRequests.Where(l => staffIds.Contains(l.StaffId) && l.Unpaid && l.Status == "approved" && l.FromDate <= end && l.ToDate >= start).ToListAsync();
        decimal gross = 0, deductions = 0, net = 0; var count = 0;
        foreach (var s in staff)
        {
            var rules = components.Where(c => c.StaffId == s.Id).Select(c => new PayRule(c.Name, c.Kind, c.Type, c.Amount)).ToList();
            if (rules.Count == 0) continue; // no salary structure yet
            var unpaid = PayrollCalculator.UnpaidDaysIn(start, leave.Where(l => l.StaffId == s.Id).Select(l => (l.FromDate, l.ToDate)));
            var r = PayrollCalculator.Compute(rules, days, unpaid);
            db.Add(new Payslip
            {
                PayrollRunId = run.Id, StaffId = s.Id, Month = run.Month, StaffName = s.FullName(), EmployeeNumber = s.EmployeeNumber, Designation = s.Designation,
                DaysInMonth = days, UnpaidLeaveDays = unpaid, UnpaidLeaveDeduction = r.UnpaidLeaveDeduction, Gross = r.Gross, Deductions = r.Deductions, Net = r.Net,
                LinesJson = JsonSerializer.Serialize(r.Lines, Json)
            });
            gross += r.Gross; deductions += r.Deductions; net += r.Net; count++;
        }
        run.Gross = gross; run.Deductions = deductions; run.Net = net; run.StaffCount = count;
    }

    private static object Slip(Payslip p) => new
    {
        p.Id, p.PayrollRunId, p.StaffId, p.Month, p.StaffName, p.EmployeeNumber, p.Designation, p.DaysInMonth, p.UnpaidLeaveDays, p.UnpaidLeaveDeduction,
        p.Gross, p.Deductions, p.Net, lines = JsonSerializer.Deserialize<List<PayLine>>(p.LinesJson, Json)
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapCrud<PayComponent>("/api/pay-components", Perm.Payroll, Perm.Payroll);

        var g = app.MapGroup("/api/payroll");

        g.MapGet("runs", async (Guid campusId, AppDbContext db, CurrentUser user) =>
            !user.CanAccessCampus(campusId) ? EndpointExtensions.Forbidden() : Results.Ok(await db.PayrollRuns.Where(r => r.CampusId == campusId).OrderByDescending(r => r.Month).ToListAsync())).Require(Perm.Payroll);

        g.MapPost("runs", async (RunRequest req, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(req.CampusId)) return EndpointExtensions.Forbidden();
            if (!TryMonth(req.Month, out _)) return EndpointExtensions.Bad("Month must look like 2026-10.");
            if (await db.PayrollRuns.AnyAsync(r => r.CampusId == req.CampusId && r.Month == req.Month)) return EndpointExtensions.Bad("There is already a payroll run for that month.");
            var campus = await db.Campuses.FirstAsync(c => c.Id == req.CampusId);
            var run = new PayrollRun { CampusId = req.CampusId, Month = req.Month, Currency = campus.Currency };
            db.Add(run);
            await Calculate(db, run);
            if (run.StaffCount == 0) return EndpointExtensions.Bad("No staff at this campus have a salary structure yet. Add pay components on a staff member's page first.");
            await db.SaveChangesAsync();
            return Results.Ok(run);
        }).Require(Perm.Payroll);

        g.MapGet("runs/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            var slips = await db.Payslips.Where(p => p.PayrollRunId == id).OrderBy(p => p.EmployeeNumber).ToListAsync();
            return Results.Ok(new { run, payslips = slips.Select(Slip) });
        }).Require(Perm.Payroll);

        g.MapPost("runs/{id:guid}/recalculate", async (Guid id, AppDbContext db) =>
        {
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            if (run.Status != "draft") return EndpointExtensions.Bad("Only a draft run can be recalculated.");
            await Calculate(db, run);
            await db.SaveChangesAsync();
            return Results.Ok(run);
        }).Require(Perm.Payroll);

        g.MapDelete("runs/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            if (run.Status != "draft") return EndpointExtensions.Bad("Only a draft run can be deleted.");
            db.RemoveRange(await db.Payslips.Where(p => p.PayrollRunId == id).ToListAsync());
            db.Remove(run);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Require(Perm.Payroll);

        // Approval books the cost: Dr salaries expense, Cr net pay owed to staff and deductions owed to authorities.
        g.MapPost("runs/{id:guid}/approve", async (Guid id, AppDbContext db, FeeService ledger, Notifier notifier, CurrentUser user) =>
        {
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            if (run.Status != "draft") return EndpointExtensions.Bad("This run has already been approved.");
            var slips = await db.Payslips.Where(p => p.PayrollRunId == id).ToListAsync();
            var lop = slips.Sum(s => s.UnpaidLeaveDeduction);
            ledger.Post(run.CampusId, run.Currency, $"Payroll {run.Month}", "payroll", run.Id,
                (PayrollAccounts.Expense, run.Gross - lop, 0), (PayrollAccounts.Payable, 0, run.Net), (PayrollAccounts.DeductionsPayable, 0, run.Deductions - lop));
            run.Status = "approved"; run.ApprovedByName = user.Name; run.ApprovedAt = DateTime.UtcNow;
            var staffIds = slips.Select(s => s.StaffId).ToList();
            foreach (var u in await db.Users.Where(u => u.StaffId != null).ToListAsync())
                if (staffIds.Contains(u.StaffId!.Value)) notifier.ToUser(u.Id, $"Payslip for {run.Month}", "Your payslip is ready. Open Payroll → My payslips.", "payroll");
            await db.SaveChangesAsync();
            return Results.Ok(run);
        }).Require(Perm.Payroll);

        g.MapPost("runs/{id:guid}/pay", async (Guid id, AppDbContext db, FeeService ledger) =>
        {
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            if (run.Status != "approved") return EndpointExtensions.Bad("Approve the run before marking it paid.");
            ledger.Post(run.CampusId, run.Currency, $"Salary payments {run.Month}", "payroll-payment", run.Id, (PayrollAccounts.Payable, run.Net, 0), (Accounts.Bank, 0, run.Net));
            run.Status = "paid"; run.PaidAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(run);
        }).Require(Perm.Payroll);

        // Bank transfer file for the run. Contains account numbers, so it is audited.
        g.MapGet("runs/{id:guid}/bank-file", async (Guid id, AppDbContext db) =>
        {
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            var slips = await db.Payslips.Where(p => p.PayrollRunId == id).OrderBy(p => p.EmployeeNumber).ToListAsync();
            var ids = slips.Select(s => s.StaffId).ToList();
            var staff = await db.Staff.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
            db.Audit("export", "PayrollBankFile", id.ToString(), new { run.Month, rows = slips.Count });
            await db.SaveChangesAsync();
            var csv = Csv.Write(["EmployeeNumber", "Name", "BankName", "AccountNumber", "Amount", "Currency", "Reference"],
                slips.Select(p => new object?[] { p.EmployeeNumber, p.StaffName, staff.GetValueOrDefault(p.StaffId)?.BankName, staff.GetValueOrDefault(p.StaffId)?.BankAccount, p.Net, run.Currency, $"Salary {run.Month}" }));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"salaries-{run.Month}.csv");
        }).Require(Perm.Payroll);

        g.MapGet("payslips/{id:guid}/pdf", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var p = await db.Payslips.FirstOrDefaultAsync(x => x.Id == id);
            // Staff can download their own payslips once the run is approved; payroll officers can download any.
            if (p is null || (!user.Has(Perm.Payroll) && p.StaffId != user.StaffId)) return Results.NotFound();
            var run = await db.PayrollRuns.FirstAsync(r => r.Id == p.PayrollRunId);
            if (!user.Has(Perm.Payroll) && run.Status == "draft") return Results.NotFound();
            var campus = await db.Campuses.FirstAsync(c => c.Id == run.CampusId);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == db.CurrentTenantId);
            var lines = JsonSerializer.Deserialize<List<PayLine>>(p.LinesJson, Json) ?? [];
            return Results.File(Pdf.Payslip(tenant.Name, campus, p, run.Currency, lines), "application/pdf", $"payslip-{p.EmployeeNumber}-{p.Month}.pdf");
        }).RequireStaff();

        g.MapGet("my-payslips", async (AppDbContext db, CurrentUser user) =>
        {
            if (user.StaffId is not { } staffId) return Results.Ok(Array.Empty<object>());
            var released = await db.PayrollRuns.Where(r => r.Status != "draft").Select(r => new { r.Id, r.Currency, r.Status }).ToListAsync();
            var ids = released.Select(r => r.Id).ToList();
            var slips = await db.Payslips.Where(p => p.StaffId == staffId && ids.Contains(p.PayrollRunId)).OrderByDescending(p => p.Month).ToListAsync();
            return Results.Ok(slips.Select(p => new { slip = Slip(p), currency = released.First(r => r.Id == p.PayrollRunId).Currency, status = released.First(r => r.Id == p.PayrollRunId).Status }));
        }).RequireStaff();

        app.MapPut("/api/staff/{id:guid}/bank", async (Guid id, BankDetails req, AppDbContext db) =>
        {
            var s = await db.Staff.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            s.BankName = req.BankName; s.BankAccount = req.BankAccount; s.TaxId = req.TaxId;
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.Payroll);
    }
}
