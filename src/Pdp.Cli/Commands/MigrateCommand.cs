using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry;

namespace Pdp.Cli.Commands;

/// <summary>
/// <c>pdp migrate</c> — applies the <c>ipam</c> + <c>registry</c> EF Core schemas to the configured
/// Postgres (creating the database if it does not yet exist). A local-dev / setup convenience: it runs
/// <b>without</b> starting the Wolverine messaging host, needing only the connection string, so it is
/// safe to run on its own and is what the Aspire AppHost invokes on F5 to make Tier-2 a single command.
/// Idempotent — re-running applies only outstanding migrations.
/// </summary>
public static class MigrateCommand
{
    /// <summary>Builds the <c>migrate</c> command bound to the resolved <paramref name="connectionString"/>.</summary>
    public static Command Create(string connectionString)
    {
        var migrate = new Command("migrate", "Apply the ipam + registry database schemas (local/dev setup).");

        migrate.SetAction(async (_, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine(
                    "error: no Postgres connection configured. Set ControlPlane:PostgresConnectionString " +
                    "(appsettings.Development.json or the ControlPlane__PostgresConnectionString env var), " +
                    "or run under the Aspire AppHost.");
                return CliExit.Validation;
            }

            await using (var ipam = new IpamDbContext(
                new DbContextOptionsBuilder<IpamDbContext>().UseNpgsql(connectionString).Options))
            {
                await ipam.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var registry = new RegistryDbContext(
                new DbContextOptionsBuilder<RegistryDbContext>().UseNpgsql(connectionString).Options))
            {
                await registry.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            Console.Out.WriteLine("Applied ipam + registry schemas.");
            return CliExit.Success;
        });

        return migrate;
    }
}
