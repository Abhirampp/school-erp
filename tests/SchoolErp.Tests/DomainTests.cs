using SchoolErp.Api.Modules.Academics;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Shared;

namespace SchoolErp.Tests;

public class GradingEngineTests
{
    private static SchemeDefinition Preset(string key) => GradingEngine.Presets[key].Def;

    [Fact]
    public void Weighted_percentage_uses_component_weights_not_raw_marks()
    {
        // 30/40 at 20%, 8/10 at 10%, 60/80 at 70%  ->  15 + 8 + 52.5 = 75.5
        var result = GradingEngine.Compute(new SchemeDefinition { Bands = [new(0, "X")], Decimals = 1 },
            [new(20, 40, 30), new(10, 10, 8), new(70, 80, 60)]);
        Assert.Equal(75.5m, result.Percent);
        Assert.True(result.Complete);
    }

    [Theory]
    [InlineData(91, "A1")] [InlineData(90.4, "A2")] [InlineData(90.5, "A1")] [InlineData(33, "D")] [InlineData(32.4, "E")] [InlineData(0, "E")] [InlineData(100, "A1")]
    public void Cbse_bands_apply_after_rounding_to_whole_marks(double percent, string grade) =>
        Assert.Equal(grade, GradingEngine.Grade(Preset("cbse"), (decimal)percent).Grade);

    [Theory]
    [InlineData(85, "7", 7)] [InlineData(50, "4", 4)] [InlineData(24, "1", 1)]
    public void Ib_scale_maps_to_1_to_7(double percent, string grade, double points)
    {
        var r = GradingEngine.Grade(Preset("ib"), (decimal)percent);
        Assert.Equal(grade, r.Grade);
        Assert.Equal((decimal)points, r.Points);
    }

    [Fact]
    public void Cambridge_and_gpa_schemes_are_just_different_data()
    {
        Assert.Equal("A*", GradingEngine.Grade(Preset("cambridge"), 92).Grade);
        Assert.Equal("U", GradingEngine.Grade(Preset("cambridge"), 12).Grade);
        Assert.Equal(3.7m, GradingEngine.Grade(Preset("gpa"), 91).Points);
    }

    [Fact]
    public void Pass_mark_is_per_scheme()
    {
        Assert.True(GradingEngine.Grade(Preset("cbse"), 33).Passed);
        Assert.False(GradingEngine.Grade(Preset("cbse"), 32).Passed);
        Assert.False(GradingEngine.Grade(Preset("ib"), 45).Passed);
    }

    [Fact]
    public void Unexcused_absence_scores_zero_but_excused_is_reweighted()
    {
        var def = new SchemeDefinition { Bands = [new(0, "X")], Decimals = 1 };
        var absent = GradingEngine.Compute(def, [new(50, 100, 80), new(50, 100, null, IsAbsent: true)]);
        var excused = GradingEngine.Compute(def, [new(50, 100, 80), new(50, 100, null, IsExcused: true)]);
        Assert.Equal(40m, absent.Percent);
        Assert.Equal(80m, excused.Percent);
    }

    [Fact]
    public void Absence_can_be_configured_to_be_left_out()
    {
        var def = new SchemeDefinition { Bands = [new(0, "X")], AbsentCountsAsZero = false };
        Assert.Equal(80m, GradingEngine.Compute(def, [new(50, 100, 80), new(50, 100, null, IsAbsent: true)]).Percent);
    }

    [Fact]
    public void Missing_marks_flag_the_result_as_incomplete()
    {
        var r = GradingEngine.Compute(Preset("cbse"), [new(50, 100, 80), new(50, 100, null)]);
        Assert.False(r.Complete);
        Assert.Equal(80m, r.Percent);
    }

    [Fact]
    public void No_marks_at_all_gives_no_grade()
    {
        var r = GradingEngine.Compute(Preset("cbse"), [new(100, 100, null)]);
        Assert.Null(r.Percent);
        Assert.Null(r.Grade);
    }

    [Fact]
    public void Rounding_modes()
    {
        var bands = new List<Band> { new(0, "F"), new(40, "P") };
        Assert.Equal("P", GradingEngine.Grade(new SchemeDefinition { Bands = bands, Decimals = 0, Rounding = "half-up" }, 39.5m).Grade);
        Assert.Equal("F", GradingEngine.Grade(new SchemeDefinition { Bands = bands, Decimals = 0, Rounding = "floor" }, 39.9m).Grade);
        Assert.Equal("F", GradingEngine.Grade(new SchemeDefinition { Bands = bands, Decimals = 0, Rounding = "none" }, 39.99m).Grade);
    }

    [Fact]
    public void Gpa_is_credit_weighted_and_skips_subjects_without_points()
    {
        Assert.Equal(3.25m, GradingEngine.Gpa([(4.0m, 1), (3.0m, 3), (null, 2)]));
        Assert.Null(GradingEngine.Gpa([(null, 1)]));
    }

