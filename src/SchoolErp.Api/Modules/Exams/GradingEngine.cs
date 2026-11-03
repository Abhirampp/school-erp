using System.Text.Json;

namespace SchoolErp.Api.Modules.Exams;

public record Band(decimal Min, string Grade, decimal? Points = null, string? Descriptor = null);

/// <summary>
/// The whole behaviour of a grading scheme, stored as JSON per tenant. CBSE, IB, Cambridge, GPA and
/// percentage schemes are all just different data for this one shape: no per-curriculum code.
/// </summary>
public class SchemeDefinition
{
    /// <summary>Each band applies from Min percent up to the next band's Min.</summary>
    public List<Band> Bands { get; set; } = [];
    public decimal PassMark { get; set; } = 40;
    public int Decimals { get; set; } = 1;
    /// <summary>half-up, floor or none (band on the raw value).</summary>
    public string Rounding { get; set; } = "half-up";
    /// <summary>true: an unexcused absence scores zero. false: the component is left out and the rest re-weighted.</summary>
    public bool AbsentCountsAsZero { get; set; } = true;
}

public record ComponentScore(decimal Weight, decimal MaxMarks, decimal? Marks, bool IsAbsent = false, bool IsExcused = false);

/// <summary>Complete is false when a counted component has no mark yet.</summary>
public record GradeResult(decimal? Percent, string? Grade, decimal? Points, string? Descriptor, bool? Passed, bool Complete);

public static class GradingEngine
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SchemeDefinition Parse(string json) => JsonSerializer.Deserialize<SchemeDefinition>(json, Json) ?? new();
    public static string Serialize(SchemeDefinition def) => JsonSerializer.Serialize(def, Json);

    public static List<string> Validate(SchemeDefinition def)
    {
        var errors = new List<string>();
        if (def.Bands.Count == 0) errors.Add("Add at least one band.");
        if (def.Bands.Any(b => string.IsNullOrWhiteSpace(b.Grade))) errors.Add("Every band needs a grade label.");
        if (def.Bands.Any(b => b.Min is < 0 or > 100)) errors.Add("Band minimums must be between 0 and 100.");
        if (def.Bands.Select(b => b.Min).Distinct().Count() != def.Bands.Count) errors.Add("Two bands start at the same percentage.");
        if (def.Bands.Count > 0 && def.Bands.Min(b => b.Min) != 0) errors.Add("The lowest band must start at 0 so every mark gets a grade.");
        if (def.PassMark is < 0 or > 100) errors.Add("Pass mark must be between 0 and 100.");
        if (def.Decimals is < 0 or > 4) errors.Add("Decimals must be 0 to 4.");
        if (def.Rounding is not ("half-up" or "floor" or "none")) errors.Add("Rounding must be half-up, floor or none.");
        return errors;
    }

    public static decimal Round(SchemeDefinition def, decimal value)
    {
        var factor = (decimal)Math.Pow(10, def.Decimals);
        return def.Rounding switch
        {
            "half-up" => Math.Round(value, def.Decimals, MidpointRounding.AwayFromZero),
            "floor" => Math.Floor(value * factor) / factor,
            _ => value
        };
    }

    public static Band? BandFor(SchemeDefinition def, decimal percent) =>
        def.Bands.OrderByDescending(b => b.Min).FirstOrDefault(b => percent >= b.Min);

    /// <summary>Weighted percentage across components, then band lookup.</summary>
    public static GradeResult Compute(SchemeDefinition def, IEnumerable<ComponentScore> components)
    {
        decimal weightSum = 0, weighted = 0;
        var complete = true;
        foreach (var c in components)
        {
            if (c.IsExcused || c.Weight <= 0 || c.MaxMarks <= 0) continue;
            decimal marks;
            if (c.IsAbsent)
            {
                if (!def.AbsentCountsAsZero) continue;
                marks = 0;
            }
            else if (c.Marks is null) { complete = false; continue; }
            else marks = Math.Clamp(c.Marks.Value, 0, c.MaxMarks);
            weightSum += c.Weight;
            weighted += c.Weight * marks / c.MaxMarks;
        }
        if (weightSum == 0) return new(null, null, null, null, null, complete);
        return Grade(def, weighted / weightSum * 100) with { Complete = complete };
    }

    public static GradeResult Grade(SchemeDefinition def, decimal rawPercent)
    {
        var percent = Round(def, rawPercent);
        var band = BandFor(def, percent);
        return new(def.Rounding == "none" ? Math.Round(percent, 2) : percent, band?.Grade, band?.Points, band?.Descriptor, percent >= def.PassMark, true);
    }

    /// <summary>Credit-weighted grade point average; subjects without points are left out.</summary>
    public static decimal? Gpa(IEnumerable<(decimal? Points, decimal Credits)> subjects)
    {
        var counted = subjects.Where(s => s.Points is not null && s.Credits > 0).ToList();
        var credits = counted.Sum(s => s.Credits);
        return credits == 0 ? null : Math.Round(counted.Sum(s => s.Points!.Value * s.Credits) / credits, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Starting points a school copies and edits. Real boundaries differ by board, subject and session
    /// (IB and Cambridge publish them per exam series), so these are defaults, not official thresholds.
    /// </summary>
    public static readonly Dictionary<string, (string Name, string ScaleType, SchemeDefinition Def)> Presets = new()
    {
        ["cbse"] = ("CBSE 9-point", "cbse", new SchemeDefinition
        {
            PassMark = 33, Decimals = 0,
            Bands = [new(91, "A1", 10), new(81, "A2", 9), new(71, "B1", 8), new(61, "B2", 7), new(51, "C1", 6), new(41, "C2", 5), new(33, "D", 4), new(0, "E", null, "Needs improvement")]
        }),
        ["ib"] = ("IB 1–7", "ib", new SchemeDefinition
        {
            PassMark = 50, Decimals = 0,
            Bands = [new(80, "7", 7, "Excellent"), new(70, "6", 6, "Very good"), new(60, "5", 5, "Good"), new(50, "4", 4, "Satisfactory"), new(40, "3", 3, "Mediocre"), new(25, "2", 2, "Poor"), new(0, "1", 1, "Very poor")]
        }),
        ["cambridge"] = ("Cambridge IGCSE A*–G", "cambridge", new SchemeDefinition
        {
            PassMark = 40, Decimals = 0,
            Bands = [new(90, "A*"), new(80, "A"), new(70, "B"), new(60, "C"), new(50, "D"), new(40, "E"), new(30, "F"), new(20, "G"), new(0, "U", null, "Ungraded")]
        }),
        ["gpa"] = ("GPA 4.0", "gpa", new SchemeDefinition
        {
            PassMark = 60, Decimals = 1,
            Bands = [new(93, "A", 4.0m), new(90, "A-", 3.7m), new(87, "B+", 3.3m), new(83, "B", 3.0m), new(80, "B-", 2.7m), new(77, "C+", 2.3m), new(73, "C", 2.0m), new(70, "C-", 1.7m), new(60, "D", 1.0m), new(0, "F", 0)]
        }),
        ["percentage"] = ("Percentage", "percentage", new SchemeDefinition
        {
            PassMark = 40, Decimals = 1,
            Bands = [new(75, "Distinction"), new(60, "First class"), new(50, "Second class"), new(40, "Pass"), new(0, "Fail")]
        }),
    };
}
