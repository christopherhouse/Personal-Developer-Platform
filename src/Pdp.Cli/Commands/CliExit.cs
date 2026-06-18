namespace Pdp.Cli.Commands;

/// <summary>
/// The CLI's exit-code contract (contracts/cli-surface.md §3), shared by every command so scripts and
/// CI can branch on the outcome consistently.
/// </summary>
public static class CliExit
{
    /// <summary>Clean success / completed run.</summary>
    public const int Success = 0;

    /// <summary>A tracked run ended <c>Failed</c>.</summary>
    public const int RunFailed = 1;

    /// <summary>Invalid input / a ledger precondition or confirmation failure (FR-023/FR-007).</summary>
    public const int Validation = 2;

    /// <summary>A single-flight rejection (FR-022a).</summary>
    public const int InProgress = 3;
}

/// <summary>A console yes/no prompt for the interactive plan/confirm UX (Article VIII).</summary>
public static class ConsolePrompt
{
    /// <summary>Prompts the owner for a yes/no confirmation on the console (default no).</summary>
    public static bool Confirm(string prompt)
    {
        Console.Out.Write($"{prompt} [y/N] ");
        var answer = Console.In.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