    [Fact]
    public void Validation_rejects_schemes_that_leave_marks_ungraded()
    {
        Assert.NotEmpty(GradingEngine.Validate(new SchemeDefinition { Bands = [new(40, "P")] }));
        Assert.NotEmpty(GradingEngine.Validate(new SchemeDefinition { Bands = [new(0, "F"), new(0, "G")] }));
        Assert.NotEmpty(GradingEngine.Validate(new SchemeDefinition { Bands = [] }));
        Assert.All(GradingEngine.Presets.Values, p => Assert.Empty(GradingEngine.Validate(p.Def)));
    }

    [Fact]
    public void Scheme_definition_round_trips_through_json() =>
        Assert.Equal("A1", GradingEngine.Grade(GradingEngine.Parse(GradingEngine.Serialize(Preset("cbse"))), 95).Grade);
}

public class FeeCalculatorTests
{
    private static readonly FeeItem[] Items = [new(Guid.NewGuid(), "Tuition", 100000), new(Guid.NewGuid(), "Transport", 24000)];

    [Fact]
    public void Installments_always_add_up_to_the_annual_amount()
    {
        var parts = FeeCalculator.Allocate(100000, [33.33m, 33.33m, 33.34m]);
        Assert.Equal(100000, parts.Sum());
        Assert.Equal(1000m, FeeCalculator.Allocate(1000, [100]).Single());
        Assert.Equal(100m, FeeCalculator.Allocate(100, [33.33m, 33.33m, 33.34m]).Sum());
    }

    [Fact]
    public void Installment_lines_split_each_head_by_percent()
    {
        var lines = FeeCalculator.BuildInstallment(Items, [40, 30, 30], 0, []);
        Assert.Equal([40000m, 9600m], lines.Select(l => l.Amount));
        Assert.All(lines, l => Assert.Equal("fee", l.Kind));
    }

    [Fact]
    public void Percent_discount_applies_to_the_installment_subtotal()
    {
        var lines = FeeCalculator.BuildInstallment(Items, [40, 30, 30], 0, [new("Sibling", "percent", 10)]);
        Assert.Equal(-4960m, lines.Single(l => l.Kind == "discount").Amount);
        Assert.Equal(44640m, lines.Sum(l => l.Amount));
    }

    [Fact]
    public void Fixed_discount_is_annual_and_spread_across_installments()
    {
        var total = Enumerable.Range(0, 3).Sum(i => FeeCalculator.BuildInstallment(Items, [40, 30, 30], i, [new("Bursary", "fixed", 10000)]).Where(l => l.Kind == "discount").Sum(l => l.Amount));
        Assert.Equal(-10000m, total);
    }

    [Fact]
    public void Stacked_discounts_never_take_an_invoice_below_zero()
    {
        var lines = FeeCalculator.BuildInstallment(Items, [100], 0, [new("Full scholarship", "percent", 100), new("Sibling", "percent", 10)]);
        Assert.Equal(0m, lines.Sum(l => l.Amount));
        Assert.Single(lines, l => l.Kind == "discount");
    }

    [Fact]
    public void Late_fine_is_percent_of_balance_plus_fixed_amount()
    {
        Assert.Equal(900m, FeeCalculator.LateFine(40000, 2, 100));
        Assert.Equal(0m, FeeCalculator.LateFine(0, 2, 100));
    }

    [Theory]
    [InlineData(0, "current")] [InlineData(-5, "current")] [InlineData(1, "1-30")] [InlineData(30, "1-30")] [InlineData(31, "31-60")] [InlineData(90, "61-90")] [InlineData(91, "90+")]
    public void Aging_buckets(int daysOverdue, string bucket)
    {
        var today = new DateOnly(2026, 10, 12);
        Assert.Equal(bucket, FeeCalculator.AgingBucket(today.AddDays(-daysOverdue), today));
    }

    [Fact]
    public void Invoice_status_follows_payments_fines_and_credits()
    {
        var i = new Invoice { Subtotal = 1000, DiscountTotal = 100 };
        FeeCalculator.Recalculate(i);
        Assert.Equal((900m, "open"), (i.Total, i.Status));
        i.PaidTotal = 400; FeeCalculator.Recalculate(i);
        Assert.Equal("partial", i.Status);
        i.FineTotal = 50; i.CreditTotal = 550; FeeCalculator.Recalculate(i);
        Assert.Equal((400m, "paid"), (i.Total, i.Status));
    }
}

public class ClashDetectorTests
{
    private static readonly Guid Period = Guid.NewGuid(), Teacher = Guid.NewGuid();
    private static TimetableSlot Slot(Guid section, int day = 1, Guid? teacher = null, string? room = null) =>
        new() { SectionId = section, Day = day, PeriodId = Period, TeacherId = teacher, Room = room };

    [Fact]
    public void Teacher_cannot_teach_two_sections_in_the_same_period()
    {
        var clashes = ClashDetector.Find(Slot(Guid.NewGuid(), teacher: Teacher), [Slot(Guid.NewGuid(), teacher: Teacher)], _ => "5B");
        Assert.Equal("teacher", Assert.Single(clashes).Kind);
    }

