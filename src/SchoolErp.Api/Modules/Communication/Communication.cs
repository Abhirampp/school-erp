using Microsoft.EntityFrameworkCore;
using SchoolErp.Api.Data;
using SchoolErp.Api.Shared;

namespace SchoolErp.Api.Modules.Communication;

/// <summary>A delivery channel. Add a provider (SendGrid, an SMS gateway, FCM) by implementing this.</summary>
public interface INotificationChannel
{
    string Name { get; }
    Task Send(Notification n);
}

/// <summary>In-app notifications are read straight from the table; nothing to deliver.</summary>
public class InAppChannel : INotificationChannel
{
    public string Name => "inapp";
    public Task Send(Notification n) => Task.CompletedTask;
}

/// <summary>Development stand-in for email, SMS and push: writes the message to the log.</summary>
public class LogChannel(string name, ILogger<LogChannel> log) : INotificationChannel
{
    public string Name => name;
    public Task Send(Notification n)
    {
        log.LogInformation("[{Channel}] to {Address}: {Subject} | {Body}", name, n.Address ?? n.UserId?.ToString(), n.Subject, n.Body);
        return Task.CompletedTask;
    }
}

/// <summary>Queues notifications in the outbox table; the dispatcher delivers them.</summary>
public class Notifier(AppDbContext db)
{
    public void ToUser(Guid userId, string subject, string body, string category) =>
        db.Notifications.Add(new Notification { UserId = userId, Channel = "inapp", Subject = subject, Body = body, Category = category });

    public async Task<int> ToGuardiansOf(IEnumerable<Guid> studentIds, string subject, string body, string category, params string[] channels)
    {
        var ids = studentIds.Distinct().ToList();
        var guardianIds = await db.StudentGuardians.Where(sg => ids.Contains(sg.StudentId)).Select(sg => sg.GuardianId).Distinct().ToListAsync();
        var guardians = await db.Guardians.Where(g => guardianIds.Contains(g.Id)).ToListAsync();
        var users = (await db.Users.Where(u => u.GuardianId != null).ToListAsync()).Where(u => guardianIds.Contains(u.GuardianId!.Value)).ToDictionary(u => u.GuardianId!.Value);
        foreach (var g in guardians)
        {
            users.TryGetValue(g.Id, out var user);
            foreach (var ch in channels)
            {
                var address = ch switch { "email" => g.Email, "sms" => g.Phone, _ => null };
                if (ch is "email" or "sms" && string.IsNullOrWhiteSpace(address)) continue;
                if (ch is "inapp" or "push" && user is null) continue;
                db.Notifications.Add(new Notification { UserId = user?.Id, Channel = ch, Address = address, Subject = subject, Body = body, Category = category });
            }
        }
        return guardians.Count;
    }
}

/// <summary>Delivers pending outbox rows every few seconds, across all tenants.</summary>
public class NotificationDispatcher(IServiceScopeFactory scopes, IEnumerable<INotificationChannel> channels, ILogger<NotificationDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var byName = channels.ToDictionary(c => c.Name);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pending = await db.Notifications.IgnoreQueryFilters().Where(n => n.Status == "pending" && !n.IsDeleted).OrderBy(n => n.CreatedAt).Take(200).ToListAsync(stop);
                foreach (var n in pending)
                {
                    try
                    {
                        if (!byName.TryGetValue(n.Channel, out var ch)) throw new InvalidOperationException($"No channel '{n.Channel}'");
                        await ch.Send(n);
                        n.Status = "sent"; n.SentAt = DateTime.UtcNow;
                    }
                    catch (Exception ex) { n.Status = "failed"; n.Error = ex.Message; }
                }
                if (pending.Count > 0) await db.SaveChangesAsync(stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Notification dispatch failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop); } catch (OperationCanceledException) { }
        }
    }
}

public static class Audience
{
    /// <summary>Active students in a campus, grade level or section.</summary>
    public static async Task<List<Guid>> StudentIds(AppDbContext db, Guid campusId, string type, Guid? id)
    {
        var yearIds = await db.AcademicYears.Where(y => y.CampusId == campusId && y.Status == "active").Select(y => y.Id).ToListAsync();
        var sections = db.Sections.Where(s => yearIds.Contains(s.AcademicYearId));
        if (type == "grade") sections = sections.Where(s => s.GradeLevelId == id);
        if (type == "section") sections = sections.Where(s => s.Id == id);
        var sectionIds = await sections.Select(s => s.Id).ToListAsync();
        return await db.Enrollments.Where(e => sectionIds.Contains(e.SectionId) && e.Status == "active").Select(e => e.StudentId).Distinct().ToListAsync();
    }
}

