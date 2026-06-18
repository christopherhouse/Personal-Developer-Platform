using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Pdp.Cli.Rendering;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Verbs.Handlers;

namespace Pdp.Cli.Commands;

/// <summary>
/// The <c>pdp ipam</c> command tree (contracts/cli-surface.md §1) — a thin façade over the spec-002
/// ledger via <see cref="IIpamVerbs"/>. <c>allocate</c>/<c>release</c> are direct ledger operations (no
/// workflow dispatch); <c>query</c> reports one region's or every region's utilization (read-only).
/// Every CIDR the platform uses originates here (Article VI / FR-010).
/// </summary>
public static class IpamCommand
{
    /// <summary>Builds the <c>ipam</c> command.</summary>
    public static Command Create(IServiceProvider services, Option<bool> jsonOption)
    {
        var ipam = new Command("ipam", "Allocate, release, and query address space from the IPAM ledger.");
        ipam.Subcommands.Add(BuildAllocate(services, jsonOption));
        ipam.Subcommands.Add(BuildRelease(services, jsonOption));
        ipam.Subcommands.Add(BuildQuery(services, jsonOption));
        return ipam;
    }

    private static Command BuildAllocate(IServiceProvider services, Option<bool> jsonOption)
    {
        var regionOption = new Option<string>("--region", "-r") { Description = "Registered region.", Required = true };
        var nameOption = new Option<string>("--name", "-n") { Description = "Allocation name (e.g. spoke name).", Required = true };
        var sizeOption = new Option<int>("--size") { Description = "Block size (prefix length); default /24.", DefaultValueFactory = _ => 24 };

        var allocate = new Command("allocate", "Reserve the lowest free aligned block of the given size.")
        {
            regionOption,
            nameOption,
            sizeOption,
        };

        allocate.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(jsonOption);
            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IIpamVerbs>();

            try
            {
                var allocation = await verbs.AllocateAsync(
                    parseResult.GetValue(regionOption)!,
                    parseResult.GetValue(nameOption)!,
                    parseResult.GetValue(sizeOption),
                    cancellationToken).ConfigureAwait(false);
                IpamView.RenderAllocation(allocation, asJson, Console.Out);
                return CliExit.Success;
            }
            catch (IpamException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.Validation;
            }
        });

        return allocate;
    }

    private static Command BuildRelease(IServiceProvider services, Option<bool> jsonOption)
    {
        var regionOption = new Option<string>("--region", "-r") { Description = "Registered region.", Required = true };
        var nameOption = new Option<string>("--name", "-n") { Description = "Allocation name to release.", Required = true };

        var release = new Command("release", "Return a previously allocated block to its pool (idempotent).")
        {
            regionOption,
            nameOption,
        };

        release.SetAction(async (parseResult, cancellationToken) =>
        {
            var region = parseResult.GetValue(regionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IIpamVerbs>();

            try
            {
                await verbs.ReleaseAsync(region, name, cancellationToken).ConfigureAwait(false);
                Console.Out.WriteLine($"Released '{name}' in {region} (or no-op if it was not allocated).");
                return CliExit.Success;
            }
            catch (IpamException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.Validation;
            }
        });

        return release;
    }

    private static Command BuildQuery(IServiceProvider services, Option<bool> jsonOption)
    {
        var regionOption = new Option<string?>("--region", "-r") { Description = "One region; omit to report every registered region." };

        var query = new Command("query", "Report region utilization (one region, or all).")
        {
            regionOption,
        };

        query.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(jsonOption);
            var region = parseResult.GetValue(regionOption);
            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IIpamVerbs>();

            try
            {
                if (string.IsNullOrWhiteSpace(region))
                {
                    var all = await verbs.QueryAllAsync(cancellationToken).ConfigureAwait(false);
                    IpamView.RenderRegions(all, asJson, Console.Out);
                }
                else
                {
                    var view = await verbs.QueryAsync(region, cancellationToken).ConfigureAwait(false);
                    IpamView.RenderRegion(view, asJson, Console.Out);
                }

                return CliExit.Success;
            }
            catch (IpamException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.Validation;
            }
        });

        return query;
    }
}
