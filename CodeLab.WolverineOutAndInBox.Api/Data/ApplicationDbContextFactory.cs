using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodeLab.WolverineOutAndInBox.Api.Data;

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        var connection = Environment.GetEnvironmentVariable("ConnectionString");
        optionsBuilder.UseNpgsql("Host=localhost;Database=app-db;Username=postgres;Password=postgres");

        return new AppDbContext(optionsBuilder.Options);
    }
}