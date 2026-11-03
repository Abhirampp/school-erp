using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SchoolErp.Api.Data;
using SchoolErp.Api.Modules.Academics;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Admissions;
using SchoolErp.Api.Modules.Assistant;
using SchoolErp.Api.Modules.Attendance;
using SchoolErp.Api.Modules.Communication;
using SchoolErp.Api.Modules.Directory;
using SchoolErp.Api.Modules.Payroll;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Modules.Identity;
using SchoolErp.Api.Modules.Portal;
using SchoolErp.Api.Modules.Rollover;
using SchoolErp.Api.Modules.Security;
using SchoolErp.Api.Modules.Students;
using SchoolErp.Api.Seed;
using SchoolErp.Api.Shared;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var jwtKey = config["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32) throw new InvalidOperationException("Set Jwt:Key to a secret of at least 32 characters.");
FieldCrypto.Configure(config["Security:FieldEncryptionKey"] ?? throw new InvalidOperationException("Set Security:FieldEncryptionKey."));
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;

builder.Services.AddOpenApi();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddDbContext<AppDbContext>(o =>
{
    var cs = config.GetConnectionString("Default");
    if (config["Database:Provider"] == "postgres") o.UseNpgsql(cs); else o.UseSqlite(cs ?? "Data Source=schoolerp.db");
});
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<FileStore>();
builder.Services.AddScoped<Notifier>();
builder.Services.AddScoped<StudentService>();
builder.Services.AddScoped<FeeService>();
builder.Services.AddScoped<ReportCardService>();
builder.Services.AddScoped<RolloverService>();
builder.Services.AddScoped<AssistantTools>();
builder.Services.AddScoped<AssistantService>();
builder.Services.AddSingleton<IPaymentProvider, MockGatewayProvider>();
builder.Services.AddSingleton<INotificationChannel, InAppChannel>();
foreach (var channel in new[] { "email", "sms", "push" })
    builder.Services.AddSingleton<INotificationChannel>(sp => new LogChannel(channel, sp.GetRequiredService<ILogger<LogChannel>>()));
builder.Services.AddHostedService<NotificationDispatcher>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = "school-erp", ValidAudience = "school-erp", ClockSkew = TimeSpan.FromSeconds(30),
        IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtKey))
    };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new() { PermitLimit = config.GetValue("RateLimits:AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    // Each assistant question can make several paid model calls; cap it per signed-in user.
    o.AddPolicy("assistant", c => RateLimitPartition.GetFixedWindowLimiter("u:" + (c.User.FindFirst("sub")?.Value ?? Ip(c)), _ => new() { PermitLimit = config.GetValue("RateLimits:AssistantPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("public", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    // Per-tenant ceiling so one school cannot starve the others in a regional stamp.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        c.User.FindFirst("tenant")?.Value is { } tenant
            ? RateLimitPartition.GetFixedWindowLimiter("t:" + tenant, _ => new() { PermitLimit = 3000, Window = TimeSpan.FromMinutes(1) })
            : RateLimitPartition.GetNoLimiter("anonymous"));
});

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Resolve the tenant and caller once per request; every query is then filtered by that tenant.
app.Use(async (ctx, next) =>
{
    var user = ctx.RequestServices.GetRequiredService<CurrentUser>();
    TokenService.Populate(user, ctx);
    if (user.IsSupportSession)
    {
        // Support sessions are read-only and stop the moment the school revokes or the approval expires.
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        var grant = await db.SupportAccessRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == user.SupportRequestId && r.TenantId == user.TenantId);
        if (grant is not { Status: "approved" } || grant.ExpiresAt <= DateTime.UtcNow) { ctx.Response.StatusCode = 401; return; }
        if (!HttpMethods.IsGet(ctx.Request.Method)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { detail = "Support access is read-only." }); return; }
    }
    await next();
});

app.MapGet("/api/health", () => new { status = "ok", time = DateTime.UtcNow });
IdentityEndpoints.Map(app);
AdminEndpoints.Map(app);
AdmissionEndpoints.Map(app);
StudentEndpoints.Map(app);
AcademicsEndpoints.Map(app);
AttendanceEndpoints.Map(app);
ExamEndpoints.Map(app);
FeeEndpoints.Map(app);
CommunicationEndpoints.Map(app);
SecurityEndpoints.Map(app);
RolloverEndpoints.Map(app);
PortalEndpoints.Map(app);
DirectoryEndpoints.Map(app);
PayrollEndpoints.Map(app);
AssistantEndpoints.Map(app);
app.MapFallback("/api/{**rest}", () => Results.NotFound());
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html"))) app.MapFallbackToFile("index.html");
// In development the web app is served by `npm start` on port 4200; send browsers that open the API port there.
else if (app.Environment.IsDevelopment()) app.MapFallback((HttpContext ctx) => Results.Redirect("http://localhost:4200" + ctx.Request.Path + ctx.Request.QueryString));

using (var scope = app.Services.CreateScope())
{
    // Development convenience. Production uses reviewed EF Core migrations as a separate pipeline step.
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}
if (config.GetValue<bool>("Seed:Demo")) await DemoSeeder.Run(app.Services, config["Seed:Password"] ?? throw new InvalidOperationException("Set Seed:Password."));

app.Run();

public partial class Program;
