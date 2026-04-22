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
        modelBuilder.MapWolverineEnvelopeStorage();
        
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}