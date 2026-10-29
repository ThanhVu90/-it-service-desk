using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceDesk.Data;
using ServiceDesk.Domain;

namespace ServiceDesk.Tests;

public sealed class CommentTests
{
    // Seeded ids: alex=1, linh=2, sam=3, admin=4; tickets: 1 = alex (VPN), 2 = linh (monitor).

    [Fact]
    public async Task Outsider_cannot_read_or_write_comments_of_someone_elses_ticket()
    {
        using var app = new DeskFactory(); using var linh = await app.Login("linh");
        Assert.Equal(HttpStatusCode.NotFound, (await linh.GetAsync("/api/tickets/1/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await linh.PostAsJsonAsync("/api/tickets/1/comments", new { body = "not mine" })).StatusCode);
        Assert.Equal(0, await CountComments(app, 1));
    }

    [Fact]
    public async Task Employee_and_technician_exchange_comments_within_visibility()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex"); using var sam = await app.Login("sam");
        var first = await alex.PostAsJsonAsync("/api/tickets/1/comments", new { body = "VPN drops after ten minutes; tried two networks." });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await sam.PostAsJsonAsync("/api/tickets/1/comments", new { body = "Recreating the profile now; keep the meeting open." });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var list = await alex.GetFromJsonAsync<JsonElement>("/api/tickets/1/comments?pageSize=50");
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("VPN drops after ten minutes; tried two networks.", items[0].GetProperty("body").GetString());
        Assert.Equal("Recreating the profile now; keep the meeting open.", items[1].GetProperty("body").GetString());
        Assert.All(items, x => Assert.Equal(1, x.GetProperty("ticketId").GetInt32()));
    }

    [Fact]
    public async Task Script_text_is_returned_as_plain_data()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        (await alex.PostAsJsonAsync("/api/tickets/1/comments", new { body = "<script>alert('cookie')</script>" })).EnsureSuccessStatusCode();
        var list = await alex.GetFromJsonAsync<JsonElement>("/api/tickets/1/comments");
        Assert.Equal("<script>alert('cookie')</script>", list.GetProperty("items")[0].GetProperty("body").GetString());
        var source = await (await app.CreateClient().GetAsync("/app.js")).Content.ReadAsStringAsync();
        Assert.Contains("function renderComments", source);
        Assert.Contains("textContent", source);
        var commentsBlock = source[(source.IndexOf("function renderComments", StringComparison.Ordinal))..];
        var nextFunction = commentsBlock.IndexOf("\nfunction ", StringComparison.Ordinal);
        var builderSource = nextFunction > 0 ? commentsBlock[..nextFunction] : commentsBlock;   // renderComments stays the last function.
        Assert.DoesNotContain("innerHTML", builderSource);
    }

    [Fact]
    public async Task Empty_and_oversized_comments_are_rejected_without_saving()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        Assert.Equal(HttpStatusCode.BadRequest, (await alex.PostAsJsonAsync("/api/tickets/1/comments", new { body = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alex.PostAsJsonAsync("/api/tickets/1/comments", new { body = new string('x', 2001) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alex.PostAsJsonAsync("/api/tickets/1/comments", new { body = "" })).StatusCode);
        Assert.Equal(0, await CountComments(app, 1));
    }

    [Fact]
    public async Task Commenting_records_one_audit_entry_and_survives_in_detail()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        (await alex.PostAsJsonAsync("/api/tickets/1/comments", new { body = "Adding more context for the technician." })).EnsureSuccessStatusCode();
        var detail = await alex.GetFromJsonAsync<JsonElement>("/api/tickets/1");
        var audit = detail.GetProperty("audit").EnumerateArray().ToList();
        Assert.Equal(1, audit.Count(x => x.GetProperty("action").GetString() == "Commented"));
        var samView = await (await app.Login("sam")).GetFromJsonAsync<JsonElement>("/api/tickets/1/comments?pageSize=50");
        Assert.Equal(1, samView.GetProperty("total").GetInt32());
        Assert.Equal("Adding more context for the technician.", samView.GetProperty("items")[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Comment_write_is_rolled_back_when_audit_insert_fails()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_comment_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'injected test failure'); END;");
        }
        Assert.Equal(HttpStatusCode.Conflict, (await alex.PostAsJsonAsync("/api/tickets/1/comments", new { body = "must not persist" })).StatusCode);
        Assert.Equal(0, await CountComments(app, 1));
        using var verify = app.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<DeskDb>();
        Assert.Equal(0, await check.AuditEvents.AsNoTracking().CountAsync(x => x.Entity == "Ticket" && x.Action == "Commented"));
    }

    [Fact]
    public async Task Paging_is_bounded_and_never_dups_or_loses_same_timestamp_comments()
    {
        using var app = new DeskFactory(); using var alex = await app.Login("alex");
        var stamp = DateTime.UtcNow;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
            for (var i = 1; i <= 3; i++)
                db.TicketComments.Add(new TicketComment { TicketId = 1, AuthorId = 1, Body = $"Same timestamp number {i}", CreatedAt = stamp });
            await db.SaveChangesAsync();
        }
        var seen = new List<int>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await alex.GetFromJsonAsync<JsonElement>($"/api/tickets/1/comments?page={page}&pageSize=1");
            Assert.Equal(3, result.GetProperty("total").GetInt32());
            var item = result.GetProperty("items").EnumerateArray().Single();
            var id = item.GetProperty("id").GetInt32();
            Assert.DoesNotContain(id, seen);
            seen.Add(id);
        }
        Assert.Equal(new[] { 1, 2, 3 }, seen);
        Assert.Equal(HttpStatusCode.BadRequest, (await alex.GetAsync("/api/tickets/1/comments?pageSize=200")).StatusCode);
    }

    private static async Task<int> CountComments(WebApplicationFactory<Program> app, int ticketId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        return await db.TicketComments.AsNoTracking().CountAsync(x => x.TicketId == ticketId);
    }
}
