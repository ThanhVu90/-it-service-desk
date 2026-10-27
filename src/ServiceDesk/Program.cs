using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Data;
using ServiceDesk.Domain;
using ServiceDesk.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<DeskDb>(o =>
{
    var postgres = builder.Configuration.GetConnectionString("Postgres");
    if (!string.IsNullOrWhiteSpace(postgres)) o.UseNpgsql(postgres);
    else o.UseSqlite(builder.Configuration.GetConnectionString("Desk") ?? "Data Source=desk.db;Foreign Keys=True");
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeskService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "ServiceDesk.Session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing") ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys"))).UseEphemeralDataProtectionProvider();
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-cache, no-store";
    try { await next(); }
    catch (RuleException ex) { await Results.Problem(statusCode: ex.Status, title: ex.Message).ExecuteAsync(context); }
    catch (DbUpdateConcurrencyException) { await Results.Problem(statusCode: 409, title: "The record changed. Refresh and try again.").ExecuteAsync(context); }
    catch (DbUpdateException ex)
    {
        app.Logger.LogWarning(ex, "Database mutation rejected");
        await Results.Problem(statusCode: 409, title: "The change conflicts with stored data. Refresh and check your input.").ExecuteAsync(context);
    }
    catch (AntiforgeryValidationException) { await Results.Problem(statusCode: 400, title: "Invalid security token. Refresh the page.").ExecuteAsync(context); }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && HttpMethods.IsPost(context.Request.Method))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next();
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/csrf", (HttpContext context, IAntiforgery csrf) => Results.Ok(new { token = csrf.GetAndStoreTokens(context).RequestToken }));
app.MapPost("/api/login", async (LoginRequest input, DeskDb db, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 254 || string.IsNullOrEmpty(input.Password) || input.Password.Length > 256)
        return Results.Problem(statusCode: 400, title: "Provide a valid email and password.");
    var user = await db.Users.SingleOrDefaultAsync(x => x.Email == input.Email.Trim().ToLowerInvariant());
    var hasher = new PasswordHasher<User>();
    if (user == null || hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
        return Results.Problem(statusCode: 401, title: "Incorrect email or password.");
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Role, user.Role) }, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { user.Id, user.Name, user.Role });
}).RequireRateLimiting("login");
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/me", (HttpContext c) => new { id = Current(c).Id, name = c.User.Identity!.Name, role = Current(c).Role });
api.MapPost("/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.NoContent(); });
api.MapGet("/users", async (DeskDb db) => await db.Users.OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Role }).ToListAsync()).RequireAuthorization(p => p.RequireRole(Roles.Admin));
api.MapGet("/dashboard", async (DeskService service, HttpContext c) =>
{
    var query = service.Visible(Current(c));
    var now = DateTime.UtcNow;
    return new { open = await query.CountAsync(x => x.Status == TicketStatus.Open), inProgress = await query.CountAsync(x => x.Status == TicketStatus.InProgress), resolved = await query.CountAsync(x => x.Status == TicketStatus.Resolved || x.Status == TicketStatus.Closed), overdue = await query.CountAsync(x => (x.Status == TicketStatus.Open || x.Status == TicketStatus.InProgress) && x.DueAt < now) };
});
api.MapGet("/tickets", async (DeskService service, HttpContext c, string? search, TicketStatus? status, int page = 1, int pageSize = 10) =>
{
    if (page < 1 || page > 100000 || pageSize < 1 || pageSize > 50 || search?.Length > 160 || (status.HasValue && !Enum.IsDefined(status.Value)))
        throw new RuleException(400, "Invalid filter or page size.");
    var query = service.Visible(Current(c)).AsNoTracking();
    if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Title.Contains(search));
    if (status.HasValue) query = query.Where(x => x.Status == status.Value);
    return Results.Ok(new { total = await query.CountAsync(), page, pageSize, items = await query.OrderByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync() });
});
api.MapGet("/tickets/{id:int}", async (int id, DeskService service, DeskDb db, HttpContext c) =>
{
    var ticket = await service.Visible(Current(c)).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    if (ticket == null) return Results.NotFound();
    var audit = await db.AuditEvents.Where(x => x.Entity == "Ticket" && x.EntityId == id).OrderBy(x => x.Id).Select(x => new { x.Action, x.At, x.ActorId }).ToListAsync();
    return Results.Ok(new { ticket, audit });
});
api.MapPost("/tickets", async (CreateTicket input, DeskService service, HttpContext c) =>
{
    var ticket = await service.CreateTicket(Current(c), input.Title, input.Description, input.Priority);
    return Results.Created($"/api/tickets/{ticket.Id}", ticket);
});
api.MapPost("/tickets/{id:int}/transition", async (int id, ChangeTicket input, DeskService service, HttpContext c) => Results.Ok(await service.Transition(Current(c), id, input.Version, input.Status, input.Resolution)));
api.MapGet("/assets", async (DeskDb db, HttpContext c) =>
{
    var actor = Current(c);
    var assets = db.Assets.AsNoTracking();
    if (!actor.IsStaff) assets = assets.Where(a => db.Assignments.Any(x => x.AssetId == a.Id && x.UserId == actor.Id && x.ReturnedAt == null));
    return Results.Ok(await assets.OrderBy(a => a.Tag).Select(a => new { a.Id, a.Tag, a.Model, a.Version, assignedTo = db.Assignments.Where(x => x.AssetId == a.Id && x.ReturnedAt == null).Select(x => (int?)x.UserId).FirstOrDefault() }).ToListAsync());
});
api.MapGet("/assets/{id:int}/history", async (int id, DeskDb db) => Results.Ok(await db.Assignments.Where(x => x.AssetId == id).OrderByDescending(x => x.Id).ToListAsync())).RequireAuthorization(p => p.RequireRole(Roles.Admin));
api.MapPost("/assets", async (CreateAsset input, DeskService service, HttpContext c) => Results.Ok(await service.CreateAsset(Current(c), input.Tag, input.Model))).RequireAuthorization(p => p.RequireRole(Roles.Admin));
api.MapPost("/assets/{id:int}/assign", async (int id, AssignAsset input, DeskService service, HttpContext c) => { await service.Assign(Current(c), id, input.UserId, input.Version); return Results.NoContent(); }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
api.MapPost("/assets/{id:int}/return", async (int id, ReturnAsset input, DeskService service, HttpContext c) => { await service.Return(Current(c), id, input.Version); return Results.NoContent(); }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

if (app.Configuration.GetValue<bool>("Demo:Seed"))
{
    if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) throw new InvalidOperationException("Demo seeding is only allowed in Development or Testing.");
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
    await db.Database.EnsureCreatedAsync();
    await DemoSeed.Run(db, app.Configuration["Demo:Password"] ?? throw new InvalidOperationException("Set Demo__Password before seeding."));
}
await app.RunAsync();
static Actor Current(HttpContext context) => new(int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!), context.User.FindFirstValue(ClaimTypes.Role)!);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record CreateTicket(string? Title, string? Description, Priority Priority);
public sealed record ChangeTicket(int Version, TicketStatus Status, string? Resolution);
public sealed record CreateAsset(string? Tag, string? Model);
public sealed record AssignAsset(int UserId, int Version);
public sealed record ReturnAsset(int Version);
public partial class Program { }


