using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// Design-time factory used only by the EF Core tooling (<c>dotnet ef migrations add</c> /
/// <c>database update</c> / <c>script</c>). It never connects — the connection string is a
/// placeholder so the Npgsql migration SQL generator is wired up. At runtime the context is
/// configured by its consumer (the test fixture, or the host) via DI. Mirrors
/// <c>IpamDbContextFactory</c>.
/// </summary>
public sealed class RegistryDbContextFactory : IDesignTimeDbContextFactory<RegistryDbContext>
{
    /// <inheritdoc />
    public RegistryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RegistryDbContext>()
            .UseNpgsql("Host=localhost;Database=pdp_registry_design_time;Username=design;Password=design")
            .Options;

        return new RegistryDbContext(options);
    }
}
