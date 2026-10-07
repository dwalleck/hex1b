using System.Text;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticModelRestoreTests
{
    // Issue 60, decision D2 (fail closed). text-state/3 has no field for DEC line renditions (DECDWL/DECDHL), so a model
    // holding any non-single-width row on the screen, the saved main screen or in retained history names the unsupported
    // surface line-renditions: a start is not restorable and a comparison is unavailable, instead of a silently
    // single-width restore. A model whose renditions are all single width names nothing.
    [TestMethod]
    [DataRow("screen, double width", "\u001b[2;1H\u001b#6wide", true)]
    [DataRow("screen, double height top", "\u001b[3;1H\u001b#3tall", true)]
    [DataRow("saved main screen", "\u001b[2;1H\u001b#6wide\u001b[?1049halt", true)]
    [DataRow("retained history", "\u001b#6wide\r\n\n\n\n\n\n\n\n\n\n\n\n", true)]
    [DataRow("reset to single width", "\u001b[2;1H\u001b#6wide\u001b#5", false)]
    [DataRow("erased by ED 2", "\u001b[2;1H\u001b#6wide\u001b[2J", false)]
    [DataRow("never set", "plain text\r\nmore", false)]
    public void Projection_LineRenditionsAreNamedUnsupported(string shape, string output, bool named)
    {
        var model = Detached(new FakeTimeProvider());
        model.ApplyRecordedOutput(Encoding.UTF8.GetBytes(output));
        var state = model.CaptureModelState();
        Assert.AreEqual(named, state.Unsupported.Contains("line-renditions"), $"{shape}: unsupported = [{string.Join(", ", state.Unsupported)}]");
        Assert.AreEqual(named, StartCheckpoint.Unsupported(state).Contains("line-renditions"), $"{shape}: start checkpoint");
        var replica = Detached(new FakeTimeProvider());
        if (named)
        {
            var refused = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(state), shape);
            StringAssert.Contains(refused.Message, "line-renditions", shape);
        }
        else
        {
            replica.RestoreModelState(state);
            Assert.IsEmpty(JsonDifferences(Json(state), Json(replica.CaptureModelState())), $"{shape}: restored start differs");
        }
    }
}
