using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SchoolErp.Api.Shared;

/// <summary>Base for every tenant-owned row: GUID key, TenantId, soft delete, timestamps (UTC).</summary>
public abstract class TenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Who is making the current request. Filled from the JWT; background jobs set it by hand.</summary>
public class CurrentUser
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "system";
    public HashSet<string> Permissions { get; set; } = [];
    public string Scope { get; set; } = Scopes.Tenant;
    public Guid? CampusId { get; set; }
    public Guid? StaffId { get; set; }
    public Guid? GuardianId { get; set; }
    public Guid? StudentId { get; set; }
    public bool IsPlatformAdmin { get; set; }
    public bool IsSupportSession { get; set; }
    public Guid? SupportRequestId { get; set; }
    public string? Ip { get; set; }

    public bool Has(string permission) => Permissions.Contains(permission);
    public bool CanAccessCampus(Guid campusId) => CampusId is null || CampusId == campusId;
}

/// <summary>"Today" for a campus is the date in its own time zone, not the server's.</summary>
public static class Clock
{
    public static DateOnly Today(string? timeZone)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "UTC"))); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return DateOnly.FromDateTime(DateTime.UtcNow); }
    }
}

public static class Scopes
{
    public const string Tenant = "tenant", Campus = "campus", Assigned = "assigned", Children = "children", Self = "self";
    public static readonly string[] All = [Tenant, Campus, Assigned, Children, Self];
}

public static class Perm
{
    public const string AdminSettings = "admin.settings", AdminUsers = "admin.users", AdminImport = "admin.import",
        Admissions = "admissions.manage",
        StudentsRead = "students.read", StudentsWrite = "students.write", StudentsMedical = "students.medical",
        Academics = "academics.manage", TimetableRead = "timetable.read",
        AttendanceMark = "attendance.mark", AttendanceRead = "attendance.read", LeaveApprove = "staff.leave.approve",
        ExamsManage = "exams.manage", ExamsMarks = "exams.marks", ExamsApprove = "exams.approve",
        FeesManage = "fees.manage", FeesRead = "fees.read",
        Announce = "comms.announce", Message = "comms.message", Consent = "consent.manage",
        AuditRead = "audit.read", Privacy = "privacy.manage", Rollover = "rollover.run",
        Payroll = "payroll.manage",
        PortalParent = "portal.parent", PortalStudent = "portal.student";

    public static readonly string[] All =
    [
        AdminSettings, AdminUsers, AdminImport, Admissions, StudentsRead, StudentsWrite, StudentsMedical,
        Academics, TimetableRead, AttendanceMark, AttendanceRead, LeaveApprove, ExamsManage, ExamsMarks, ExamsApprove,
        FeesManage, FeesRead, Announce, Message, Consent, AuditRead, Privacy, Rollover, Payroll, PortalParent, PortalStudent
    ];

    /// <summary>Role templates each school can copy and edit (name, scope, permissions).</summary>
    public static readonly (string Name, string Scope, string[] Perms)[] Templates =
    [
        ("School admin", Scopes.Tenant, All.Where(p => !p.StartsWith("portal.")).ToArray()),
        ("Principal", Scopes.Campus, [StudentsRead, StudentsMedical, TimetableRead, AttendanceRead, LeaveApprove, ExamsApprove, ExamsManage, FeesRead, Announce, Message, Consent, AuditRead]),
        ("Teacher", Scopes.Assigned, [StudentsRead, TimetableRead, AttendanceMark, AttendanceRead, ExamsMarks, Message]),
        ("Accountant", Scopes.Campus, [StudentsRead, FeesManage, FeesRead, Payroll]),
        ("Parent", Scopes.Children, [PortalParent, Message]),
        ("Student", Scopes.Self, [PortalStudent]),
    ];
}

/// <summary>AES-GCM field-level encryption for medical data and ID numbers (NFR-SEC-01).</summary>
public static class FieldCrypto
{
    private static byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes("dev-only-field-key"));
    public static void Configure(string secret) => _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public static string? Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return "enc:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public static string? Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("enc:")) return stored;
        var raw = Convert.FromBase64String(stored[4..]);
        var plain = new byte[raw.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}

public class EncryptedConverter() : ValueConverter<string?, string?>(v => FieldCrypto.Encrypt(v), v => FieldCrypto.Decrypt(v));

/// <summary>RFC 6238 time-based one-time passwords for staff MFA.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Decode(string secret)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in secret.ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) continue;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 255)); bits -= 8; }
        }
        return [.. bytes];
    }

    public static string Code(string secret, DateTime utc, int stepOffset = 0)
    {
        var counter = new DateTimeOffset(utc).ToUnixTimeSeconds() / 30 + stepOffset;
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var hash = HMACSHA1.HashData(Decode(secret), msg);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static bool Verify(string secret, string? code, DateTime utc) =>
        code is { Length: 6 } && new[] { -1, 0, 1 }.Any(d => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Code(secret, utc, d)), Encoding.ASCII.GetBytes(code)));
}
