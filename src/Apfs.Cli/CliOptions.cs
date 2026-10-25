namespace Apfs.Cli;

internal sealed class CliUsageException(string message) : Exception(message);

/// <summary>Basit komut satırı ayrıştırıcı: komut, konumsal argümanlar, bayraklar ve --volume/--partition.</summary>
internal sealed class CliOptions
{
    public string Command { get; private init; } = "help";
    public List<string> Positional { get; } = [];
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Volume { get; private set; }
    public int Partition { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions { Command = args.Length > 0 ? args[0].ToLowerInvariant() : "help" };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--volume":
                    o.Volume = ParseInt(args, ++i, a);
                    o.Flags.Add("--volume-set");
                    break;
                case "--partition":
                    o.Partition = ParseInt(args, ++i, a);
                    break;
                default:
                    if (a.StartsWith('-') && a.Length > 1 && !a.StartsWith(@"\\")) o.Flags.Add(a);
                    else o.Positional.Add(a);
                    break;
            }
        }
        return o;
    }

    public bool Has(string flag) => Flags.Contains(flag);

    public string Require(int index, string name) =>
        index < Positional.Count && !string.IsNullOrWhiteSpace(Positional[index])
            ? Positional[index]
            : throw new CliUsageException($"<{name}> eksik");

    private static int ParseInt(string[] args, int i, string name) =>
        i < args.Length && int.TryParse(args[i], out int v) && v >= 0 ? v : throw new CliUsageException($"{name} için sayı bekleniyordu");
}
