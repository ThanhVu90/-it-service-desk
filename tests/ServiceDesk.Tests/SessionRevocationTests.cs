using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceDesk.Data;
using ServiceDesk.Domain;

namespace ServiceDesk.Tests;

public sealed class SessionRevocationTests
{
    // Seeded ids: alex=1, linh=2, sam=3, admin=4 (DemoSeed insert order).

    [Fact]
    public async Task Locked_account_loses_access_on_the_next_request_and_relogin()
    {
        using var app = new DeskFactory(); using var admin = await app.Login("admin"); using var sam = await app.Login("sam");
        (await admin.GetAsync("/api/me")).EnsureSuccessStatusCode();
        var lockResponse = await admin.PostAsJsonAsync("/api/users/3/lock", new { });
        Assert.Equal(HttpStatusCode.NoContent, lockResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.PostAsJsonAsync("/api/login", new { email = "sam@example.test", password = DeskFactory.Password })).StatusCode);
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        var samUser = users.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == 3);
        Assert.False(samUser.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Locking_is_admin_only()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam"); using var alex = await app.Login("alex");
        Assert.Equal(HttpStatusCode.Forbidden, (await sam.PostAsJsonAsync("/api/users/1/lock", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alex.GetAsync("/api/me")).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        Assert.True((await db.Users.SingleAsync(x => x.Id == 1)).IsActive);
        Assert.Equal(0, await db.AuditEvents.AsNoTracking().CountAsync(x => x.Entity == "User" && x.EntityId == 1));
    }

    [Fact]
    public async Task Locking_requires_csrf_token()
    {
        using var app = new DeskFactory(); using var admin = await app.Login("admin");
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users/3/lock", new { })).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        Assert.True((await db.Users.SingleAsync(x => x.Id == 3)).IsActive);
    }

    [Fact]
    public async Task Failed_audit_insert_rolls_back_the_lock()
    {
        using var app = new DeskFactory(); using var admin = await app.Login("admin");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_lock_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'injected test failure'); END;");
        }
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/users/3/lock", new { })).StatusCode);
        using var verify = app.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<DeskDb>();
        var user = await check.Users.SingleAsync(x => x.Id == 3);
        Assert.True(user.IsActive);
        Assert.Equal(1, user.SessionVersion);
        Assert.Equal(0, await check.AuditEvents.AsNoTracking().CountAsync(x => x.Entity == "User"));
    }

    [Fact]
    public async Task Last_active_administrator_is_protected_from_locking()
    {
        using var app = new DeskFactory(); using var admin = await app.Login("admin");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/users/4/lock", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/me")).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        var user = await db.Users.SingleAsync(x => x.Id == 4);
        Assert.True(user.IsActive);
        Assert.Equal(1, user.SessionVersion);
    }

    [Fact]
    public async Task Bumped_session_version_rejects_existing_cookies_without_a_lock()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        (await sam.GetAsync("/api/me")).EnsureSuccessStatusCode();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
            (await db.Users.SingleAsync(x => x.Id == 3)).SessionVersion++;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Deleted_user_is_rejected_by_session_validation()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        (await sam.GetAsync("/api/me")).EnsureSuccessStatusCode();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
            db.Users.Remove(await db.Users.SingleAsync(x => x.Id == 3));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.GetAsync("/api/me")).StatusCode);
    }
}
