using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Identity;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Security;

public static class TenantProvisioner
{
    /// <summary>Creates a tenant with the role templates and its first school admin.</summary>
    public static async Task<(Tenant tenant, User admin)> Create(AppDbContext db, CurrentUser cu, string name, string slug, string region, string plan, string adminEmail, string adminName, string password)
    {
        var tenant = new Tenant { Name = name, Slug = slug, Region = region, Plan = plan };
        db.Tenants.Add(tenant);
        var previous = cu.TenantId;
        cu.TenantId = tenant.Id; // new rows below belong to the new tenant
        Role? adminRole = null;
        foreach (var (roleName, scope, perms) in Perm.Templates)
        {
            var role = new Role { Name = roleName, Scope = scope, Permissions = string.Join(",", perms), IsTemplate = true };
            db.Add(role);
            adminRole ??= role;
        }
        var admin = new User { Email = adminEmail.Trim().ToLowerInvariant(), DisplayName = adminName, RoleId = adminRole!.Id };
        admin.PasswordHash = TokenService.Hasher.HashPassword(admin, password);
        db.Add(admin);
        await db.SaveChangesAsync();
        cu.TenantId = previous;
        return (tenant, admin);
    }
}

public static class SecurityEndpoints
{
    public record EraseRequest(string ConfirmAdmissionNumber);
    public record Decision(bool Approve);
    public record TenantRequest(string Name, string Slug, string Region, string Plan, string AdminEmail, string AdminName, string AdminPassword);
    public record SupportRequest(Guid TenantId, string Reason, int Hours);

    /// <summary>What a support session may do: read configuration and records. Never medical data, exports or writes.</summary>
    private static readonly string[] SupportPermissions =
        [Perm.AdminSettings, Perm.AdminUsers, Perm.Admissions, Perm.StudentsRead, Perm.Academics, Perm.TimetableRead, Perm.AttendanceRead, Perm.FeesRead, Perm.AuditRead];

    private static TBuilder RequirePlatform<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder => b.AddEndpointFilter(async (ctx, next) =>
    {
        var user = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
        return user.IsPlatformAdmin && !user.IsSupportSession ? await next(ctx) : Results.Problem("Platform administrators only.", statusCode: 403);
    });

    public static void Map(IEndpointRouteBuilder app)
    {
        // ---- Audit log (read-only: there is no endpoint that edits or deletes audit rows) ----
        app.MapGet("/api/audit", async (string? entityType, string? action, string? search, DateTime? from, DateTime? to, int? take, AppDbContext db) =>
        {
            var q = db.AuditEvents.Where(a => a.TenantId == db.CurrentTenantId);
            if (!string.IsNullOrEmpty(entityType)) q = q.Where(a => a.EntityType == entityType);
            if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.Action == action);
            if (!string.IsNullOrEmpty(search)) q = q.Where(a => a.ActorName.Contains(search) || (a.EntityId != null && a.EntityId.Contains(search)));
            if (from is { } f) q = q.Where(a => a.At >= f);
            if (to is { } t) q = q.Where(a => a.At <= t);
            return await q.OrderByDescending(a => a.At).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync();
        }).Require(Perm.AuditRead);

        // ---- School-controlled export and deletion per student (FR-09-03) ----
        var privacy = app.MapGroup("/api/privacy/students/{id:guid}").Require(Perm.Privacy);

