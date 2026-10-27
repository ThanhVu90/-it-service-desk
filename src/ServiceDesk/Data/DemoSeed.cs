using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Domain;
namespace ServiceDesk.Data;

public static class DemoSeed
{
    public static async Task Run(DeskDb db, string password)
    {
        if (password.Length < 12) throw new InvalidOperationException("Demo password must have at least 12 characters.");
        if (await db.Users.AnyAsync()) return;
        var hasher = new PasswordHasher<User>();
        var users = new[] {
            new User { Name = "Alex Nguyen", Email = "alex@example.test", Role = Roles.Employee },
            new User { Name = "Linh Tran", Email = "linh@example.test", Role = Roles.Employee },
            new User { Name = "Sam Pham", Email = "sam@example.test", Role = Roles.Technician },
            new User { Name = "Admin", Email = "admin@example.test", Role = Roles.Admin }
        };
        foreach (var user in users) user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        db.Assets.AddRange(new Asset { Tag = "LT-001", Model = "ThinkPad T14 / 16 GB" }, new Asset { Tag = "LT-002", Model = "Latitude 5440 / 16 GB" }, new Asset { Tag = "MN-001", Model = "Dell 27-inch display" });
        db.Tickets.AddRange(
            new Ticket { Title = "VPN disconnects during team calls", Description = "Connection drops after ten minutes on the office VPN. Local internet stays available.", RequesterId = users[0].Id, Priority = Priority.High, CreatedAt = now.AddHours(-6), DueAt = now.AddHours(-2) },
            new Ticket { Title = "Request a monitor for the design desk", Description = "A second monitor is needed for the new workspace.", RequesterId = users[1].Id, Priority = Priority.Low, CreatedAt = now.AddHours(-1), DueAt = now.AddHours(71) });
        await db.SaveChangesAsync();
    }
}
