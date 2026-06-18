namespace Pdp.Cli.Tests;

/// <summary>
/// Serializes the CLI command tests that redirect the process-global <see cref="System.Console"/>
/// streams. xUnit runs distinct test classes in parallel by default; two classes swapping
/// <c>Console.Out</c>/<c>Console.Error</c> at once would capture each other's output. Sharing one
/// collection forces them to run sequentially.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CliConsoleCollection
{
    /// <summary>The collection name shared by the console-redirecting CLI test classes.</summary>
    public const string Name = "cli-console";
}
