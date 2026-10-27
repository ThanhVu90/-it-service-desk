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
    private static void RequireAdmin(Actor actor)
    {
        if (actor.Role != Roles.Admin) throw new RuleException(403, "Administrator access required.");
    }
}
