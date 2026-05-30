namespace Mithril.Tools.MapCalibrationStudy;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: MapCalibrationStudy <measure|bootstrap> [options]");
            return 1;
        }
        // Subcommand dispatch is implemented in Task 7. Scaffolding only here.
        Console.WriteLine($"MapCalibrationStudy: '{args[0]}' (not yet implemented)");
        return 0;
    }
}
