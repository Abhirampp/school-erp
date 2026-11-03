using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Fees;

public static class FeeEndpoints
{
    public record ItemRequest(Guid FeeHeadId, decimal Amount);
    public record InstallmentRequest(string Name, DateOnly DueDate, decimal Percent);
    public record StructureRequest(Guid AcademicYearId, Guid GradeLevelId, string Name, string? Category, string Currency, List<ItemRequest> Items, List<InstallmentRequest> Installments);
    public record GenerateRequest(Guid AcademicYearId, Guid? GradeLevelId, Guid? StudentId);
    public record OfflinePayment(decimal Amount, string Method, string? Reference);
    public record AmountReason(decimal Amount, string Reason);
    public record CampusRequest(Guid CampusId);
    public record MockOutcome(string Status);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapCrud<FeeHead>("/api/fee-heads", null, Perm.FeesManage);
        app.MapCrud<Discount>("/api/discounts", Perm.FeesRead, Perm.FeesManage);
        app.MapCrud<StudentDiscount>("/api/student-discounts", Perm.FeesRead, Perm.FeesManage);

        var g = app.MapGroup("/api/fees");

        // ---- Structures ----
        g.MapGet("structures", async (Guid academicYearId, AppDbContext db) =>
        {
            var structures = await db.FeeStructures.Where(s => s.AcademicYearId == academicYearId).OrderBy(s => s.CreatedAt).ToListAsync();
            var ids = structures.Select(s => s.Id).ToList();
            var items = await db.FeeStructureItems.Where(i => ids.Contains(i.FeeStructureId)).ToListAsync();
            var installments = await db.Installments.Where(i => ids.Contains(i.FeeStructureId)).OrderBy(i => i.Order).ToListAsync();
            var heads = await db.FeeHeads.ToDictionaryAsync(h => h.Id, h => h.Name);
            var grades = await db.GradeLevels.ToDictionaryAsync(x => x.Id, x => x.Name);
            return structures.Select(s => new
            {
                s.Id, s.Name, s.Category, s.Currency, s.GradeLevelId, s.AcademicYearId, grade = grades.GetValueOrDefault(s.GradeLevelId),
                items = items.Where(i => i.FeeStructureId == s.Id).Select(i => new { i.FeeHeadId, head = heads.GetValueOrDefault(i.FeeHeadId), i.Amount }),
                installments = installments.Where(i => i.FeeStructureId == s.Id).Select(i => new { i.Id, i.Name, i.DueDate, i.Percent }),
                total = items.Where(i => i.FeeStructureId == s.Id).Sum(i => i.Amount)
            });
        }).Require(Perm.FeesRead, Perm.FeesManage);

        g.MapPost("structures", async (StructureRequest req, AppDbContext db) =>
        {
            if (req.Items.Count == 0 || req.Items.Any(i => i.Amount <= 0)) return EndpointExtensions.Bad("Add at least one fee head with an amount above 0.");
            if (req.Installments.Count == 0) return EndpointExtensions.Bad("Add at least one installment.");
            if (req.Installments.Sum(i => i.Percent) != 100) return EndpointExtensions.Bad("Installment percentages must add up to 100.");
            var s = new FeeStructure { AcademicYearId = req.AcademicYearId, GradeLevelId = req.GradeLevelId, Name = req.Name, Category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim(), Currency = req.Currency };
            db.Add(s);
            foreach (var i in req.Items) db.Add(new FeeStructureItem { FeeStructureId = s.Id, FeeHeadId = i.FeeHeadId, Amount = i.Amount });
            var order = 0;
            foreach (var i in req.Installments) db.Add(new Installment { FeeStructureId = s.Id, Name = i.Name, DueDate = i.DueDate, Percent = i.Percent, Order = order++ });
            await db.SaveChangesAsync();
            return Results.Ok(new { s.Id });
        }).Require(Perm.FeesManage);

