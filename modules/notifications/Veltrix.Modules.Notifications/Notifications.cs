using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veltrix.Contracts.Notifications;
using Veltrix.Modules.Identity;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Notifications;

public sealed class Notification : TenantEntity
{
    /// <summary>Specific recipient, or null for all users holding <see cref="RequiredPermission"/>.</summary>
    public Guid? UserId { get; set; }
    public string? RequiredPermission { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationSeverity Severity { get; set; }
    public string? Link { get; set; }
    public string? DedupeKey { get; set; }
    public DateTimeOffset? EmailProcessedAt { get; set; }
}

public sealed class NotificationRead
{
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ReadAt { get; set; }
}

public sealed class NotificationPreference : TenantEntity
{
    public Guid UserId { get; set; }
    public string Type { get; set; } = "";
    public bool InApp { get; set; } = true;
    public bool Email { get; set; }
    public bool Push { get; set; } = true;
}

public sealed class PushToken : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Platform { get; set; } = "";
    public string Token { get; set; } = "";
}

internal sealed class NotificationConfig : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("notifications", "notifications");
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.DedupeKey });
        b.Property(x => x.RequiredPermission).HasMaxLength(64);
        b.Property(x => x.Type).HasMaxLength(64);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Body).HasMaxLength(2000);
        b.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Link).HasMaxLength(300);
        b.Property(x => x.DedupeKey).HasMaxLength(200);
    }
}

internal sealed class NotificationReadConfig : IEntityTypeConfiguration<NotificationRead>
{
    public void Configure(EntityTypeBuilder<NotificationRead> b)
    {
        b.ToTable("notification_reads", "notifications");
        b.HasKey(x => new { x.NotificationId, x.UserId });
        b.HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NotificationPreferenceConfig : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.ToTable("notification_preferences", "notifications");
        b.HasIndex(x => new { x.UserId, x.Type }).IsUnique();
        b.Property(x => x.Type).HasMaxLength(64);
    }
}

internal sealed class PushTokenConfig : IEntityTypeConfiguration<PushToken>
{
    public void Configure(EntityTypeBuilder<PushToken> b)
    {
        b.ToTable("push_tokens", "notifications");
        b.HasIndex(x => x.Token).IsUnique();
        b.Property(x => x.Platform).HasMaxLength(20);
        b.Property(x => x.Token).HasMaxLength(500);
    }
}

/// <summary>Stages notifications in the current unit of work so they are only persisted with the business change.</summary>
public sealed class NotificationPublisher(DbContext db) : INotificationPublisher
{
    public void Publish(Guid tenantId, string type, string title, string body, NotificationSeverity severity = NotificationSeverity.Info,
        string? requiredPermission = null, Guid? userId = null, string? link = null, string? dedupeKey = null)
    {
        db.Set<Notification>().Add(new Notification
        {
            TenantId = tenantId, Type = type, Title = Trim(title, 200), Body = Trim(body, 2000), Severity = severity,
            RequiredPermission = requiredPermission, UserId = userId, Link = link, DedupeKey = dedupeKey,
        });
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>SignalR hub for live events (notifications, sync hints). Clients join their tenant group automatically.</summary>
[Microsoft.AspNetCore.Authorization.Authorize]
public sealed class LiveHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tid = Context.User?.FindFirst("tid")?.Value;
        var uid = Context.User?.FindFirst("sub")?.Value;
        if (tid is not null) await Groups.AddToGroupAsync(Context.ConnectionId, "tenant:" + tid);
        if (uid is not null) await Groups.AddToGroupAsync(Context.ConnectionId, "user:" + uid);
        await base.OnConnectedAsync();
    }
}

public sealed class NotificationService(DbContext db, ITenantContext ctx, IClock clock)
{
    private IQueryable<Notification> Visible()
    {
        var uid = ctx.RequireUser();
        var perms = ctx.Permissions.ToList();
        var all = ctx.HasPermission(Permissions.All);
        return db.Set<Notification>().Where(n => n.UserId == uid || (n.UserId == null && (n.RequiredPermission == null || all || perms.Contains(n.RequiredPermission))));
    }

