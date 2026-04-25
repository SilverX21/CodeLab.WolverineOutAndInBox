using CodeLab.WolverineOutAndInBox.Api.Entities;

using Microsoft.EntityFrameworkCore;

using Wolverine.EntityFrameworkCore;

namespace CodeLab.WolverineOutAndInBox.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {

    }

    public DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Registers Wolverine's envelope storage tables in this DbContext.
        // This creates the outbox table (for messages waiting to be relayed to the broker),
        // the inbox table (for deduplicating already-processed incoming messages),
        // and the saga state table (for persisting long-running saga instances).
        // These tables are required for durable messaging to work.
        modelBuilder.MapWolverineEnvelopeStorage();

        // Automatically discovers and applies all IEntityTypeConfiguration<T> classes
        // in this assembly (e.g. UserConfig). No need to register configs manually —
        // just add a new class implementing IEntityTypeConfiguration<T> in Data/Configs/
        // and it will be picked up automatically.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
