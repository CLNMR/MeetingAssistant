using System.Reflection;
using Msctl;

// msctl: the command-line tool (spec 04-dictation-settings, "msctl"). Each area adds a class
// implementing ICommandGroup in Commands/; commands are found by reflection.
var groups = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => typeof(ICommandGroup).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
    .Select(t => (ICommandGroup)Activator.CreateInstance(t)!)
    .ToList();
var commands = groups.SelectMany(g => g.Commands).ToDictionary(c => c.Name, StringComparer.Ordinal);

if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
{
    Console.WriteLine("usage: msctl <command> [args]\n");
    foreach (var c in commands.Values.OrderBy(c => c.Name)) Console.WriteLine($"  {c.Usage}");
    return 0;
}
if (!commands.TryGetValue(args[0], out var cmd))
{
    Console.Error.WriteLine($"unknown command: {args[0]} (msctl help lists the commands)");
    return 2;
}
try
{
    return await cmd.Run(args.Skip(1).ToArray());
}
catch (UsageException e)
{
    Console.Error.WriteLine(e.Message.Length > 0 ? e.Message : "usage: msctl " + cmd.Usage);
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

namespace Msctl
{
    public interface ICommandGroup
    {
        IEnumerable<Command> Commands { get; }
    }

    /// One msctl command. `Usage` is the help line, starting with the name.
    public sealed record Command(string Name, string Usage, Func<string[], Task<int>> Run);

    public sealed class UsageException(string message = "") : Exception(message);

    public static class Args
    {
        /// Value after `--flag`, or null.
        public static string? Option(string[] args, string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        public static bool Flag(string[] args, string flag) => args.Contains(flag);
        /// Arguments that are not flags or flag values (flags listed in `withValue` take one value).
        public static List<string> Positional(string[] args, params string[] withValue)
        {
            var list = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--")) { if (withValue.Contains(args[i])) i++; continue; }
                list.Add(args[i]);
            }
            return list;
        }
    }
}