    public async Task<IReadOnlyList<NotificationDto>> ListAsync(bool unreadOnly, int take, CancellationToken ct)
    {
        var uid = ctx.RequireUser();
        var reads = db.Set<NotificationRead>().Where(r => r.UserId == uid).Select(r => r.NotificationId);
        var q = Visible();
        if (unreadOnly) q = q.Where(n => !reads.Contains(n.Id));
        return await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(take, 1, 200))
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.Severity.ToString(), n.Link, n.CreatedAt, reads.Contains(n.Id))).ToListAsync(ct);
    }

    public async Task<UnreadCountDto> UnreadAsync(CancellationToken ct)
    {
        var uid = ctx.RequireUser();
        var reads = db.Set<NotificationRead>().Where(r => r.UserId == uid).Select(r => r.NotificationId);
        return new UnreadCountDto(await Visible().CountAsync(n => !reads.Contains(n.Id), ct));
    }

    public async Task MarkReadAsync(IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var uid = ctx.RequireUser();
        var already = db.Set<NotificationRead>().Where(r => r.UserId == uid).Select(r => r.NotificationId);
        var q = Visible().Where(n => !already.Contains(n.Id));
        if (ids is { Count: > 0 }) q = q.Where(n => ids.Contains(n.Id));
        var toMark = await q.Select(n => n.Id).Take(5000).ToListAsync(ct);
        foreach (var id in toMark) db.Add(new NotificationRead { NotificationId = id, UserId = uid, ReadAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationPreferenceDto>> PreferencesAsync(CancellationToken ct)
    {
        var uid = ctx.RequireUser();
        return await db.Set<NotificationPreference>().Where(p => p.UserId == uid).Select(p => new NotificationPreferenceDto(p.Type, p.InApp, p.Email, p.Push)).ToListAsync(ct);
    }

    public async Task SetPreferencesAsync(IReadOnlyList<NotificationPreferenceDto> prefs, CancellationToken ct)
    {
        var uid = ctx.RequireUser();
        var tid = ctx.RequireTenant();
        var existing = await db.Set<NotificationPreference>().Where(p => p.UserId == uid).ToListAsync(ct);
        foreach (var p in prefs)
        {
            if (p.Type.Length is 0 or > 64) throw ValidationException.For("type", "Invalid type.");
            var row = existing.SingleOrDefault(e => e.Type == p.Type) ?? db.Add(new NotificationPreference { TenantId = tid, UserId = uid, Type = p.Type }).Entity;
            row.InApp = p.InApp; row.Email = p.Email; row.Push = p.Push;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RegisterPushTokenAsync(RegisterPushTokenRequest req, CancellationToken ct)
    {
        new Validator().Require(req.Platform is "android" or "windows", "platform", "Unsupported.").NotEmpty(req.Token, "token", 500).ThrowIfInvalid();
        var uid = ctx.RequireUser();
        var row = await db.Set<PushToken>().IgnoreQueryFilters().SingleOrDefaultAsync(t => t.Token == req.Token, ct);
        if (row is not null && row.TenantId != ctx.RequireTenant()) { db.Remove(row); row = null; }
        row ??= db.Add(new PushToken { TenantId = ctx.RequireTenant(), Token = req.Token }).Entity;
        row.UserId = uid; row.Platform = req.Platform; row.DeviceId = req.DeviceId;
        await db.SaveChangesAsync(ct);
    }
}

// ---------- External channels ----------
public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "no-reply@veltrix.local";
}

/// <summary>SMTP email channel. Disabled unless Smtp:Host is configured; credentials come from configuration only.</summary>
public sealed class SmtpEmailSender(SmtpOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Host);

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        if (!IsConfigured) { logger.LogDebug("SMTP not configured; email to {Recipient} skipped", to.Length > 3 ? to[..3] + "***" : "***"); return; }
        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.EnableSsl };
        if (!string.IsNullOrEmpty(options.Username)) client.Credentials = new NetworkCredential(options.Username, options.Password);
        using var msg = new MailMessage(options.From, to, subject, body);
        await client.SendMailAsync(msg, ct);
    }
}

/// <summary>Push provider abstraction (e.g. Firebase Cloud Messaging). No provider is bundled; when none is configured, push is skipped.</summary>
public interface IPushProvider
{
    bool IsConfigured { get; }
    Task SendAsync(IReadOnlyList<string> tokens, string title, string body, CancellationToken ct);
}

public sealed class NullPushProvider : IPushProvider
{
    public bool IsConfigured => false;
    public Task SendAsync(IReadOnlyList<string> tokens, string title, string body, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Password reset delivery by email when SMTP is configured.</summary>
public sealed class EmailPasswordResetNotifier(IEmailSender email, IConfiguration config, ILogger<EmailPasswordResetNotifier> logger) : IPasswordResetNotifier
{
    public async Task SendResetAsync(User user, string token, CancellationToken ct)
    {
        if (!email.IsConfigured)
        {
            logger.LogWarning("Password reset requested for user {UserId} but no email channel is configured; an administrator must reset the password.", user.Id);
            return;
        }
        var baseUrl = config["App:PasswordResetUrl"] ?? "veltrix://reset-password";
        await email.SendAsync(user.Email, "VELTRIX OS — password reset",
            $"A password reset was requested for your account.\n\nUse this code within 30 minutes: {token}\nOr open: {baseUrl}?token={Uri.EscapeDataString(token)}\n\nIf you did not request this, ignore this message.", ct);
    }
}

public sealed class NotificationsModule : IModule
{
    public string Name => "notifications";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<INotificationPublisher, NotificationPublisher>();
        services.AddScoped<NotificationService>();
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions());
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IPushProvider, NullPushProvider>();
        services.AddScoped<IPasswordResetNotifier, EmailPasswordResetNotifier>();
        services.AddSignalR();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapHub<LiveHub>("/hubs/live");
        var g = app.MapModuleGroup("notifications", "Notifications");
        g.MapGet("/", (bool? unreadOnly, int? take, NotificationService s, CancellationToken ct) => s.ListAsync(unreadOnly ?? false, take ?? 50, ct));
        g.MapGet("/unread-count", (NotificationService s, CancellationToken ct) => s.UnreadAsync(ct));
        g.MapPost("/read", async (List<Guid>? ids, NotificationService s, CancellationToken ct) => { await s.MarkReadAsync(ids, ct); return Results.NoContent(); });
        g.MapGet("/preferences", (NotificationService s, CancellationToken ct) => s.PreferencesAsync(ct));
        g.MapPut("/preferences", async (List<NotificationPreferenceDto> prefs, NotificationService s, CancellationToken ct) => { await s.SetPreferencesAsync(prefs, ct); return Results.NoContent(); });
        g.MapPost("/push-tokens", async (RegisterPushTokenRequest r, NotificationService s, CancellationToken ct) => { await s.RegisterPushTokenAsync(r, ct); return Results.NoContent(); });
    }
}
