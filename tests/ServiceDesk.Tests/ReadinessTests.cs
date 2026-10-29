using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceDesk.Data;

namespace ServiceDesk.Tests;

public sealed class ReadinessTests
{
    [Fact]
    public async Task Liveness_stays_simple_and_readiness_reports_ready_when_the_schema_is_current()
    {
        using var app = new DeskFactory();
        var health = await app.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var ready = await app.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.Contains("ready", body);
        Assert.DoesNotContain(".db", body);
        Assert.DoesNotContain("Data Source", body);
    }

    [Fact]
    public async Task Readiness_returns_503_without_connection_details_when_the_store_is_lost()
    {
        using var app = new DeskFactory();
        var client = app.CreateClient();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        // Drop the store out from under the running process; SQLite recreates an empty file on the next open,
        // which disconnects the schema from the migrations. Readiness must notice and fail closed.
        foreach (var suffix in new[] { "", "-shm", "-wal" }) if (File.Exists(app.DatabasePath + suffix)) File.Delete(app.DatabasePath + suffix);
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(".db", body);
        Assert.DoesNotContain("Data Source", body);
        Assert.DoesNotContain(app.DatabasePath, body);
    }

    [Fact]
    public async Task Validation_errors_carry_a_correlation_id_in_body_and_header()
    {
        using var app = new DeskFactory();
        using var alex = await app.Login("alex");
        var broken = await alex.PostAsJsonAsync("/api/tickets", new { title = "", description = "", priority = "Normal" });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        var json = await broken.Content.ReadFromJsonAsync<JsonElement>();
        var generated = json.GetProperty("correlationId").GetString();
        Assert.Matches("^[0-9a-f]{12}$", generated!);
        Assert.True(broken.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.Equal(generated, values!.Single());

        var echoing = new HttpRequestMessage(HttpMethod.Get, "/api/tickets/1");
        echoing.Headers.Add("X-Correlation-ID", "trace42abcdef");
        var echoed = await alex.SendAsync(echoing);
        Assert.True(echoed.Headers.TryGetValues("X-Correlation-ID", out var echoedValues));
        Assert.Equal("trace42abcdef", echoedValues!.Single());
    }

    [Fact]
    public async Task Unhandled_database_failure_is_a_500_with_correlation_id_and_no_leaked_details()
    {
        using var app = new DeskFactory();
        var client = app.CreateClient();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        foreach (var suffix in new[] { "", "-shm", "-wal" }) if (File.Exists(app.DatabasePath + suffix)) File.Delete(app.DatabasePath + suffix);
        await DeskFactory.RefreshToken(client);
        var response = await client.PostAsJsonAsync("/api/login", new { email = "alex@example.test", password = DeskFactory.Password });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches("^[0-9a-f]{12}$", json.GetProperty("correlationId").GetString()!);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(".db", body);
        Assert.DoesNotContain("Data Source", body);
        Assert.DoesNotContain(app.DatabasePath, body);
        Assert.DoesNotContain(" at ", body);
    }

    [Fact]
    public async Task Not_found_responses_reveal_no_paths_or_stack_details()
    {
        using var app = new DeskFactory();
        using var alex = await app.Login("alex");
        var missing = await alex.GetAsync("/api/tickets/9999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var body = await missing.Content.ReadAsStringAsync();
        Assert.DoesNotContain(" at ", body);
        Assert.DoesNotContain(".db", body);
        Assert.DoesNotContain("Data Source", body);
        Assert.DoesNotContain(app.DatabasePath, body);
    }
}