public static class CommunicationEndpoints
{
    public record AnnouncementRequest(Guid CampusId, string Title, string Body, string AudienceType, Guid? AudienceId, string[] Channels);
    public record ThreadRequest(Guid StudentId, Guid RecipientUserId, string Subject, string Body);
    public record MessageRequest(string Body);
    private static readonly string[] ValidChannels = ["inapp", "email", "sms", "push"];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/announcements", async (Guid campusId, AppDbContext db) =>
            await db.Announcements.Where(a => a.CampusId == campusId).OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync()).RequireStaff();

        app.MapPost("/api/announcements", async (AnnouncementRequest req, AppDbContext db, Notifier notifier, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(req.CampusId)) return EndpointExtensions.Forbidden();
            if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Body)) return EndpointExtensions.Bad("Title and message are required.");
            var channels = req.Channels.Where(ValidChannels.Contains).Distinct().ToArray();
            if (channels.Length == 0) channels = ["inapp"];
            var students = await Audience.StudentIds(db, req.CampusId, req.AudienceType, req.AudienceId);
            var count = await notifier.ToGuardiansOf(students, req.Title, req.Body, "announcement", channels);
            var a = new Announcement { CampusId = req.CampusId, Title = req.Title, Body = req.Body, AudienceType = req.AudienceType, AudienceId = req.AudienceId, Channels = string.Join(",", channels), SentByName = user.Name, RecipientCount = count };
            db.Add(a);
            await db.SaveChangesAsync();
            return Results.Ok(a);
        }).Require(Perm.Announce);

        // Delivery tracking
        app.MapGet("/api/notifications/log", async (AppDbContext db) =>
            await db.Notifications.Where(n => n.Category != "otp").OrderByDescending(n => n.CreatedAt).Take(200)
                .Select(n => new { n.Id, n.Channel, n.Address, n.Subject, n.Category, n.Status, n.SentAt, n.CreatedAt, n.Error }).ToListAsync()).Require(Perm.Announce, Perm.AuditRead);

        app.MapGet("/api/notifications", async (AppDbContext db, CurrentUser user) =>
            await db.Notifications.Where(n => n.UserId == user.UserId && n.Channel == "inapp").OrderByDescending(n => n.CreatedAt).Take(50)
                .Select(n => new { n.Id, n.Subject, n.Body, n.Category, n.CreatedAt, n.ReadAt }).ToListAsync()).Require();

        app.MapPost("/api/notifications/read", async (AppDbContext db, CurrentUser user) =>
        {
            var unread = await db.Notifications.Where(n => n.UserId == user.UserId && n.Channel == "inapp" && n.ReadAt == null).ToListAsync();
            unread.ForEach(n => n.ReadAt = DateTime.UtcNow);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).Require();

        var m = app.MapGroup("/api/messages").Require(Perm.Message);

        // Who can this user write to about a student? Parents: the child's teachers. Staff: the child's guardians.
        m.MapGet("contacts", async (Guid studentId, AppDbContext db, CurrentUser user) =>
        {
            if (user.GuardianId is { } gid)
            {
                if (!await db.StudentGuardians.AnyAsync(sg => sg.StudentId == studentId && sg.GuardianId == gid)) return EndpointExtensions.Forbidden();
                var sectionIds = await db.Enrollments.Where(e => e.StudentId == studentId && e.Status == "active").Select(e => e.SectionId).ToListAsync();
                var staffIds = await db.ClassSubjects.Where(c => sectionIds.Contains(c.SectionId) && c.TeacherId != null).Select(c => c.TeacherId!.Value).ToListAsync();
                staffIds.AddRange(await db.Sections.Where(s => sectionIds.Contains(s.Id) && s.ClassTeacherId != null).Select(s => s.ClassTeacherId!.Value).ToListAsync());
                var users = (await db.Users.Where(u => u.StaffId != null).ToListAsync()).Where(u => staffIds.Contains(u.StaffId!.Value));
                return Results.Ok(users.Select(u => new { userId = u.Id, name = u.DisplayName }));
            }
            var guardianIds = await db.StudentGuardians.Where(sg => sg.StudentId == studentId).Select(sg => sg.GuardianId).ToListAsync();
            var parents = (await db.Users.Where(u => u.GuardianId != null).ToListAsync()).Where(u => guardianIds.Contains(u.GuardianId!.Value));
            return Results.Ok(parents.Select(u => new { userId = u.Id, name = u.DisplayName }));
        });

        m.MapGet("threads", async (AppDbContext db, CurrentUser user) =>
        {
            var threads = await db.MessageThreads.Where(t => t.StaffUserId == user.UserId || t.GuardianUserId == user.UserId).OrderByDescending(t => t.LastMessageAt).ToListAsync();
            var studentIds = threads.Select(t => t.StudentId).ToList();
            var students = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName);
            var userIds = threads.SelectMany(t => new[] { t.StaffUserId, t.GuardianUserId }).ToList();
            var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            return threads.Select(t => new { t.Id, t.Subject, t.LastMessageAt, t.StudentId, student = students.GetValueOrDefault(t.StudentId), with = names.GetValueOrDefault(t.StaffUserId == user.UserId ? t.GuardianUserId : t.StaffUserId) });
        });

        m.MapPost("threads", async (ThreadRequest req, AppDbContext db, Notifier notifier, CurrentUser user) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body)) return EndpointExtensions.Bad("Message is required.");
            var other = await db.Users.FirstOrDefaultAsync(u => u.Id == req.RecipientUserId);
            if (other is null) return EndpointExtensions.Bad("Recipient not found.");
            var isParent = user.GuardianId is not null;
            // One side must be a guardian of the student, the other a staff member.
            var guardianId = isParent ? user.GuardianId : other.GuardianId;
            var staffSide = isParent ? other.StaffId : user.StaffId ?? (user.Scope is Scopes.Tenant or Scopes.Campus ? Guid.Empty : null);
            if (guardianId is null || staffSide is null || !await db.StudentGuardians.AnyAsync(sg => sg.StudentId == req.StudentId && sg.GuardianId == guardianId))
                return EndpointExtensions.Forbidden("Messages are only between a student's guardians and school staff.");
            var t = new MessageThread { StudentId = req.StudentId, Subject = req.Subject, LastMessageAt = DateTime.UtcNow, StaffUserId = isParent ? other.Id : user.UserId, GuardianUserId = isParent ? user.UserId : other.Id };
            db.Add(t);
            db.Add(new Message { ThreadId = t.Id, SenderUserId = user.UserId, SenderName = user.Name, Body = req.Body });
            notifier.ToUser(other.Id, $"New message from {user.Name}", req.Subject, "message");
            await db.SaveChangesAsync();
            return Results.Ok(t);
        });

        m.MapGet("threads/{id:guid}", async (Guid id, AppDbContext db, CurrentUser user) =>
        {
            var t = await db.MessageThreads.FirstOrDefaultAsync(x => x.Id == id && (x.StaffUserId == user.UserId || x.GuardianUserId == user.UserId));
            if (t is null) return Results.NotFound();
            var messages = await db.Messages.Where(x => x.ThreadId == id).OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.SenderUserId, x.SenderName, x.Body, x.CreatedAt }).ToListAsync();
            return Results.Ok(new { thread = t, messages });
        });

        m.MapPost("threads/{id:guid}", async (Guid id, MessageRequest req, AppDbContext db, Notifier notifier, CurrentUser user) =>
        {
            var t = await db.MessageThreads.FirstOrDefaultAsync(x => x.Id == id && (x.StaffUserId == user.UserId || x.GuardianUserId == user.UserId));
            if (t is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Body)) return EndpointExtensions.Bad("Message is required.");
            db.Add(new Message { ThreadId = id, SenderUserId = user.UserId, SenderName = user.Name, Body = req.Body });
            t.LastMessageAt = DateTime.UtcNow;
            notifier.ToUser(t.StaffUserId == user.UserId ? t.GuardianUserId : t.StaffUserId, $"New message from {user.Name}", t.Subject, "message");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Consent forms
        app.MapGet("/api/consent-forms", async (Guid campusId, AppDbContext db) =>
        {
            var forms = await db.ConsentForms.Where(f => f.CampusId == campusId).OrderByDescending(f => f.CreatedAt).ToListAsync();
            var formIds = forms.Select(f => f.Id).ToList();
            var counts = (await db.Consents.Where(c => formIds.Contains(c.ConsentFormId)).ToListAsync()).GroupBy(c => c.ConsentFormId)
                .ToDictionary(x => x.Key, x => new { granted = x.Count(c => c.Decision == "granted" && c.WithdrawnAt == null), declined = x.Count(c => c.Decision == "declined"), withdrawn = x.Count(c => c.WithdrawnAt != null) });
            return forms.Select(f => new { form = f, counts = counts.GetValueOrDefault(f.Id) });
        }).RequireStaff();

        app.MapPost("/api/consent-forms", async (ConsentForm form, AppDbContext db, Notifier notifier, CurrentUser user) =>
        {
            if (!user.CanAccessCampus(form.CampusId)) return EndpointExtensions.Forbidden();
            form.Id = Guid.NewGuid(); form.IsDeleted = false;
            db.Add(form);
            var students = await Audience.StudentIds(db, form.CampusId, form.AudienceType, form.AudienceId);
            await notifier.ToGuardiansOf(students, $"Consent requested: {form.Title}", "Please review and respond in the parent portal.", "consent", "inapp", "email");
            await db.SaveChangesAsync();
            return Results.Ok(form);
        }).Require(Perm.Consent);

        app.MapGet("/api/consent-forms/{id:guid}/responses", async (Guid id, AppDbContext db) =>
        {
            var consents = await db.Consents.Where(c => c.ConsentFormId == id).OrderByDescending(c => c.DecidedAt).ToListAsync();
            var studentIds = consents.Select(c => c.StudentId).ToList();
            var students = await db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName);
            return consents.Select(c => new { c.Id, student = students.GetValueOrDefault(c.StudentId), c.GrantedByName, c.Decision, c.DecidedAt, c.WithdrawnAt, c.Ip });
        }).RequireStaff();
    }
}
