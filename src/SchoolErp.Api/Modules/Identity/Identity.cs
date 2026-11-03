using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Identity;

public class TokenService(IConfiguration config)
{
    public static readonly PasswordHasher<User> Hasher = new();
    public SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));

    public string Create(User u, Role? role, Guid tenantId, DateTime expires, bool support = false, string? permissionsOverride = null, Guid? supportRequestId = null)
    {
        var claims = new List<Claim>
        {
            new("sub", u.Id.ToString()), new("name", u.DisplayName), new("tenant", tenantId.ToString()),
            new("perms", permissionsOverride ?? role?.Permissions ?? ""), new("scope", role?.Scope ?? Scopes.Tenant),
        };
        if (u.CampusId is { } c) claims.Add(new("campus", c.ToString()));
        if (u.StaffId is { } s) claims.Add(new("staff", s.ToString()));
        if (u.GuardianId is { } g) claims.Add(new("guardian", g.ToString()));
        if (u.StudentId is { } st) claims.Add(new("student", st.ToString()));
        if (u.IsPlatformAdmin) claims.Add(new("platform", "1"));
        if (support) claims.Add(new("support", supportRequestId?.ToString() ?? "1"));
        var jwt = new JwtSecurityToken("school-erp", "school-erp", claims, expires: expires,
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>Fills the scoped CurrentUser from the validated JWT.</summary>
    public static void Populate(CurrentUser cu, HttpContext http)
    {
        cu.Ip = http.Connection.RemoteIpAddress?.ToString();
        var p = http.User;
        if (p.Identity?.IsAuthenticated != true) return;
        Guid? G(string type) => Guid.TryParse(p.FindFirstValue(type), out var g) ? g : null;
        cu.UserId = G("sub") ?? Guid.Empty;
        cu.TenantId = G("tenant") ?? Guid.Empty;
        cu.Name = p.FindFirstValue("name") ?? "";
        cu.Permissions = [.. (p.FindFirstValue("perms") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];
        cu.Scope = p.FindFirstValue("scope") ?? Scopes.Tenant;
        cu.CampusId = G("campus"); cu.StaffId = G("staff"); cu.GuardianId = G("guardian"); cu.StudentId = G("student");
        cu.IsPlatformAdmin = p.FindFirstValue("platform") == "1";
        cu.IsSupportSession = p.FindFirstValue("support") is not null;
        cu.SupportRequestId = G("support");
    }
}

public static class IdentityEndpoints
{
    public record LoginRequest(string Email, string Password, string? MfaCode);
    public record OtpRequest(string Identifier);
    public record OtpVerify(string Identifier, string Code);
    public record MfaEnable(string Code);
    public record ChangePassword(string Current, string Next);

    private static object Session(User u, Role? role, string token) => new
    {
        token,
        user = new
        {
            u.Id, u.DisplayName, u.Email, u.Locale, u.CampusId, u.StaffId, u.GuardianId, u.StudentId, u.IsPlatformAdmin, u.MfaEnabled,
            role = role?.Name ?? "Platform admin", scope = role?.Scope ?? Scopes.Tenant,
            permissions = (role?.Permissions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        }
    };

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static async Task<object> SignIn(AppDbContext db, TokenService tokens, CurrentUser cu, User u, string method)
    {
        var role = await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == u.RoleId && !r.IsDeleted);
        u.LastLoginAt = DateTime.UtcNow;
        cu.TenantId = u.TenantId; cu.UserId = u.Id; cu.Name = u.DisplayName;
        db.SuppressAudit = true;
        db.Audit("login", "User", u.Id.ToString(), new { method });
        await db.SaveChangesAsync();
        return Session(u, role, tokens.Create(u, role, u.TenantId, DateTime.UtcNow.AddHours(8)));
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("login", async (LoginRequest req, AppDbContext db, TokenService tokens, CurrentUser cu) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Email == email && !x.IsDeleted && x.IsActive);
            // Same response for unknown user and wrong password.
            if (u is null || TokenService.Hasher.VerifyHashedPassword(u, u.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
                return Results.Problem("Email or password is incorrect.", statusCode: 401);
            if (u.MfaEnabled)
            {
                if (string.IsNullOrWhiteSpace(req.MfaCode)) return Results.Ok(new { mfaRequired = true });
                if (!Totp.Verify(u.MfaSecret!, req.MfaCode.Trim(), DateTime.UtcNow)) return Results.Problem("The authenticator code is incorrect.", statusCode: 401);
            }
            return Results.Ok(await SignIn(db, tokens, cu, u, u.MfaEnabled ? "password+mfa" : "password"));
        }).RequireRateLimiting("auth");

        // Parents sign in with a one-time code sent to their email or phone.
        g.MapPost("otp/request", async (OtpRequest req, AppDbContext db, CurrentUser cu, IWebHostEnvironment env) =>
        {
            var id = req.Identifier.Trim().ToLowerInvariant();
            var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => (x.Email == id || x.Phone == id) && !x.IsDeleted && x.IsActive && x.GuardianId != null);
            string? devCode = null;
            if (u is not null)
            {
                var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                cu.TenantId = u.TenantId;
                db.OtpCodes.Add(new OtpCode { UserId = u.Id, CodeHash = Hash(code), ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
                var byPhone = u.Phone == id;
                db.Notifications.Add(new Notification { UserId = u.Id, Channel = byPhone ? "sms" : "email", Address = byPhone ? u.Phone : u.Email, Subject = "Your sign-in code", Body = $"Your sign-in code is {code}. It expires in 10 minutes.", Category = "otp" });
                await db.SaveChangesAsync();
                if (env.IsDevelopment()) devCode = code;
            }
            // Always the same answer, so the endpoint does not reveal which accounts exist.
            return Results.Ok(new { sent = true, devCode });
        }).RequireRateLimiting("auth");

        g.MapPost("otp/verify", async (OtpVerify req, AppDbContext db, TokenService tokens, CurrentUser cu) =>
        {
            var id = req.Identifier.Trim().ToLowerInvariant();
            var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => (x.Email == id || x.Phone == id) && !x.IsDeleted && x.IsActive && x.GuardianId != null);
            if (u is null) return Results.Problem("The code is incorrect or has expired.", statusCode: 401);
            var otp = await db.OtpCodes.IgnoreQueryFilters().Where(o => o.UserId == u.Id && !o.IsDeleted && o.ExpiresAt > DateTime.UtcNow).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync();
            if (otp is null || otp.Attempts >= 5 || otp.CodeHash != Hash(req.Code.Trim()))
            {
                if (otp is not null) { otp.Attempts++; cu.TenantId = u.TenantId; await db.SaveChangesAsync(); }
                return Results.Problem("The code is incorrect or has expired.", statusCode: 401);
            }
            otp.IsDeleted = true;
            return Results.Ok(await SignIn(db, tokens, cu, u, "otp"));
        }).RequireRateLimiting("auth");

        g.MapGet("me", async (AppDbContext db, CurrentUser cu, TokenService tokens) =>
        {
            var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == cu.UserId);
            if (u is null) return Results.Unauthorized();
            var role = await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == u.RoleId);
            return Results.Ok(Session(u, role, ""));
        }).Require();

        g.MapPost("mfa/setup", async (AppDbContext db, CurrentUser cu) =>
        {
            var u = await db.Users.IgnoreQueryFilters().FirstAsync(x => x.Id == cu.UserId);
            u.MfaSecret = Totp.NewSecret();
            u.MfaEnabled = false;
            await db.SaveChangesAsync();
            return Results.Ok(new { secret = u.MfaSecret, uri = $"otpauth://totp/GlobalSchoolERP:{Uri.EscapeDataString(u.Email)}?secret={u.MfaSecret}&issuer=GlobalSchoolERP" });
        }).Require();

        g.MapPost("mfa/enable", async (MfaEnable req, AppDbContext db, CurrentUser cu) =>
        {
            var u = await db.Users.IgnoreQueryFilters().FirstAsync(x => x.Id == cu.UserId);
            if (u.MfaSecret is null || !Totp.Verify(u.MfaSecret, req.Code.Trim(), DateTime.UtcNow)) return EndpointExtensions.Bad("The code is incorrect.");
            u.MfaEnabled = true;
            await db.SaveChangesAsync();
            return Results.Ok(new { enabled = true });
        }).Require();

        g.MapPost("password", async (ChangePassword req, AppDbContext db, CurrentUser cu) =>
        {
            var u = await db.Users.IgnoreQueryFilters().FirstAsync(x => x.Id == cu.UserId);
            if (TokenService.Hasher.VerifyHashedPassword(u, u.PasswordHash, req.Current) == PasswordVerificationResult.Failed) return EndpointExtensions.Bad("Current password is incorrect.");
            if (req.Next.Length < 10) return EndpointExtensions.Bad("Use at least 10 characters.");
            u.PasswordHash = TokenService.Hasher.HashPassword(u, req.Next);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require();

        // SSO needs each school's Google Workspace / Microsoft Entra client registration.
        g.MapGet("sso/{provider}", (string provider) => Results.Problem($"SSO with {provider} is not configured for this deployment.", statusCode: 501));
    }
}
