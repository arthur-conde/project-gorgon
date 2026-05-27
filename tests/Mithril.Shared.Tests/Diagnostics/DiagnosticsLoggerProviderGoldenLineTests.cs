using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.Shared.Diagnostics;
using Xunit;

namespace Mithril.Shared.Tests.Diagnostics;

/// <summary>
/// Pins the on-disk compact-JSON shape consumed by tools/MithrilLogMcp.
/// </summary>
public sealed class DiagnosticsLoggerProviderGoldenLineTests
{
    [Fact]
    public void File_line_has_Category_and_Message_for_mcp()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mithril-golden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using (var provider = new DiagnosticsLoggerProvider(dir))
            {
                var logger = provider.CreateLogger("GoldenTest");
                logger.LogDiagnosticInfo("GoldenTest", "hello mcp");
            }

            var file = Directory.GetFiles(dir, "mithril-*.json").Single();
            var line = File.ReadAllLines(file).Last(l => !string.IsNullOrWhiteSpace(l));

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            root.TryGetProperty("@t", out _).Should().BeTrue("MCP detects Serilog compact JSON by @t");
            root.GetProperty("Category").GetString().Should().Be("GoldenTest");
            root.GetProperty("Message").GetString().Should().Be("hello mcp");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
