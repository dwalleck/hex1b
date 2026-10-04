using Hex1b.Tool.Commands.Terminal;

namespace Hex1b.Tool.Tests;

[TestClass]
public class TerminalStartCommandTests
{
    [TestMethod]
    [DataRow("dotnet")]
    [DataRow("dotnet.exe")]
    [DataRow("DOTNET.EXE")]
    public void HostStartInfo_FrameworkHost_IncludesAssemblyAndPreservesExecutable(string name)
    {
        var executable = Path.Combine(Path.GetTempPath(), "private sdk", name);
        string[] arguments = ["terminal", "host", "--width", "80", "--", "program", "--width=3", "two words", "", "a\"b", "--"];

        var start = TerminalStartCommand.HostStartInfo(executable, arguments);

        Assert.AreEqual(executable, start.FileName);
        TestSeq.AreEqual(new[] { typeof(TerminalStartCommand).Assembly.Location }.Concat(arguments), start.ArgumentList);
        Assert.IsFalse(start.UseShellExecute);
        Assert.IsTrue(start.CreateNoWindow);
        Assert.IsTrue(start.RedirectStandardOutput);
        Assert.IsTrue(start.RedirectStandardError);
    }

    [TestMethod]
    [DataRow("Hex1b.Tool")]
    [DataRow("Hex1b.Tool.exe")]
    [DataRow("custom-dotnet")]
    [DataRow("custom-dotnet.exe")]
    public void HostStartInfo_Apphost_DoesNotPrependAssembly(string name)
    {
        var executable = Path.Combine(Path.GetTempPath(), "app directory", name);
        string[] arguments = ["terminal", "host", "--", "program", "two words", "--record-case"];

        var start = TerminalStartCommand.HostStartInfo(executable, arguments);

        Assert.AreEqual(executable, start.FileName);
        TestSeq.AreEqual(arguments, start.ArgumentList);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void HostStartInfo_UnavailableProcessPath_FallsBackToDotnet(string? path)
    {
        var start = TerminalStartCommand.HostStartInfo(path, ["terminal", "host"]);

        Assert.AreEqual("dotnet", start.FileName);
        TestSeq.AreEqual(new[] { typeof(TerminalStartCommand).Assembly.Location, "terminal", "host" }, start.ArgumentList);
    }
}
