using Spacewise.Core;

namespace Spacewise.Tests.Unit;

public sealed class ScanHtmlReportTests
{
    [Fact]
    public void Paths_are_opt_in_and_html_is_escaped()
    {
        string root = Path.Combine(Path.GetTempPath(), "private-person");
        string folder = Path.Combine(root, "folder<&");
        var scan = new ScanResult(Guid.NewGuid(), root, ScanState.Partial, 1, 20, 1, 2,
            [new(Path.Combine(folder, "file<script>.txt"), 20, 4096)], []) { Folders = [new(folder, 1, 20)] };
        string hidden = ScanHtmlReport.Build(scan, DateTime.UtcNow);
        Assert.DoesNotContain("private-person", hidden); Assert.DoesNotContain("<script>", hidden);
        Assert.Contains("Результат неполный", hidden); Assert.Contains("<svg", hidden);
        string explicitPaths = ScanHtmlReport.Build(scan, DateTime.UtcNow, includePaths: true);
        Assert.Contains("private-person", explicitPaths); Assert.Contains("&lt;script&gt;", explicitPaths);
        Assert.DoesNotContain("<script>", explicitPaths);
    }
}
