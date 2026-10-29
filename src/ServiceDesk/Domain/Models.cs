namespace ServiceDesk.Domain;

public static class Roles
{
    public const string Employee = "Employee", Technician = "Technician", Admin = "Admin";
}
public enum TicketStatus { Open, InProgress, Resolved, Closed }
public enum Priority { Low, Normal, High }
public sealed class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = Roles.Employee;
    public string PasswordHash { get; set; } = "";

    /// <summary>Administrators set this to false to revoke the account: login is refused and live sessions are rejected.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Raised on every lock so existing cookie principal claims stop matching.</summary>
    public int SessionVersion { get; set; } = 1;
}
public sealed class Ticket
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int RequesterId { get; set; }
    public int? AssigneeId { get; set; }
    public Priority Priority { get; set; }
    public TicketStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime DueAt { get; set; }
    public string Resolution { get; set; } = "";
    public int Version { get; set; } = 1;
}
public sealed class Asset
{
    public int Id { get; set; }
    public string Tag { get; set; } = "";
    public string Model { get; set; } = "";
    public int Version { get; set; } = 1;
}
public sealed class Assignment
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public int UserId { get; set; }
    public DateTime AssignedAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
}
public sealed class AuditEvent
{
    public long Id { get; set; }
    public int ActorId { get; set; }
    public string Entity { get; set; } = "";
    public int EntityId { get; set; }
    public string Action { get; set; } = "";
    public DateTime At { get; set; }
}
public sealed record Actor(int Id, string Role)
{
    public bool IsStaff => Role is Roles.Admin or Roles.Technician;
    public bool CanRead(Ticket ticket) => IsStaff || ticket.RequesterId == Id;
}
public sealed class RuleException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
public static class Rules
{
    public static string Text(string? value, string field, int max)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > max)
            throw new RuleException(400, $"{field} must contain 1–{max} characters.");
        return text;
    }
    public static void Transition(Ticket ticket, TicketStatus next, Actor actor, string? resolution)
    {
        var allowed = (ticket.Status, next) switch
        {
            (TicketStatus.Open, TicketStatus.InProgress) => actor.IsStaff,
            (TicketStatus.InProgress, TicketStatus.Resolved) => actor.Role == Roles.Admin || actor.Id == ticket.AssigneeId,
            (TicketStatus.Resolved, TicketStatus.Closed) => actor.Role == Roles.Admin || actor.Id == ticket.RequesterId,
            _ => false
        };
        if (!allowed) throw new RuleException(409, "This transition is not allowed for your role or the current ticket state.");
        if (next == TicketStatus.Resolved) ticket.Resolution = Text(resolution, "Resolution", 2000);
        if (next == TicketStatus.InProgress) ticket.AssigneeId = actor.Id;
        ticket.Status = next;
        ticket.Version++;
    }
}
