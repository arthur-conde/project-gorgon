using System;
using System.IO;

namespace Mithril.Tools.LogSanitizer;

internal static class Program
{
    public static int Main(string[] args)
    {
        string? inPath = null;
        string? outPath = null;
        string source = "player";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in" when i + 1 < args.Length: inPath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--source" when i + 1 < args.Length: source = args[++i]; break;
                default:
                    Console.Error.WriteLine($"Unknown arg: {args[i]}");
                    return PrintUsage();
            }
        }

        if (inPath is null || outPath is null)
            return PrintUsage();

        ILogSourceRules rules = source switch
        {
            "player" => new PlayerLogRules(),
            "chat" => new ChatLogRules(),
            _ => throw new ArgumentException($"Unknown source: {source}"),
        };

        var input = File.ReadAllText(inPath);
        var output = new LogSanitizer(rules).Sanitize(input);
        File.WriteAllText(outPath, output);

        Console.Error.WriteLine($"Sanitized {input.Length:N0} chars → {outPath}");
        return 0;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("Usage: Mithril.LogSanitizer --in <path> --out <path> [--source player|chat]");
        return 1;
    }
}