        g.MapDelete("structures/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var s = await db.FeeStructures.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var installmentIds = await db.Installments.Where(i => i.FeeStructureId == id).Select(i => (Guid?)i.Id).ToListAsync();
            if (await db.Invoices.AnyAsync(i => installmentIds.Contains(i.InstallmentId))) return EndpointExtensions.Bad("Invoices were already raised from this structure, so it cannot be deleted.");
            db.Remove(s);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Require(Perm.FeesManage);

        // ---- Invoices ----
        g.MapPost("invoices/generate", async (GenerateRequest req, FeeService fees) =>
        {
            var (created, skipped, warnings) = await fees.GenerateInvoices(req.AcademicYearId, req.GradeLevelId, req.StudentId);
            return new { created, skipped, warnings };
        }).Require(Perm.FeesManage);

        g.MapGet("invoices", async (Guid campusId, string? status, Guid? studentId, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(campusId)) return EndpointExtensions.Forbidden();
            var today = Clock.Today((await db.Campuses.FirstOrDefaultAsync(c => c.Id == campusId))?.TimeZone);
            var q = db.Invoices.Where(i => i.CampusId == campusId);
            if (!string.IsNullOrEmpty(status)) q = status == "overdue" ? q.Where(i => (i.Status == "open" || i.Status == "partial") && i.DueDate < today) : q.Where(i => i.Status == status);
            if (studentId is { } s) q = q.Where(i => i.StudentId == s);
            var list = await q.OrderByDescending(i => i.Number).Take(500).ToListAsync();
            var ids = list.Select(i => i.StudentId).ToList();
            var names = await db.Students.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FirstName + " " + x.LastName);
            return Results.Ok(list.Select(i => new { i.Id, i.Number, i.Title, i.StudentId, student = names.GetValueOrDefault(i.StudentId), i.IssueDate, i.DueDate, i.Currency, i.Total, i.PaidTotal, balance = i.Total - i.PaidTotal, i.Status }));
        }).Require(Perm.FeesRead, Perm.FeesManage);

        g.MapGet("invoices/{id:guid}", async (Guid id, AppDbContext db) => await InvoiceDetail(db, id) is { } d ? Results.Ok(d) : Results.NotFound()).Require(Perm.FeesRead, Perm.FeesManage);

        g.MapPost("invoices/{id:guid}/payments", async (Guid id, OfflinePayment req, AppDbContext db, FeeService fees) =>
        {
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
            if (invoice is null) return Results.NotFound();
            if (req.Method is not ("cash" or "cheque" or "bank")) return EndpointExtensions.Bad("Method must be cash, cheque or bank.");
            if (invoice.Status is "void" or "carried") return EndpointExtensions.Bad("This invoice is closed.");
            if (req.Amount <= 0 || req.Amount > invoice.Total - invoice.PaidTotal) return EndpointExtensions.Bad($"Amount must be between 0 and the balance of {invoice.Total - invoice.PaidTotal:0.00}.");
            var p = new Payment { InvoiceId = id, Amount = req.Amount, Currency = invoice.Currency, Method = req.Method, GatewayReference = req.Reference };
            db.Add(p);
            await fees.Settle(p, invoice);
            await db.SaveChangesAsync();
            return Results.Ok(new { p.Id, p.ReceiptNumber });
        }).Require(Perm.FeesManage);

        g.MapPost("invoices/{id:guid}/pay-online", async (Guid id, AppDbContext db, FeeService fees) =>
        {
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
            if (invoice is null) return Results.NotFound();
            var (url, error) = await fees.StartOnlinePayment(invoice);
            return error is null ? Results.Ok(new { url }) : EndpointExtensions.Bad(error);
        }).Require(Perm.FeesManage);

        g.MapPost("invoices/{id:guid}/credit-notes", async (Guid id, AmountReason req, AppDbContext db, FeeService fees) =>
        {
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
            if (invoice is null) return Results.NotFound();
            if (req.Amount <= 0 || req.Amount > invoice.Total - invoice.PaidTotal) return EndpointExtensions.Bad("A credit note cannot exceed the unpaid balance. Refund a payment first if the fee was already paid.");
            if (string.IsNullOrWhiteSpace(req.Reason)) return EndpointExtensions.Bad("Give a reason.");
            var count = await db.CreditNotes.IgnoreQueryFilters().CountAsync(c => c.TenantId == db.CurrentTenantId);
            var cn = new CreditNote { InvoiceId = id, Amount = req.Amount, Reason = req.Reason, Number = $"CN-{DateTime.UtcNow.Year}-{count + 1:D5}" };
            db.Add(cn);
            invoice.CreditTotal += req.Amount;
            FeeCalculator.Recalculate(invoice);
            fees.Post(invoice.CampusId, invoice.Currency, $"Credit note {cn.Number} on {invoice.Number}: {req.Reason}", "credit-note", cn.Id, (Accounts.Adjustments, req.Amount, 0), (Accounts.Receivable, 0, req.Amount));
            await db.SaveChangesAsync();
            return Results.Ok(cn);
        }).Require(Perm.FeesManage);

        g.MapPost("payments/{id:guid}/refund", async (Guid id, AmountReason req, AppDbContext db, FeeService fees) =>
        {
            var p = await db.Payments.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            if (p.Status is not ("succeeded" or "refunded") || req.Amount <= 0 || req.Amount > p.Amount - p.RefundedAmount) return EndpointExtensions.Bad("The refund exceeds what is left on this payment.");
            var invoice = await db.Invoices.FirstAsync(i => i.Id == p.InvoiceId);
            if (p.Method == "online" && p.GatewayReference is not null && !await fees.Provider(p.Gateway).Refund(p.GatewayReference, req.Amount))
                return Results.Problem("The payment gateway declined the refund.", statusCode: 502);
            p.RefundedAmount += req.Amount;
            if (p.RefundedAmount >= p.Amount) p.Status = "refunded";
            invoice.PaidTotal -= req.Amount;
            FeeCalculator.Recalculate(invoice);
            fees.Post(invoice.CampusId, invoice.Currency, $"Refund of {p.ReceiptNumber}: {req.Reason}", "refund", p.Id, (Accounts.Receivable, req.Amount, 0), (Accounts.ForMethod(p.Method), 0, req.Amount));
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require(Perm.FeesManage);

        g.MapGet("payments/{id:guid}/receipt", async (Guid id, AppDbContext db) =>
        {
            var p = await db.Payments.FirstOrDefaultAsync(x => x.Id == id && x.ReceiptNumber != null);
            return p is null ? Results.NotFound() : Results.File(await Receipt(db, p), "application/pdf", $"{p.ReceiptNumber}.pdf");
        }).Require(Perm.FeesRead, Perm.FeesManage);

        g.MapPost("fines/run", async (CampusRequest req, FeeService fees) => new { finesApplied = await fees.ApplyLateFines(req.CampusId) }).Require(Perm.FeesManage);
        g.MapPost("reminders/run", async (CampusRequest req, FeeService fees) => new { remindersSent = await fees.SendReminders(req.CampusId) }).Require(Perm.FeesManage);

        // ---- Reports ----
        g.MapGet("aging", async (Guid campusId, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(campusId)) return EndpointExtensions.Forbidden();
            var today = Clock.Today((await db.Campuses.FirstOrDefaultAsync(c => c.Id == campusId))?.TimeZone);
            var open = await db.Invoices.Where(i => i.CampusId == campusId && (i.Status == "open" || i.Status == "partial")).ToListAsync();
            var ids = open.Select(i => i.StudentId).Distinct().ToList();
            var names = await db.Students.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FirstName + " " + x.LastName);
            string[] buckets = ["current", "1-30", "31-60", "61-90", "90+"];
            var rows = open.GroupBy(i => i.StudentId).Select(grp => new
            {
                studentId = grp.Key, student = names.GetValueOrDefault(grp.Key),
                buckets = buckets.ToDictionary(b => b, b => grp.Where(i => FeeCalculator.AgingBucket(i.DueDate, today) == b).Sum(i => i.Total - i.PaidTotal)),
                total = grp.Sum(i => i.Total - i.PaidTotal)
            }).OrderByDescending(r => r.total).ToList();
            return Results.Ok(new { buckets, rows, totals = buckets.ToDictionary(b => b, b => rows.Sum(r => r.buckets[b])), total = rows.Sum(r => r.total) });
        }).Require(Perm.FeesRead, Perm.FeesManage);

        g.MapGet("ledger", async (Guid campusId, DateOnly? from, DateOnly? to, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(campusId)) return EndpointExtensions.Forbidden();
            var entries = await Ledger(db, campusId, from, to);
            var trial = entries.GroupBy(e => e.Account).OrderBy(x => x.Key).Select(x => new { account = x.Key, debit = x.Sum(e => e.Debit), credit = x.Sum(e => e.Credit) }).ToList();
            return Results.Ok(new { entries = entries.Take(500), trialBalance = trial, totalDebit = trial.Sum(t => t.debit), totalCredit = trial.Sum(t => t.credit) });
        }).Require(Perm.FeesRead, Perm.FeesManage);

        // Journal CSV that accounting packages can import. Exports are audited (FR-09-02).
        g.MapGet("ledger/export", async (Guid campusId, DateOnly? from, DateOnly? to, AppDbContext db, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(campusId)) return EndpointExtensions.Forbidden();
            var entries = await Ledger(db, campusId, from, to);
            db.Audit("export", "Ledger", campusId.ToString(), new { from, to, rows = entries.Count });
            await db.SaveChangesAsync();
            var csv = Csv.Write(["Date", "Transaction", "Account", "Debit", "Credit", "Currency", "Description", "Reference"],
                entries.Select(e => new object?[] { e.Date.ToString("yyyy-MM-dd"), e.TransactionId, e.Account, e.Debit, e.Credit, e.Currency, e.Description, e.RefType }));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "ledger.csv");
        }).Require(Perm.FeesManage);

        // ---- Gateway callbacks (no login: authenticated by the provider's signature) ----
        app.MapPost("/api/payments/webhook/{provider}", async (string provider, HttpRequest http, AppDbContext db, FeeService fees, CurrentUser cu) =>
        {
            using var reader = new StreamReader(http.Body);
            var body = await reader.ReadToEndAsync();
            return await HandleWebhook(provider, body, http.Headers["X-Signature"].FirstOrDefault(), db, fees, cu);
        }).RequireRateLimiting("public");

        // The mock gateway's hosted page. Disabled unless Payments:Mock:Enabled is true (development and demos only).
        app.MapGet("/api/payments/mock/{reference}", async (string reference, AppDbContext db, IConfiguration config) =>
        {
            if (!config.GetValue<bool>("Payments:Mock:Enabled")) return Results.NotFound();
            var p = await db.Payments.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.GatewayReference == reference && x.Gateway == "mock" && !x.IsDeleted);
            return p is null ? Results.NotFound() : Results.Ok(new { p.Amount, p.Currency, p.Status });
        }).RequireRateLimiting("public");

        app.MapPost("/api/payments/mock/{reference}", async (string reference, MockOutcome req, AppDbContext db, FeeService fees, CurrentUser cu, IConfiguration config, IEnumerable<IPaymentProvider> providers) =>
        {
            if (!config.GetValue<bool>("Payments:Mock:Enabled")) return Results.NotFound();
            var mock = providers.OfType<MockGatewayProvider>().First();
            var body = JsonSerializer.Serialize(new { reference, status = req.Status == "succeeded" ? "succeeded" : "failed" });
            return await HandleWebhook("mock", body, mock.Sign(body), db, fees, cu);
        }).RequireRateLimiting("public");
    }

    private static async Task<IResult> HandleWebhook(string provider, string body, string? signature, AppDbContext db, FeeService fees, CurrentUser cu)
    {
        IPaymentProvider gateway;
        try { gateway = fees.Provider(provider); } catch (InvalidOperationException) { return Results.NotFound(); }
        WebhookEvent evt;
        try { evt = gateway.VerifyWebhook(body, signature); } catch (Exception) { return Results.BadRequest(); }
        if (!evt.Valid || evt.Reference is null) return Results.Unauthorized();
        // The webhook carries no tenant; the payment row tells us which tenant it belongs to.
        var p = await db.Payments.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.GatewayReference == evt.Reference && x.Gateway == provider && !x.IsDeleted);
        if (p is null) return Results.NotFound();
        cu.TenantId = p.TenantId; cu.Name = $"{provider} gateway";
        if (p.Status != "pending") return Results.Ok(new { p.Status });
        if (!evt.Succeeded) p.Status = "failed";
        else await fees.Settle(p, await db.Invoices.FirstAsync(i => i.Id == p.InvoiceId));
        await db.SaveChangesAsync();
        return Results.Ok(new { p.Status });
    }

    private static async Task<List<LedgerEntry>> Ledger(AppDbContext db, Guid campusId, DateOnly? from, DateOnly? to)
    {
        var q = db.LedgerEntries.Where(e => e.CampusId == campusId);
        if (from is { } f) q = q.Where(e => e.Date >= f);
        if (to is { } t) q = q.Where(e => e.Date <= t);
        return await q.OrderByDescending(e => e.CreatedAt).ThenBy(e => e.TransactionId).ToListAsync();
    }

    public static async Task<object?> InvoiceDetail(AppDbContext db, Guid id)
    {
        var i = await db.Invoices.FirstOrDefaultAsync(x => x.Id == id);
        if (i is null) return null;
        var s = await db.Students.FirstOrDefaultAsync(x => x.Id == i.StudentId);
        return new
        {
            invoice = new { i.Id, i.Number, i.Title, i.StudentId, student = s?.FullName(), i.IssueDate, i.DueDate, i.Currency, i.Subtotal, i.DiscountTotal, i.FineTotal, i.CreditTotal, i.Total, i.PaidTotal, balance = i.Total - i.PaidTotal, i.Status },
            lines = (await db.InvoiceLines.Where(l => l.InvoiceId == id).Select(l => new { l.Kind, l.Description, l.Amount }).ToListAsync()).OrderBy(l => l.Amount < 0 ? 1 : l.Kind == "fine" ? 2 : 0).ThenByDescending(l => l.Amount),
            payments = await db.Payments.Where(p => p.InvoiceId == id).OrderBy(p => p.CreatedAt).Select(p => new { p.Id, p.Amount, p.RefundedAmount, p.Method, p.Status, p.ReceiptNumber, p.PaidAt }).ToListAsync(),
            creditNotes = await db.CreditNotes.Where(c => c.InvoiceId == id).Select(c => new { c.Number, c.Amount, c.Reason, c.CreatedAt }).ToListAsync()
        };
    }

    public static async Task<byte[]> Receipt(AppDbContext db, Payment p)
    {
        var invoice = await db.Invoices.FirstAsync(i => i.Id == p.InvoiceId);
        var student = await db.Students.FirstAsync(s => s.Id == invoice.StudentId);
        var campus = await db.Campuses.FirstAsync(c => c.Id == invoice.CampusId);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == db.CurrentTenantId);
        return Pdf.Receipt(tenant.Name, campus, student, invoice, p);
    }
}
