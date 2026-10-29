using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServiceDesk.Data;
using ServiceDesk.Domain;
using ServiceDesk.Services;

namespace ServiceDesk.Tests;

public sealed class DeskFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test-only-password-42!";
    private readonly string database = Path.Combine(Path.GetTempPath(), $"desk-tests-{Guid.NewGuid():N}.db");
    public string DatabasePath => database;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Desk"] = $"Data Source={database};Foreign Keys=True;Pooling=False",
            ["ConnectionStrings:Postgres"] = "",
            ["Logging:LogLevel:Default"] = "Error",
            ["Demo:Seed"] = "true",
            ["Demo:Password"] = Password
        }));
    }
    public async Task<HttpClient> Login(string account)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await RefreshToken(client);
        var response = await client.PostAsJsonAsync("/api/login", new { email = account + "@example.test", password = Password });
        response.EnsureSuccessStatusCode();
        await RefreshToken(client);
        return client;
    }
    public static async Task RefreshToken(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/csrf")).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) foreach (var suffix in new[] { "", "-shm", "-wal" }) if (File.Exists(database + suffix)) File.Delete(database + suffix);
    }
}

public sealed class WorkflowTests
{
    [Fact]
    public async Task Persisted_times_are_explicit_utc_and_high_priority_has_four_hours()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        var created = await alex.PostAsJsonAsync("/api/tickets", new { title = "UTC proof", description = "Check the saved deadline", priority = "High" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var result = await alex.GetFromJsonAsync<JsonElement>($"/api/tickets/{id}");
        var ticket = result.GetProperty("ticket");
        Assert.EndsWith("Z", ticket.GetProperty("dueAt").GetString());
        Assert.Equal(TimeSpan.FromHours(4), ticket.GetProperty("dueAt").GetDateTime() - ticket.GetProperty("createdAt").GetDateTime());
    }
    [Fact]
    public async Task Technician_cannot_manage_asset_handovers()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        Assert.Equal(HttpStatusCode.Forbidden, (await sam.PostAsJsonAsync("/api/assets/1/assign", new { userId = 1, version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sam.PostAsJsonAsync("/api/assets/1/return", new { version = 1 })).StatusCode);
    }
    [Fact]
    public async Task Duplicate_tag_and_unknown_recipient_are_rejected()
    {
        using var app = new DeskFactory(); using var admin = await app.Login("admin");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/assets", new { tag = "lt-001", model = "Duplicate" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/assets/1/assign", new { userId = 9999, version = 1 })).StatusCode);
    }
    [Fact]
    public async Task Login_rate_limit_rejects_excessive_attempts()
    {
        using var app = new DeskFactory(); using var client = app.CreateClient();
        await DeskFactory.RefreshToken(client);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/login", new { email = "admin@example.test", password = "wrong-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/login", new { email = "admin@example.test", password = "wrong-password" })).StatusCode);
    }
    [Fact]
    public async Task Anonymous_client_cannot_read_records()
    {
        using var app = new DeskFactory(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/assets")).StatusCode);
    }
    [Fact]
    public async Task Employee_cannot_read_other_employee_ticket_or_audit()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        Assert.Equal(HttpStatusCode.NotFound, (await alex.GetAsync("/api/tickets/2")).StatusCode);
        var list = await alex.GetFromJsonAsync<JsonElement>("/api/tickets?pageSize=50");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.All(list.GetProperty("items").EnumerateArray(), t => Assert.Equal(1, t.GetProperty("requesterId").GetInt32()));
        var filtered = await alex.GetFromJsonAsync<JsonElement>("/api/tickets?search=monitor");
        Assert.Equal(0, filtered.GetProperty("total").GetInt32());
        var stats = await alex.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Equal(1, stats.GetProperty("open").GetInt32());
    }
    [Fact]
    public async Task Employee_cannot_update_other_ticket()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        var response = await alex.PostAsJsonAsync("/api/tickets/2/transition", new { version = 1, status = "InProgress" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    [Fact]
    public async Task Forged_role_and_requester_fields_do_not_escalate_privileges()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        var created = await alex.PostAsJsonAsync("/api/tickets", new { title = "Email access", description = "Cannot open mailbox", priority = "Normal", requesterId = 4, role = "Admin" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ticket = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, ticket.GetProperty("requesterId").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await alex.PostAsJsonAsync("/api/assets", new { tag = "X", model = "Y", role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alex.GetAsync("/api/users")).StatusCode);
    }
    [Fact]
    public async Task Missing_csrf_token_rejects_mutation_without_saving()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        alex.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var response = await alex.PostAsJsonAsync("/api/tickets", new { title = "New", description = "Should not save", priority = "Normal" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, (await alex.GetFromJsonAsync<JsonElement>("/api/tickets")).GetProperty("total").GetInt32());
    }
    [Fact]
    public async Task Login_also_requires_csrf_token()
    {
        using var app = new DeskFactory(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/login", new { email = "admin@example.test", password = DeskFactory.Password })).StatusCode);
    }
    [Fact]
    public async Task Full_lifecycle_preserves_resolution_and_ordered_audit()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam"); using var alex = await app.Login("alex");
        var claimed = await sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 1, status = "InProgress" });
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 2, status = "Resolved", resolution = "Renewed VPN profile and verified a 30-minute call." })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 3, status = "Closed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alex.PostAsJsonAsync("/api/tickets/1/transition", new { version = 3, status = "Closed" })).StatusCode);
        var result = await alex.GetFromJsonAsync<JsonElement>("/api/tickets/1");
        Assert.Equal("Closed", result.GetProperty("ticket").GetProperty("status").GetString());
        Assert.Contains("VPN", result.GetProperty("ticket").GetProperty("resolution").GetString());
        Assert.Equal(3, result.GetProperty("audit").GetArrayLength());
    }
    [Fact]
    public async Task Invalid_transition_leaves_ticket_and_audit_unchanged()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        Assert.Equal(HttpStatusCode.Conflict, (await sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 1, status = "Closed" })).StatusCode);
        var result = await sam.GetFromJsonAsync<JsonElement>("/api/tickets/1");
        Assert.Equal("Open", result.GetProperty("ticket").GetProperty("status").GetString());
        Assert.Equal(1, result.GetProperty("ticket").GetProperty("version").GetInt32());
        Assert.Empty(result.GetProperty("audit").EnumerateArray());
    }
    [Fact]
    public async Task Resolution_requires_meaningful_notes()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        (await sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 1, status = "InProgress" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 2, status = "Resolved", resolution = " " })).StatusCode);
    }
    [Fact]
    public async Task Stale_version_cannot_overwrite_claim()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam"); using var admin = await app.Login("admin");
        (await sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 1, status = "InProgress" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/tickets/1/transition", new { version = 1, status = "InProgress" })).StatusCode);
        var detail = await admin.GetFromJsonAsync<JsonElement>("/api/tickets/1");
        Assert.Equal(3, detail.GetProperty("ticket").GetProperty("assigneeId").GetInt32());
    }
    [Fact]
    public async Task Concurrent_claims_have_exactly_one_winner()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam"); using var admin = await app.Login("admin");
        var results = await Task.WhenAll(sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 1, status = "InProgress" }), admin.PostAsJsonAsync("/api/tickets/1/transition", new { version = 1, status = "InProgress" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }
    [Fact]
    public async Task Concurrent_handover_has_one_winner_and_one_active_assignment()
    {
        using var app = new DeskFactory(); using var admin = await app.Login("admin");
        var results = await Task.WhenAll(admin.PostAsJsonAsync("/api/assets/1/assign", new { userId = 1, version = 1 }), admin.PostAsJsonAsync("/api/assets/1/assign", new { userId = 2, version = 1 }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var history = await admin.GetFromJsonAsync<JsonElement>("/api/assets/1/history");
        Assert.Equal(1, history.GetArrayLength());
    }
    [Fact]
    public async Task Return_then_reassign_preserves_history_and_employee_visibility()
    {
        using var app = new DeskFactory(); using var admin = await app.Login("admin"); using var alex = await app.Login("alex"); using var linh = await app.Login("linh");
        (await admin.PostAsJsonAsync("/api/assets/1/assign", new { userId = 1, version = 1 })).EnsureSuccessStatusCode();
        Assert.Equal(1, (await alex.GetFromJsonAsync<JsonElement>("/api/assets")).GetArrayLength());
        Assert.Empty((await linh.GetFromJsonAsync<JsonElement>("/api/assets")).EnumerateArray());
        (await admin.PostAsJsonAsync("/api/assets/1/return", new { version = 2 })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/assets/1/assign", new { userId = 2, version = 3 })).EnsureSuccessStatusCode();
        Assert.Empty((await alex.GetFromJsonAsync<JsonElement>("/api/assets")).EnumerateArray());
        Assert.Equal(2, (await admin.GetFromJsonAsync<JsonElement>("/api/assets/1/history")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await alex.GetAsync("/api/assets/1/history")).StatusCode);
    }
    [Theory]
    [InlineData("", "Normal")]
    [InlineData(" ", "Normal")]
    [InlineData("Valid", "Impossible")]
    public async Task Bad_ticket_input_does_not_save(string title, string priority)
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        Assert.Equal(HttpStatusCode.BadRequest, (await alex.PostAsJsonAsync("/api/tickets", new { title, description = "Details", priority })).StatusCode);
    }
    [Fact]
    public async Task Pagination_is_bounded()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        Assert.Equal(HttpStatusCode.BadRequest, (await sam.GetAsync("/api/tickets?pageSize=10000")).StatusCode);
        var first = await sam.GetFromJsonAsync<JsonElement>("/api/tickets?pageSize=1&page=1");
        var second = await sam.GetFromJsonAsync<JsonElement>("/api/tickets?pageSize=1&page=2");
        Assert.Equal(2, first.GetProperty("total").GetInt32());
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("id").GetInt32(), second.GetProperty("items")[0].GetProperty("id").GetInt32());
    }
    [Fact]
    public async Task Failed_audit_insert_rolls_back_ticket_creation()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'injected test failure'); END;");
        }
        var result = await sam.PostAsJsonAsync("/api/tickets", new { title = "Rollback proof", description = "Must not persist", priority = "Normal" });
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        using var verify = app.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<DeskDb>();
        Assert.Equal(2, await check.Tickets.CountAsync());
        Assert.Equal(0, await check.AuditEvents.CountAsync());
    }
    [Fact]
    public async Task Database_itself_prevents_duplicate_active_assignments()
    {
        using var app = new DeskFactory(); using var admin = await app.Login("admin");
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        db.Assignments.Add(new Assignment { AssetId = 1, UserId = 1, AssignedAt = DateTime.UtcNow }); await db.SaveChangesAsync();
        db.Assignments.Add(new Assignment { AssetId = 1, UserId = 2, AssignedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task Audit_records_cannot_be_edited_through_the_context()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        (await sam.PostAsJsonAsync("/api/tickets/1/transition", new { version = 1, status = "InProgress" })).EnsureSuccessStatusCode();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        (await db.AuditEvents.FirstAsync()).Action = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task Logout_revokes_browser_session()
    {
        using var app = new DeskFactory(); using var sam = await app.Login("sam");
        (await sam.PostAsJsonAsync("/api/logout", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.GetAsync("/api/me")).StatusCode);
    }
    [Fact]
    public async Task Static_ui_has_security_headers_and_no_inline_scripts()
    {
        using var app = new DeskFactory(); using var client = app.CreateClient();
        var response = await client.GetAsync("/"); response.EnsureSuccessStatusCode();
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("/app.js", await response.Content.ReadAsStringAsync());
    }
}


