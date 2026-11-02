namespace Relay.Core.Navigation;

public static class CommandLineArgs
{
    public const string SingleArgument = "--single-argument";

    /// <summary>
    /// Links opened from other apps arrive as <c>Relay.exe --single-argument &lt;url&gt;</c>. Everything after the switch is
    /// one URL and never a switch, so a crafted link such as <c>https://x" --uninstall --quiet</c> can't inject options.
    /// </summary>
    public static string[] Normalize(string[] args, string rawCommandLine)
    {
        if (args.Length == 0 || !args[0].Equals(SingleArgument, StringComparison.OrdinalIgnoreCase)) return args;
        var i = rawCommandLine.IndexOf(SingleArgument, StringComparison.OrdinalIgnoreCase);
        var rest = i < 0 ? string.Empty : rawCommandLine[(i + SingleArgument.Length)..].Trim();
        return rest.Length == 0 || rest.StartsWith("--", StringComparison.Ordinal) ? [] : [rest];
    }
}
