using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// Design-time factory used only by the EF Core tooling (<c>dotnet ef migrations add</c> /
/// <c>script</c>). It never connects — the connection string is a placeholder so the Npgsql
/// migration SQL generator is wired up. At runtime the context is configured by its consumer
/// (the test fixture, or the spec-006 host) via DI.
/// </summary>
public sealed class IpamDbContextFactory : IDesignTimeDbContextFactory<IpamDbContext>
{
    /// <inheritdoc />
    public IpamDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IpamDbContext>()
            .UseNpgsql("Host=localhost;Database=pdp_ipam_design_time;Username=design;Password=design")
            .Options;

        return new IpamDbContext(options);
    }
}
