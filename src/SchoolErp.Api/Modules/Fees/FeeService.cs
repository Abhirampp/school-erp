using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Modules.Students;

namespace SchoolErp.Api.Modules.Fees;

public static class Accounts
{
    public const string Receivable = "1100 Accounts receivable", Bank = "1000 Bank", Cash = "1010 Cash", Gateway = "1020 Payment gateway clearing",
        Discounts = "5000 Discounts and scholarships", FineIncome = "4100 Late fine income", Adjustments = "4900 Fee adjustments (credit notes)";
    public static string Income(string head) => $"4000 Fee income: {head}";
    public static string ForMethod(string method) => method switch { "cash" => Cash, "online" => Gateway, _ => Bank };
}

public class FeeService(AppDbContext db, Notifier notifier, StudentService students, IEnumerable<IPaymentProvider> providers, IConfiguration config)
{
    private int _seq;
    private async Task<string> NextNumber<T>(string prefix) where T : Shared.TenantEntity =>
        $"{prefix}-{DateTime.UtcNow.Year}-{await db.Set<T>().IgnoreQueryFilters().CountAsync(x => x.TenantId == db.CurrentTenantId) + 1 + _seq++:D5}";

    /// <summary>Posts one balanced double-entry transaction. Throws if debits and credits differ.</summary>
    public void Post(Guid campusId, string currency, string description, string refType, Guid refId, params (string Account, decimal Debit, decimal Credit)[] lines)
    {
        lines = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToArray();
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit)) throw new InvalidOperationException($"Unbalanced ledger posting for {refType} {refId}.");
        var tx = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var l in lines)
            db.Add(new LedgerEntry { CampusId = campusId, TransactionId = tx, Date = today, Account = l.Account, Debit = l.Debit, Credit = l.Credit, Currency = currency, Description = description, RefType = refType, RefId = refId });
    }

    /// <summary>
    /// Creates one invoice per installment for each enrolled student with a matching fee structure.
    /// Safe to run again: a student who already has the installment invoice is skipped.
    /// </summary>
    public async Task<(int created, int skipped, List<string> warnings)> GenerateInvoices(Guid academicYearId, Guid? gradeLevelId, Guid? studentId)
    {
        var warnings = new List<string>();
        var year = await db.AcademicYears.FirstAsync(y => y.Id == academicYearId);
        var structures = await db.FeeStructures.Where(s => s.AcademicYearId == academicYearId).ToListAsync();
        var structureIds = structures.Select(s => s.Id).ToList();
        var items = await db.FeeStructureItems.Where(i => structureIds.Contains(i.FeeStructureId)).ToListAsync();
        var installments = await db.Installments.Where(i => structureIds.Contains(i.FeeStructureId)).OrderBy(i => i.Order).ToListAsync();
        var heads = await db.FeeHeads.ToDictionaryAsync(h => h.Id, h => h.Name);
        var sections = await db.Sections.Where(s => s.AcademicYearId == academicYearId).ToDictionaryAsync(s => s.Id);
        var enrollments = await db.Enrollments.Where(e => e.AcademicYearId == academicYearId && e.Status == "active").ToListAsync();
        var allStudents = await db.Students.ToDictionaryAsync(s => s.Id);
        var links = await db.StudentGuardians.ToListAsync();
        var discounts = await db.Discounts.ToListAsync();
        var studentDiscounts = await db.StudentDiscounts.Where(d => d.AcademicYearId == academicYearId).ToListAsync();
        var existing = (await db.Invoices.Where(i => i.AcademicYearId == academicYearId && i.InstallmentId != null).Select(i => new { i.StudentId, i.InstallmentId }).ToListAsync())
            .Select(x => (x.StudentId, x.InstallmentId!.Value)).ToHashSet();
        var enrolledIds = enrollments.Select(e => e.StudentId).ToHashSet();
        int created = 0, skipped = 0;

        foreach (var e in enrollments)
        {
            if (studentId is not null && e.StudentId != studentId) continue;
            if (!sections.TryGetValue(e.SectionId, out var section) || (gradeLevelId is not null && section.GradeLevelId != gradeLevelId)) continue;
            var student = allStudents[e.StudentId];
            var structure = structures.FirstOrDefault(s => s.GradeLevelId == section.GradeLevelId && s.Category != null && s.Category == student.Category)
                            ?? structures.FirstOrDefault(s => s.GradeLevelId == section.GradeLevelId && s.Category == null);
            if (structure is null) { warnings.Add($"{student.FullName()}: no fee structure for their grade."); continue; }
            var myLinks = links.Where(l => l.StudentId == student.Id).ToList();
            var primary = myLinks.FirstOrDefault(l => l.IsPrimary) ?? myLinks.FirstOrDefault();
            if (primary is null) { warnings.Add($"{student.FullName()}: no guardian, so there is no fee account to bill."); continue; }
            var account = await students.EnsureFeeAccount(primary.GuardianId);

            // Sibling concession: applies to every child except the eldest enrolled sibling.
            var guardianIds = myLinks.Select(l => l.GuardianId).ToList();
            var siblings = links.Where(l => guardianIds.Contains(l.GuardianId) && l.StudentId != student.Id && enrolledIds.Contains(l.StudentId)).Select(l => allStudents[l.StudentId]).ToList();
            var isYounger = siblings.Any(s => (s.DateOfBirth ?? DateOnly.MinValue, s.Id).CompareTo((student.DateOfBirth ?? DateOnly.MinValue, student.Id)) < 0);
            var rules = discounts.Where(d => studentDiscounts.Any(sd => sd.StudentId == student.Id && sd.DiscountId == d.Id) || (d.AutoApplySibling && isYounger))
                .Select(d => new DiscountRule(d.Name, d.Type, d.Value)).ToList();

            var feeItems = items.Where(i => i.FeeStructureId == structure.Id).Select(i => new FeeItem(i.FeeHeadId, heads.GetValueOrDefault(i.FeeHeadId, "Fee"), i.Amount)).ToList();
            var plan = installments.Where(i => i.FeeStructureId == structure.Id).ToList();
            var percents = plan.Select(p => p.Percent).ToList();
            for (var n = 0; n < plan.Count; n++)
            {
                if (existing.Contains((student.Id, plan[n].Id))) { skipped++; continue; }
                var lines = FeeCalculator.BuildInstallment(feeItems, percents, n, rules);
                var invoice = new Invoice
                {
                    FeeAccountId = account.Id, StudentId = student.Id, CampusId = year.CampusId, AcademicYearId = academicYearId, InstallmentId = plan[n].Id,
                    Number = await NextNumber<Invoice>("INV"), Title = $"{structure.Name}: {plan[n].Name}", IssueDate = DateOnly.FromDateTime(DateTime.UtcNow), DueDate = plan[n].DueDate, Currency = structure.Currency,
                    Subtotal = lines.Where(l => l.Kind == "fee").Sum(l => l.Amount), DiscountTotal = -lines.Where(l => l.Kind == "discount").Sum(l => l.Amount)
                };
                FeeCalculator.Recalculate(invoice);
                db.Add(invoice);
                foreach (var l in lines) db.Add(new InvoiceLine { InvoiceId = invoice.Id, FeeHeadId = l.HeadId, Kind = l.Kind, Description = l.Description, Amount = l.Amount });
                Post(year.CampusId, invoice.Currency, $"Invoice {invoice.Number}", "invoice", invoice.Id,
                    [(Accounts.Receivable, invoice.Total, 0), (Accounts.Discounts, invoice.DiscountTotal, 0), .. lines.Where(l => l.Kind == "fee").Select(l => (Accounts.Income(l.Description), 0m, l.Amount))]);
                created++;
            }
        }
        await db.SaveChangesAsync();
        return (created, skipped, warnings);
    }

    /// <summary>Marks a payment as received: receipt number, invoice totals, ledger posting, receipt notice.</summary>
    public async Task Settle(Payment p, Invoice invoice)
    {
        if (p.Status == "succeeded") return; // gateways retry webhooks; settle once
        p.Status = "succeeded"; p.PaidAt = DateTime.UtcNow; p.ReceiptNumber = await NextNumber<Payment>("RCT");
        invoice.PaidTotal += p.Amount;
        FeeCalculator.Recalculate(invoice);
        Post(invoice.CampusId, invoice.Currency, $"Receipt {p.ReceiptNumber} for {invoice.Number}", "payment", p.Id, (Accounts.ForMethod(p.Method), p.Amount, 0), (Accounts.Receivable, 0, p.Amount));
        await notifier.ToGuardiansOf([invoice.StudentId], "Payment received", $"We received {p.Amount:0.00} {p.Currency} for invoice {invoice.Number}. Receipt {p.ReceiptNumber}.", "fees", "inapp", "email");
    }

    public IPaymentProvider Provider(string? name = null) =>
        providers.FirstOrDefault(x => x.Name == (name ?? config["Payments:Provider"] ?? "mock")) ?? throw new InvalidOperationException("No payment provider is configured.");

    public async Task<(string? url, string? error)> StartOnlinePayment(Invoice invoice)
    {
        var balance = invoice.Total - invoice.PaidTotal;
        if (invoice.Status is "void" or "carried" || balance <= 0) return (null, "This invoice has nothing left to pay.");
        var provider = Provider();
        var p = new Payment { InvoiceId = invoice.Id, Amount = balance, Currency = invoice.Currency, Method = "online", Gateway = provider.Name };
        var order = await provider.CreateOrder(p.Id, balance, invoice.Currency, $"Invoice {invoice.Number}");
        p.GatewayReference = order.Reference;
        db.Add(p);
        await db.SaveChangesAsync();
        return (order.HostedPageUrl, null);
    }

    public async Task<int> ApplyLateFines(Guid campusId)
    {
        var campus = await db.Campuses.FirstAsync(c => c.Id == campusId);
        if (campus.LateFinePercent <= 0 && campus.LateFineFixed <= 0) return 0;
        var cutoff = Shared.Clock.Today(campus.TimeZone).AddDays(-campus.LateFineGraceDays);
        var overdue = await db.Invoices.Where(i => i.CampusId == campusId && !i.FineApplied && i.DueDate < cutoff && (i.Status == "open" || i.Status == "partial")).ToListAsync();
        foreach (var i in overdue)
        {
            var fine = FeeCalculator.LateFine(i.Total - i.PaidTotal, campus.LateFinePercent, campus.LateFineFixed);
            if (fine <= 0) continue;
            i.FineTotal += fine; i.FineApplied = true;
            FeeCalculator.Recalculate(i);
            db.Add(new InvoiceLine { InvoiceId = i.Id, Kind = "fine", Description = "Late payment fine", Amount = fine });
            Post(campusId, i.Currency, $"Late fine on {i.Number}", "fine", i.Id, (Accounts.Receivable, fine, 0), (Accounts.FineIncome, 0, fine));
        }
        await db.SaveChangesAsync();
        return overdue.Count;
    }

    /// <summary>Reminds guardians of invoices due within 7 days or overdue, at most once every 3 days per invoice.</summary>
    public async Task<int> SendReminders(Guid campusId)
    {
        var today = Shared.Clock.Today((await db.Campuses.FirstAsync(c => c.Id == campusId)).TimeZone);
        var recently = DateTime.UtcNow.AddDays(-3);
        var due = await db.Invoices.Where(i => i.CampusId == campusId && (i.Status == "open" || i.Status == "partial") && i.DueDate <= today.AddDays(7) && (i.LastReminderAt == null || i.LastReminderAt < recently)).ToListAsync();
        foreach (var i in due)
        {
            var overdue = i.DueDate < today;
            await notifier.ToGuardiansOf([i.StudentId], overdue ? "Fee payment overdue" : "Fee payment due soon",
                $"Invoice {i.Number} ({i.Title}) has {i.Total - i.PaidTotal:0.00} {i.Currency} outstanding, {(overdue ? "due since" : "due on")} {i.DueDate:yyyy-MM-dd}.", "fees", "inapp", "email", "sms");
            i.LastReminderAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return due.Count;
    }
}
