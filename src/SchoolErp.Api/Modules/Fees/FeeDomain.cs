using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchoolErp.Api.Modules.Fees;

public record FeeItem(Guid? HeadId, string Name, decimal Amount);
public record DiscountRule(string Name, string Type, decimal Value);
public record DraftLine(Guid? HeadId, string Kind, string Description, decimal Amount);

/// <summary>Pure fee arithmetic, kept free of the database so it can be unit tested.</summary>
public static class FeeCalculator
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Splits an amount across installments by percent. When the percents total 100 the last
    /// installment absorbs the rounding remainder, so the parts always add up to the amount.
    /// </summary>
    public static List<decimal> Allocate(decimal amount, IReadOnlyList<decimal> percents)
    {
        var parts = percents.Select(p => R(amount * p / 100)).ToList();
        if (parts.Count > 0 && percents.Sum() == 100) parts[^1] = amount - parts.Take(parts.Count - 1).Sum();
        return parts;
    }

    /// <summary>Lines for one installment: a fee line per head, then discount lines (negative), never below zero.</summary>
    public static List<DraftLine> BuildInstallment(IReadOnlyList<FeeItem> items, IReadOnlyList<decimal> percents, int index, IReadOnlyList<DiscountRule> discounts)
    {
        var lines = items.Select(i => new DraftLine(i.HeadId, "fee", i.Name, Allocate(i.Amount, percents)[index])).Where(l => l.Amount != 0).ToList();
        var subtotal = lines.Sum(l => l.Amount);
        var remaining = subtotal;
        foreach (var d in discounts)
        {
            // Percent discounts apply to this installment; fixed discounts are annual and spread like the fees.
            var amount = d.Type == "percent" ? R(subtotal * d.Value / 100) : Allocate(d.Value, percents)[index];
            amount = Math.Min(amount, remaining);
            if (amount <= 0) continue;
            remaining -= amount;
            lines.Add(new DraftLine(null, "discount", d.Name, -amount));
        }
        return lines;
    }

    public static decimal LateFine(decimal balance, decimal percent, decimal fixedAmount) => balance <= 0 ? 0 : R(balance * percent / 100) + fixedAmount;

    public static string AgingBucket(DateOnly dueDate, DateOnly today)
    {
        var days = today.DayNumber - dueDate.DayNumber;
        return days <= 0 ? "current" : days <= 30 ? "1-30" : days <= 60 ? "31-60" : days <= 90 ? "61-90" : "90+";
    }

    public static void Recalculate(Invoice i)
    {
        i.Total = i.Subtotal - i.DiscountTotal + i.FineTotal - i.CreditTotal;
        if (i.Status is "void" or "carried") return;
        i.Status = i.PaidTotal >= i.Total ? "paid" : i.PaidTotal > 0 ? "partial" : "open";
    }
}

public record PaymentOrder(string Reference, string HostedPageUrl);
public record WebhookEvent(bool Valid, string? Reference, bool Succeeded);

/// <summary>
/// Payment gateway plug-in point (Stripe, Razorpay, Network International, ...). Card data never touches
/// this server: the provider hosts the payment page and reports the outcome through a signed webhook.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }
    Task<PaymentOrder> CreateOrder(Guid paymentId, decimal amount, string currency, string description);
    WebhookEvent VerifyWebhook(string body, string? signature);
    Task<bool> Refund(string reference, decimal amount);
}

/// <summary>Stand-in gateway for development and demos. Its "hosted page" is a screen in the web app.</summary>
public class MockGatewayProvider(IConfiguration config) : IPaymentProvider
{
    public string Name => "mock";
    private byte[] Secret => Encoding.UTF8.GetBytes(config["Payments:Mock:WebhookSecret"] ?? "mock-secret");

    public Task<PaymentOrder> CreateOrder(Guid paymentId, decimal amount, string currency, string description)
    {
        var reference = "mock_" + Guid.NewGuid().ToString("N");
        return Task.FromResult(new PaymentOrder(reference, $"/pay/mock/{reference}"));
    }

    public string Sign(string body) => Convert.ToHexString(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(body)));

    public WebhookEvent VerifyWebhook(string body, string? signature)
    {
        if (signature is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(body)), Encoding.ASCII.GetBytes(signature.ToUpperInvariant())))
            return new(false, null, false);
        using var doc = JsonDocument.Parse(body);
        return new(true, doc.RootElement.GetProperty("reference").GetString(), doc.RootElement.GetProperty("status").GetString() == "succeeded");
    }

    public Task<bool> Refund(string reference, decimal amount) => Task.FromResult(true);
}
