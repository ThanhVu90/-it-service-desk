using Microsoft.EntityFrameworkCore;
using ServiceDesk.Data;
using ServiceDesk.Domain;
namespace ServiceDesk.Services;

public sealed class DeskService(DeskDb db, TimeProvider clock)
{
    public IQueryable<Ticket> Visible(Actor actor) => actor.IsStaff ? db.Tickets : db.Tickets.Where(x => x.RequesterId == actor.Id);
    public void Audit(Actor actor, string entity, int id, string action) => db.AuditEvents.Add(new AuditEvent { ActorId = actor.Id, Entity = entity, EntityId = id, Action = action, At = clock.GetUtcNow().UtcDateTime });
    public async Task<Ticket> CreateTicket(Actor actor, string? title, string? description, Priority priority)
    {
        if (!Enum.IsDefined(priority)) throw new RuleException(400, "Invalid priority.");
        var now = clock.GetUtcNow().UtcDateTime;
        var ticket = new Ticket { Title = Rules.Text(title, "Title", 160), Description = Rules.Text(description, "Description", 4000), Priority = priority, RequesterId = actor.Id, CreatedAt = now, DueAt = now.AddHours(priority switch { Priority.High => 4, Priority.Normal => 24, _ => 72 }) };
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        Audit(actor, "Ticket", ticket.Id, "Created");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ticket;
    }
    public async Task<Ticket> Transition(Actor actor, int id, int version, TicketStatus next, string? resolution)
    {
        var ticket = await Visible(actor).SingleOrDefaultAsync(x => x.Id == id) ?? throw new RuleException(404, "Ticket not found.");
        if (ticket.Version != version) throw new RuleException(409, "Ticket changed. Refresh before trying again.");
        var previous = ticket.Status;
        Rules.Transition(ticket, next, actor, resolution);
        Audit(actor, "Ticket", id, $"{previous} → {next}");
        await db.SaveChangesAsync(); // EF wraps entity changes and the audit insert in one transaction.
        return ticket;
    }
    public async Task<Asset> CreateAsset(Actor actor, string? tag, string? model)
    {
        RequireAdmin(actor);
        var asset = new Asset { Tag = Rules.Text(tag, "Tag", 40).ToUpperInvariant(), Model = Rules.Text(model, "Model", 160) };
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        Audit(actor, "Asset", asset.Id, "Registered");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return asset;
    }
    public async Task Assign(Actor actor, int id, int userId, int version)
    {
        RequireAdmin(actor);
        var asset = await db.Assets.FindAsync(id) ?? throw new RuleException(404, "Asset not found.");
        if (asset.Version != version) throw new RuleException(409, "Asset changed. Refresh before trying again.");
        if (!await db.Users.AnyAsync(x => x.Id == userId)) throw new RuleException(400, "Unknown recipient.");
        if (await db.Assignments.AnyAsync(x => x.AssetId == id && x.ReturnedAt == null)) throw new RuleException(409, "Asset already assigned.");
        db.Assignments.Add(new Assignment { AssetId = id, UserId = userId, AssignedAt = clock.GetUtcNow().UtcDateTime });
        asset.Version++;
        Audit(actor, "Asset", id, $"Assigned to user #{userId}");
        await db.SaveChangesAsync();
    }
    public async Task Return(Actor actor, int id, int version)
    {
        RequireAdmin(actor);
        var asset = await db.Assets.FindAsync(id) ?? throw new RuleException(404, "Asset not found.");
        if (asset.Version != version) throw new RuleException(409, "Asset changed. Refresh before trying again.");
        var assignment = await db.Assignments.SingleOrDefaultAsync(x => x.AssetId == id && x.ReturnedAt == null) ?? throw new RuleException(409, "Asset is not assigned.");
        assignment.ReturnedAt = clock.GetUtcNow().UtcDateTime;
        asset.Version++;
        Audit(actor, "Asset", id, "Returned");
        await db.SaveChangesAsync();
    }
    public async Task LockUserAsync(Actor actor, int userId)
    {
        if (actor.Role != Roles.Admin) throw new RuleException(403, "Administrator access required.");
        var target = await db.Users.FindAsync(userId) ?? throw new RuleException(404, "User not found.");
        if (target.Role == Roles.Admin)
        {
            var otherActiveAdmins = await db.Users.CountAsync(x => x.Role == Roles.Admin && x.IsActive && x.Id != userId);
            if (otherActiveAdmins == 0) throw new RuleException(409, "The last active administrator cannot be locked out.");
        }
        target.IsActive = false;
        target.SessionVersion++;
        Audit(actor, "User", userId, "Locked (account revoked)");
        await db.SaveChangesAsync(); // EF wraps the lock and the audit insert in one transaction; failed audit lock rolls back.
    }
    public async Task<TicketComment> AddComment(Actor actor, int ticketId, string? body)
    {
        var text = Rules.Text(body, "Comment", 2000);
        var ticket = await Visible(actor).SingleOrDefaultAsync(x => x.Id == ticketId) ?? throw new RuleException(404, "Ticket not found.");
        var comment = new TicketComment { TicketId = ticket.Id, AuthorId = actor.Id, Body = text, CreatedAt = clock.GetUtcNow().UtcDateTime };
        db.TicketComments.Add(comment);
        Audit(actor, "Ticket", ticket.Id, "Commented");
        await db.SaveChangesAsync(); // EF wraps the comment and the audit entry in one transaction; a failed audit rolls the comment back.
        return comment;
    }

    public async Task<(int Total, TicketComment[] Items)> ListComments(Actor actor, int ticketId, int page, int pageSize)
    {
        if (!await Visible(actor).AnyAsync(x => x.Id == ticketId)) throw new RuleException(404, "Ticket not found.");
        var query = db.TicketComments.AsNoTracking().Where(x => x.TicketId == ticketId);
        var items = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync();
        return (await query.CountAsync(), items);
    }

    private static void RequireAdmin(Actor actor)
    {
        if (actor.Role != Roles.Admin) throw new RuleException(403, "Administrator access required.");
    }
}
