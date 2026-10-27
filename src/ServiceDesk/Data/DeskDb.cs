using Microsoft.EntityFrameworkCore;
using ServiceDesk.Domain;
namespace ServiceDesk.Data;

public sealed class DeskDb(DbContextOptions<DeskDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        b.Entity<Ticket>().Property(x => x.CreatedAt).HasConversion(utc);
        b.Entity<Ticket>().Property(x => x.DueAt).HasConversion(utc);
        b.Entity<Assignment>().Property(x => x.AssignedAt).HasConversion(utc);
        b.Entity<Assignment>().Property(x => x.ReturnedAt).HasConversion(utc);
        b.Entity<AuditEvent>().Property(x => x.At).HasConversion(utc);
        b.Entity<User>().HasIndex(x => x.Email).IsUnique();
        b.Entity<User>().Property(x => x.Email).HasMaxLength(254);
        b.Entity<Ticket>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Ticket>().Property(x => x.Title).HasMaxLength(160);
        b.Entity<Ticket>().HasOne<User>().WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Ticket>().HasOne<User>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Ticket>().HasIndex(x => new { x.RequesterId, x.Status });
        b.Entity<Asset>().HasIndex(x => x.Tag).IsUnique();
        b.Entity<Asset>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Assignment>().HasIndex(x => x.AssetId).IsUnique().HasFilter("\"ReturnedAt\" IS NULL");
        b.Entity<Assignment>().HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Assignment>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AuditEvent>().HasOne<User>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries<AuditEvent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit events are append-only through this application.");
        return base.SaveChangesAsync(cancellationToken);
    }
}

