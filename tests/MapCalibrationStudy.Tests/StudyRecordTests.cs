using FluentAssertions;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class StudyRecordTests
{
    [Fact]
    public void Csv_has_header_and_one_row_per_record()
    {
        var rows = new[]
        {
            new StudyRecord("AreaSerbule", 0.019, 0, false, 0.385, 0.40, 0.96, 0.012, 0.30, 0.31, 6, true),
            new StudyRecord("AreaEltibule", -179.996, 180, false, 0.315, 0.33, 0.95, 0.011, 0.34, 0.35, 5, true),
        };
        var csv = StudyRecord.ToCsv(rows);
        var lines = csv.Trim().Split('\n');
        lines.Should().HaveCount(3); // header + 2
        lines[0].Should().Contain("area").And.Contain("rotationDeg").And.Contain("insetFracMax");
        lines[1].Should().StartWith("AreaSerbule,");
    }

    [Fact]
    public void Markdown_renders_a_table()
    {
        var rows = new[] { new StudyRecord("AreaCave1", 0.0003, 0, false, 0.42, 0.43, 0.98, 0.0, 0.05, 0.05, 4, true) };
        var md = StudyRecord.ToMarkdown(rows);
        md.Should().Contain("| area |").And.Contain("| AreaCave1 |");
    }
}
