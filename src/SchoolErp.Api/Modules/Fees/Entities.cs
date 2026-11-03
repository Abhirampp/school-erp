using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Fees;

public class FeeHead : TenantEntity
{
    public Guid CampusId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public class FeeStructure : TenantEntity
{
    public Guid AcademicYearId { get; set; }
    public Guid GradeLevelId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Optional student category this structure applies to (null = everyone).</summary>
    public string? Category { get; set; }
    public string Currency { get; set; } = "INR";
}

public class FeeStructureItem : TenantEntity
{
    public Guid FeeStructureId { get; set; }
    public Guid FeeHeadId { get; set; }
    public decimal Amount { get; set; }
}

public class Installment : TenantEntity
{
    public Guid FeeStructureId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly DueDate { get; set; }
    public decimal Percent { get; set; }
    public int Order { get; set; }
}

public class Discount : TenantEntity
{
    public string Name { get; set; } = "";
    /// <summary>discount, scholarship, sibling</summary>
    public string Kind { get; set; } = "discount";
    /// <summary>percent or fixed</summary>
    public string Type { get; set; } = "percent";
    public decimal Value { get; set; }
    /// <summary>Sibling concessions apply automatically to younger siblings.</summary>
    public bool AutoApplySibling { get; set; }
}

public class StudentDiscount : TenantEntity
{
    public Guid StudentId { get; set; }
    public Guid DiscountId { get; set; }
    public Guid AcademicYearId { get; set; }
}

/// <summary>One fee account per family (primary guardian).</summary>
public class FeeAccount : TenantEntity
{
    public Guid GuardianId { get; set; }
    public string Number { get; set; } = "";
}

public class Invoice : TenantEntity
{
    public Guid FeeAccountId { get; set; }
    public Guid StudentId { get; set; }
    public Guid CampusId { get; set; }
    public Guid AcademicYearId { get; set; }
    public Guid? InstallmentId { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string Currency { get; set; } = "INR";
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal FineTotal { get; set; }
    public decimal CreditTotal { get; set; }
    public decimal Total { get; set; }
    public decimal PaidTotal { get; set; }
    /// <summary>open, partial, paid, void</summary>
    public string Status { get; set; } = "open";
    public bool FineApplied { get; set; }
    public DateTime? LastReminderAt { get; set; }
}

public class InvoiceLine : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public Guid? FeeHeadId { get; set; }
    /// <summary>fee, discount, fine, opening</summary>
    public string Kind { get; set; } = "fee";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
}

public class Payment : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public decimal RefundedAmount { get; set; }
    public string Currency { get; set; } = "INR";
    /// <summary>online, cash, cheque, bank</summary>
    public string Method { get; set; } = "online";
    public string? Gateway { get; set; }
    public string? GatewayReference { get; set; }
    /// <summary>pending, succeeded, failed, refunded</summary>
    public string Status { get; set; } = "pending";
    public string? ReceiptNumber { get; set; }
    public DateTime? PaidAt { get; set; }
}

public class CreditNote : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public string Number { get; set; } = "";
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>Double-entry ledger line. Lines sharing a TransactionId always balance.</summary>
public class LedgerEntry : TenantEntity
{
    public Guid CampusId { get; set; }
    public Guid TransactionId { get; set; }
    public DateOnly Date { get; set; }
    public string Account { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Currency { get; set; } = "INR";
    public string Description { get; set; } = "";
    public string? RefType { get; set; }
    public Guid? RefId { get; set; }
}
