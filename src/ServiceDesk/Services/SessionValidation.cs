using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Data;

namespace ServiceDesk.Services;

/// <summary>
/// Re-validates every cookie principal against the database: unknown user, locked account or a
/// stale session version stops matching on the next request. The database being unavailable is
/// treated as untrusted, so validation fails closed; passwords and cookie values are never logged.
/// </summary>
public static class SessionValidation
{
    internal const string SessionVersionClaim = "sd_session";

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("SessionValidation");
        var identity = context.Principal?.Identity as ClaimsIdentity;
        var userId = 0;
        if (identity is null || !identity.IsAuthenticated || !int.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId))
        {
            await RejectAsync(context, logger, userId, "malformed-principal");
            return;
        }
        var claimedVersion = identity.FindFirst(SessionVersionClaim)?.Value;
        if (claimedVersion is null)
        {
            await RejectAsync(context, logger, userId, "missing-version-claim"); // Cookies signed before the upgrade carry no version.
            return;
        }
        try
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<DeskDb>();
            var state = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.IsActive, x.SessionVersion }).SingleOrDefaultAsync();
            if (state is null)
            {
                await RejectAsync(context, logger, userId, "unknown-user");
                return;
            }
            if (!state.IsActive)
            {
                await RejectAsync(context, logger, userId, "account-locked");
                return;
            }
            if (state.SessionVersion.ToString(CultureInfo.InvariantCulture) != claimedVersion)
            {
                await RejectAsync(context, logger, userId, "version-mismatch");
                return;
            }
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Session validation for user {UserId} could not reach the database; rejecting the request.", userId);
            await RejectAsync(context, logger, userId, "database-unavailable");
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context, Microsoft.Extensions.Logging.ILogger logger, int userId, string reason)
    {
        // SessionVersion=0 with an unknown id only skips a second lookup for logging; no credentials or cookies appear here.
        logger.LogWarning("Session for user {UserId} rejected ({Reason}).", userId, reason);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
