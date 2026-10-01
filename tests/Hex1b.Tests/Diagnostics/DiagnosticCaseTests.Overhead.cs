namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    // The instrumentation-overhead report (ticket 15) lives with the diagnostics evidence; the guide says where it is,
    // what it measures and that it sets no budget.
    [TestMethod]
    public void DocsMentionOverheadReport()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "src/Hex1b/Hex1b.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repository root not found");
        var text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine(root, "src/content/guide/diagnostic-capture.md")), @"\s+", " ");
        foreach (var term in new[]
                 {
                     "armed-versus-unarmed", "15-instrumentation-overhead-evidence", "It sets no budget", "physical display latency",
                     "diagnostics --filter", "`verify.py`",
                 })
            StringAssert.Contains(text, term);
    }
}