        privacy.MapGet("export", async (Guid id, AppDbContext db) =>
        {
            var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var guardianIds = await db.StudentGuardians.Where(x => x.StudentId == id).Select(x => x.GuardianId).ToListAsync();
            var enrollmentIds = await db.Enrollments.Where(e => e.StudentId == id).Select(e => e.Id).ToListAsync();
            var invoiceIds = await db.Invoices.Where(i => i.StudentId == id).Select(i => i.Id).ToListAsync();
            var bundle = new
            {
                exportedAt = DateTime.UtcNow,
                student = s,
                guardians = await db.Guardians.Where(x => guardianIds.Contains(x.Id)).ToListAsync(),
                enrollments = await db.Enrollments.Where(e => e.StudentId == id).ToListAsync(),
                attendance = await db.AttendanceRecords.Where(a => a.StudentId == id).ToListAsync(),
                scores = await db.Scores.Where(x => enrollmentIds.Contains(x.EnrollmentId)).ToListAsync(),
                invoices = await db.Invoices.Where(i => i.StudentId == id).ToListAsync(),
                payments = await db.Payments.Where(p => invoiceIds.Contains(p.InvoiceId)).ToListAsync(),
                consents = await db.Consents.Where(c => c.StudentId == id).ToListAsync(),
                documents = await db.StudentDocuments.Where(d => d.StudentId == id).Select(d => new { d.FileName, d.Kind, d.Size, d.CreatedAt }).ToListAsync()
            };
            db.Audit("export", "Student", id.ToString(), new { kind = "full data export" });
            await db.SaveChangesAsync();
            return Results.Json(bundle, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }, "application/json");
        });

        // Erasure removes personal data and files. Invoices and ledger rows stay (financial record-keeping law) but no longer identify the child.
        privacy.MapPost("erase", async (Guid id, EraseRequest req, AppDbContext db, FileStore files) =>
        {
            var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            if (!string.Equals(req.ConfirmAdmissionNumber?.Trim(), s.AdmissionNumber, StringComparison.OrdinalIgnoreCase)) return EndpointExtensions.Bad("Type the student's admission number to confirm.");
            if (await db.Enrollments.AnyAsync(e => e.StudentId == id && e.Status == "active")) return EndpointExtensions.Bad("Withdraw the student before erasing their data.");

            if (s.PhotoPath is not null) files.Delete(db.CurrentTenantId, s.PhotoPath);
            foreach (var d in await db.StudentDocuments.Where(x => x.StudentId == id).ToListAsync()) { files.Delete(db.CurrentTenantId, d.StoragePath); db.Remove(d); }
            var enrollmentIds = await db.Enrollments.Where(e => e.StudentId == id).Select(e => e.Id).ToListAsync();
            db.RemoveRange(await db.AttendanceRecords.Where(a => a.StudentId == id).ToListAsync());
            db.RemoveRange(await db.Scores.Where(x => enrollmentIds.Contains(x.EnrollmentId)).ToListAsync());
            db.RemoveRange(await db.Consents.Where(c => c.StudentId == id).ToListAsync());
            var threads = await db.MessageThreads.Where(t => t.StudentId == id).ToListAsync();
            var threadIds = threads.Select(t => t.Id).ToList();
            db.RemoveRange(await db.Messages.Where(m => threadIds.Contains(m.ThreadId)).ToListAsync());
            db.RemoveRange(threads);

            // Guardians with no other child at the school are erased too, and their logins closed.
            var links = await db.StudentGuardians.Where(x => x.StudentId == id).ToListAsync();
            foreach (var link in links)
            {
                if (!await db.StudentGuardians.AnyAsync(x => x.GuardianId == link.GuardianId && x.StudentId != id) && await db.Guardians.FirstOrDefaultAsync(x => x.Id == link.GuardianId) is { } guardian)
                {
                    guardian.FirstName = "Erased"; guardian.LastName = ""; guardian.NativeName = null; guardian.Email = null; guardian.Phone = null; guardian.Occupation = null; guardian.CustomFieldsJson = "{}";
                    foreach (var u in await db.Users.Where(u => u.GuardianId == guardian.Id).ToListAsync()) { u.IsActive = false; u.Email = $"erased-{u.Id:N}@invalid"; u.Phone = null; u.DisplayName = "Erased"; }
                }
            }
            s.FirstName = "Erased"; s.LastName = ""; s.NativeName = null; s.DateOfBirth = null; s.Gender = null; s.Email = null; s.Phone = null; s.PhotoPath = null; s.Category = null;
            s.NationalId = null; s.MedicalNotes = null; s.SpecialNeeds = null; s.CustomFieldsJson = "{}"; s.LeavingReason = null; s.Status = "erased";
            db.Audit("erase", "Student", id.ToString());
            await db.SaveChangesAsync();
            return Results.Ok(new { erased = true });
        });

        // ---- Support access: the school decides (FR-09-04) ----
        var support = app.MapGroup("/api/security/support-requests").Require(Perm.AdminSettings);
        support.MapGet("", async (AppDbContext db) => await db.SupportAccessRequests.Where(r => r.TenantId == db.CurrentTenantId).OrderByDescending(r => r.RequestedAt).ToListAsync());
        support.MapPost("{id:guid}/decision", async (Guid id, Decision req, AppDbContext db, CurrentUser user) =>
        {
            var r = await db.SupportAccessRequests.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == db.CurrentTenantId);
            if (r is null) return Results.NotFound();
            if (r.Status != "pending") return EndpointExtensions.Bad("This request has already been decided.");
            r.Status = req.Approve ? "approved" : "denied"; r.DecidedByName = user.Name;
            if (req.Approve) r.ExpiresAt = DateTime.UtcNow.AddHours(r.Hours);
            db.Audit("support-access", "SupportAccessRequest", id.ToString(), new { decision = r.Status, r.RequestedByName, r.ExpiresAt });
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });
        support.MapPost("{id:guid}/revoke", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var r = await db.SupportAccessRequests.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == db.CurrentTenantId);
            if (r is null) return Results.NotFound();
            r.Status = "revoked"; r.DecidedByName = user.Name;
            db.Audit("support-access", "SupportAccessRequest", id.ToString(), new { decision = "revoked" });
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });

        // ---- Platform control plane: tenant directory only, never student data ----
        var platform = app.MapGroup("/api/platform").RequirePlatform();
        platform.MapGet("tenants", async (AppDbContext db) => await db.Tenants.OrderBy(t => t.Name).ToListAsync());
        platform.MapPost("tenants", async (TenantRequest req, AppDbContext db, CurrentUser cu) =>
        {
            var slug = req.Slug.Trim().ToLowerInvariant();
            if (slug.Length < 3 || !slug.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return EndpointExtensions.Bad("Slug must be at least 3 letters, digits or hyphens.");
            if (await db.Tenants.AnyAsync(t => t.Slug == slug)) return EndpointExtensions.Bad("That slug is taken.");
            if (req.AdminPassword.Length < 10) return EndpointExtensions.Bad("Admin password must be at least 10 characters.");
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == req.AdminEmail.Trim().ToLower() && !u.IsDeleted)) return EndpointExtensions.Bad("That admin email is already in use.");
            var (tenant, _) = await TenantProvisioner.Create(db, cu, req.Name, slug, req.Region, req.Plan, req.AdminEmail, req.AdminName, req.AdminPassword);
            return Results.Ok(tenant);
        });
        platform.MapGet("support-requests", async (AppDbContext db) => await db.SupportAccessRequests.OrderByDescending(r => r.RequestedAt).Take(200).ToListAsync());
        platform.MapPost("support-requests", async (SupportRequest req, AppDbContext db, CurrentUser cu) =>
        {
            if (!await db.Tenants.AnyAsync(t => t.Id == req.TenantId)) return EndpointExtensions.Bad("Unknown tenant.");
            if (string.IsNullOrWhiteSpace(req.Reason)) return EndpointExtensions.Bad("Give the school a reason.");
            var r = new SupportAccessRequest { TenantId = req.TenantId, RequestedById = cu.UserId, RequestedByName = cu.Name, Reason = req.Reason, Hours = Math.Clamp(req.Hours, 1, 72) };
            db.SupportAccessRequests.Add(r);
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });
        // Exchanges an approved, unexpired request for a read-only token that dies when the approval does.
        platform.MapPost("support-requests/{id:guid}/session", async (Guid id, AppDbContext db, CurrentUser cu, TokenService tokens) =>
        {
            var r = await db.SupportAccessRequests.FirstOrDefaultAsync(x => x.Id == id && x.RequestedById == cu.UserId);
            if (r is null) return Results.NotFound();
            if (r.Status != "approved" || r.ExpiresAt is not { } expires || expires <= DateTime.UtcNow) return EndpointExtensions.Forbidden("The school has not approved this request, or the approval has expired.");
            var me = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == cu.UserId);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == r.TenantId);
            cu.TenantId = r.TenantId;
            db.Audit("support-access", "SupportAccessRequest", id.ToString(), new { started = true, by = cu.Name });
            await db.SaveChangesAsync();
            var token = tokens.Create(me, null, r.TenantId, expires, support: true, permissionsOverride: string.Join(",", SupportPermissions), supportRequestId: id);
            return Results.Ok(new { token, expiresAt = expires, tenant = tenant.Name, permissions = SupportPermissions });
        });
    }
}
