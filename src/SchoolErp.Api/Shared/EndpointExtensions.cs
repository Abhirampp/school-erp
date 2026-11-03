using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;

namespace SchoolErp.Api.Shared;

public static class EndpointExtensions
{
    /// <summary>Requires a signed-in user holding at least one of the permissions.</summary>
    public static TBuilder Require<TBuilder>(this TBuilder builder, params string[] anyOf) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var user = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            if (user.UserId == Guid.Empty) return Results.Unauthorized();
            if (anyOf.Length > 0 && !anyOf.Any(user.Has)) return Results.Problem("You do not have permission to do this.", statusCode: 403);
            return await next(ctx);
        });

    /// <summary>Any signed-in staff member (not a parent or student portal account).</summary>
    public static TBuilder RequireStaff<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var user = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            if (user.UserId == Guid.Empty) return Results.Unauthorized();
            if (user.Scope is Scopes.Children or Scopes.Self) return Results.Problem("Staff only.", statusCode: 403);
            return await next(ctx);
        });

    public static IResult Bad(string message) => Results.Problem(message, statusCode: 400);
    public static IResult Forbidden(string message = "Not allowed.") => Results.Problem(message, statusCode: 403);

    /// <summary>
    /// List/create/update/delete for a simple configuration entity. Lists accept any Guid property as a
    /// query filter (e.g. ?campusId=...). TenantId and soft-delete state can never be set by the caller.
    /// </summary>
    public static void MapCrud<T>(this IEndpointRouteBuilder app, string path, string? readPermission, string writePermission) where T : TenantEntity
    {
        var g = app.MapGroup(path);
        var list = g.MapGet("", async (HttpRequest req, AppDbContext db) =>
        {
            IQueryable<T> q = db.Set<T>();
            foreach (var (key, value) in req.Query)
            {
                var prop = typeof(T).GetProperties().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (prop is null || !Guid.TryParse(value, out var id)) continue;
                var name = prop.Name;
                if (prop.PropertyType == typeof(Guid)) q = q.Where(e => EF.Property<Guid>(e, name) == id);
                else if (prop.PropertyType == typeof(Guid?)) q = q.Where(e => EF.Property<Guid?>(e, name) == id);
            }
            return await q.OrderBy(e => e.CreatedAt).ToListAsync();
        });
        if (readPermission is null) list.RequireStaff(); else list.Require(readPermission, writePermission);

        g.MapPost("", async (T body, AppDbContext db) =>
        {
            body.Id = Guid.NewGuid();
            body.IsDeleted = false;
            db.Add(body);
            await db.SaveChangesAsync();
            return Results.Ok(body);
        }).Require(writePermission);

        g.MapPut("{id:guid}", async (Guid id, T body, AppDbContext db) =>
        {
            var existing = await db.Set<T>().FirstOrDefaultAsync(e => e.Id == id);
            if (existing is null) return Results.NotFound();
            body.Id = id; body.TenantId = existing.TenantId; body.CreatedAt = existing.CreatedAt; body.IsDeleted = false;
            db.Entry(existing).CurrentValues.SetValues(body);
            await db.SaveChangesAsync();
            return Results.Ok(existing);
        }).Require(writePermission);

        g.MapDelete("{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var existing = await db.Set<T>().FirstOrDefaultAsync(e => e.Id == id);
            if (existing is null) return Results.NotFound();
            db.Remove(existing);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).Require(writePermission);
    }
}

/// <summary>Minimal CSV reader/writer (quoted fields, embedded commas and newlines).</summary>
public static class Csv
{
    public static List<Dictionary<string, string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(f => f.Trim().Length > 0)) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Trim().Length > 0)) rows.Add(row);
        if (rows.Count == 0) return [];
        var header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        return rows.Skip(1).Select(r => header.Select((h, i) => (h, v: i < r.Count ? r[i].Trim() : ""))
            .ToDictionary(x => x.h, x => x.v, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static string Write(IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        static string Cell(object? v)
        {
            var s = v switch { null => "", IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };
            // Neutralise spreadsheet formula injection in exported text.
            if (v is string && s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", header));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Cell)));
        return sb.ToString();
    }
}

/// <summary>Tenant-partitioned local file storage. Swap for regional blob storage in production.</summary>
public class FileStore(IWebHostEnvironment env)
{
    private string Root => Path.Combine(env.ContentRootPath, "App_Data", "uploads");
    private static readonly string[] Allowed = [".pdf", ".png", ".jpg", ".jpeg", ".webp", ".doc", ".docx"];
    public const long MaxBytes = 10 * 1024 * 1024;

    public async Task<(string path, string? error)> Save(Guid tenantId, IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!Allowed.Contains(ext)) return ("", $"File type {ext} is not allowed.");
        if (file.Length > MaxBytes) return ("", "File is larger than 10 MB.");
        var rel = Path.Combine(tenantId.ToString("N"), Guid.NewGuid().ToString("N") + ext);
        var full = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = File.Create(full);
        await file.CopyToAsync(fs);
        return (rel, null);
    }

    public string? Resolve(Guid tenantId, string rel)
    {
        // Stored paths always start with the owning tenant's folder.
        if (!rel.StartsWith(tenantId.ToString("N"))) return null;
        var full = Path.GetFullPath(Path.Combine(Root, rel));
        return full.StartsWith(Path.GetFullPath(Root)) && File.Exists(full) ? full : null;
    }

    public void Delete(Guid tenantId, string rel)
    {
        var full = Resolve(tenantId, rel);
        if (full is not null) File.Delete(full);
    }
}