    [Fact]
    public void Room_cannot_be_double_booked_and_names_are_case_insensitive() =>
        Assert.Equal("room", Assert.Single(ClashDetector.Find(Slot(Guid.NewGuid(), room: "lab 1"), [Slot(Guid.NewGuid(), room: "Lab 1 ")], _ => "5B")).Kind);

    [Fact]
    public void Different_day_or_same_section_is_not_a_clash()
    {
        var section = Guid.NewGuid();
        Assert.Empty(ClashDetector.Find(Slot(Guid.NewGuid(), 1, Teacher), [Slot(Guid.NewGuid(), 2, Teacher)], _ => ""));
        Assert.Empty(ClashDetector.Find(Slot(section, 1, Teacher, "R1"), [Slot(section, 1, Teacher, "R1")], _ => ""));
        Assert.Empty(ClashDetector.Find(Slot(Guid.NewGuid()), [Slot(Guid.NewGuid())], _ => ""));
    }
}

public class SharedTests
{
    [Fact]
    public void Field_encryption_round_trips_and_does_not_store_plain_text()
    {
        var cipher = FieldCrypto.Encrypt("Asthma; carries an inhaler")!;
        Assert.StartsWith("enc:", cipher);
        Assert.DoesNotContain("Asthma", cipher);
        Assert.NotEqual(cipher, FieldCrypto.Encrypt("Asthma; carries an inhaler"));
        Assert.Equal("Asthma; carries an inhaler", FieldCrypto.Decrypt(cipher));
        Assert.Null(FieldCrypto.Encrypt(null));
    }

    [Fact]
    public void Totp_matches_the_rfc_6238_test_vector()
    {
        // RFC 6238 appendix B, SHA-1, secret "12345678901234567890", T = 59 s -> 94287082 (last 6 digits)
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        var at = DateTimeOffset.FromUnixTimeSeconds(59).UtcDateTime;
        Assert.Equal("287082", Totp.Code(secret, at));
        Assert.True(Totp.Verify(secret, "287082", at));
        Assert.False(Totp.Verify(secret, "000000", at));
        Assert.False(Totp.Verify(secret, "287082", at.AddMinutes(5)));
    }

    [Fact]
    public void Csv_handles_quotes_commas_and_blank_lines()
    {
        var rows = Csv.Parse("Name,Note\r\n\"Rao, Asha\",\"said \"\"hi\"\"\"\r\n\r\nB,\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Rao, Asha", rows[0]["name"]);
        Assert.Equal("said \"hi\"", rows[0]["Note"]);
    }

    [Fact]
    public void Csv_export_neutralises_spreadsheet_formulas() =>
        Assert.Contains("'=HYPERLINK", Csv.Write(["A"], [new object?[] { "=HYPERLINK(\"http://x\")" }]));
}

public class PayrollCalculatorTests
{
    private static readonly SchoolErp.Api.Modules.Payroll.PayRule[] Teacher =
    [
        new("Basic", "earning", "fixed", 42000), new("House rent allowance", "earning", "percent", 40), new("Transport allowance", "earning", "fixed", 2400),
        new("Provident fund", "deduction", "percent", 12), new("Professional tax", "deduction", "fixed", 200),
    ];

    [Fact]
    public void Percent_components_are_a_share_of_basic()
    {
        var r = SchoolErp.Api.Modules.Payroll.PayrollCalculator.Compute(Teacher, 30, 0);
        Assert.Equal((61200m, 5240m, 55960m), (r.Gross, r.Deductions, r.Net));
        Assert.Equal(16800m, r.Lines.Single(l => l.Name == "House rent allowance").Amount);
    }

    [Fact]
    public void Unpaid_leave_is_deducted_pro_rata_by_calendar_day()
    {
        var r = SchoolErp.Api.Modules.Payroll.PayrollCalculator.Compute(Teacher, 30, 3);
        Assert.Equal(6120m, r.UnpaidLeaveDeduction);
        Assert.Equal(61200m - 6120m - 5240m, r.Net);
    }

    [Fact]
    public void Net_pay_never_goes_negative()
    {
        var r = SchoolErp.Api.Modules.Payroll.PayrollCalculator.Compute([new("Basic", "earning", "fixed", 1000), new("Loan recovery", "deduction", "fixed", 5000)], 30, 0);
        Assert.Equal(0m, r.Net);
        Assert.Equal(r.Gross, r.Deductions);
    }

    [Fact]
    public void Unpaid_days_count_only_the_part_of_the_leave_inside_the_month()
    {
        var october = new DateOnly(2026, 10, 1);
        var days = SchoolErp.Api.Modules.Payroll.PayrollCalculator.UnpaidDaysIn(october,
            [(new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 2)), (new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 3)), (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5))]);
        Assert.Equal(3, days); // 1, 2 and 3 October; overlapping requests are not double counted
    }
}
