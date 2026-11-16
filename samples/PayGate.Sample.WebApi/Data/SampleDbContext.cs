namespace PayGate.Sample.WebApi.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>The app's own database. PayGate never sees it.</summary>
public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    private const int _sqliteConstraint = 19;

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    public DbSet<WebhookInboxEntry> WebhookInbox => Set<WebhookInboxEntry>();

    /// <summary>True when saving failed on a unique index, which here means someone else got there first.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: _sqliteConstraint };

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare DateTimeOffset values, and the reconciliation job asks "older than".
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<PaymentStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<OrderStatus>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(order =>
        {
            order.Property(o => o.Description).HasMaxLength(200);
            order.Property(o => o.Currency).HasMaxLength(3);
            order.HasMany(o => o.Attempts).WithOne(a => a.Order).HasForeignKey(a => a.OrderId);
        });

        modelBuilder.Entity<PaymentAttempt>(attempt =>
        {
            attempt.HasIndex(a => a.MerchantReference).IsUnique();
            attempt.HasIndex(a => new { a.OrderId, a.Number }).IsUnique();
            attempt.HasIndex(a => a.ProviderPaymentId);
            attempt.HasIndex(a => new { a.Status, a.CreatedAt });
            attempt.Property(a => a.MerchantReference).HasMaxLength(64);
            attempt.Property(a => a.Account).HasMaxLength(64);
            attempt.Property(a => a.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<WebhookInboxEntry>(entry =>
        {
            // The dedupe contract: one row per provider event, enforced by the database, not by a check in code.
            entry.HasIndex(e => new { e.Provider, e.EventId }).IsUnique();
            entry.Property(e => e.Provider).HasMaxLength(32);
            entry.Property(e => e.Account).HasMaxLength(64);
            entry.Property(e => e.EventId).HasMaxLength(200);
            entry.Property(e => e.EventType).HasMaxLength(32);
        });
    }
}
