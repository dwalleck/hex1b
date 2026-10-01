using Hex1b.Diagnostics.Cases;

namespace Hex1b.Tests.Diagnostics;

[TestClass]
public class CaseRoomTests
{
    private const long MaxBytes = 1024 * 1024;

    [TestMethod]
    public void Tiers_AreNetOfReservations_ReleasedOnce()
    {
        // Each tier is the bound less its reserve less the reservations; a kept line takes its reservation back once,
        // however many times its write is attempted.
        var written = 0L;
        var room = new CaseRoom(MaxBytes, () => written);
        Assert.AreEqual((MaxBytes - 16 * 1024, MaxBytes - 8 * 1024, MaxBytes - 2 * 1024), (room.EventLimit, room.RangeLimit, room.ClosingLimit));
        Assert.IsTrue(room.TryReserveRecovery(100_000, 0, 0, out var reserved));
        Assert.AreEqual(100_000 + 4 * 1024 + 16 * 1024, reserved);
        Assert.AreEqual((MaxBytes - 16 * 1024 - reserved, MaxBytes - 8 * 1024 - reserved, MaxBytes - 2 * 1024 - reserved), (room.EventLimit, room.RangeLimit, room.ClosingLimit));
        Assert.IsTrue(room.TryKeep(ref reserved, 30_000, 0, 0), "a smaller state than estimated is kept");
        Assert.AreEqual(30_000 + 20 * 1024, reserved);
        Assert.AreEqual(MaxBytes - 16 * 1024 - reserved, room.EventLimit, "the reservation was not reduced to the line");
        room.ReleaseLine(7, reserved);
        room.ReleaseLine(7, reserved);
        Assert.AreEqual(0L, room.Reserved, "a retried line released its reservation twice");
        Assert.AreEqual(MaxBytes - 16 * 1024, room.EventLimit);
    }

    [TestMethod]
    public void TryReserveRecovery_CountsTheLineInFlight()
    {
        // A line the writer announced before the reservation is counted by the check as if written: the reservation
        // shrinks to what is left behind it; abandoned or written, the line no longer counts beyond the bytes written.
        var written = 900_000L;
        var room = new CaseRoom(MaxBytes, () => written);
        room.BeginLine(100_000);
        Assert.IsTrue(room.TryReserveRecovery(20_000, 0, 0, out var reserved));
        Assert.AreEqual(MaxBytes - 16 * 1024 - 900_000 - 100_000, reserved, "the line in flight was not counted");
        Assert.AreEqual(reserved, room.Reserved);
        room.Release(reserved);
        room.EndLine();
        Assert.IsTrue(room.TryReserveRecovery(20_000, 0, 0, out reserved));
        Assert.AreEqual(20_000 + 20 * 1024, reserved);
        // Growing the reservation to a larger projected state counts a line in flight the same way.
        room.BeginLine(100_000);
        Assert.IsFalse(room.TryKeep(ref reserved, 40_000, 0, 0), "the line in flight was not counted when the reservation grew");
        Assert.AreEqual(20_000 + 20 * 1024, reserved);
        room.EndLine();
        Assert.IsTrue(room.TryKeep(ref reserved, 40_000, 0, 0));
        Assert.AreEqual(40_000 + 20 * 1024, reserved);
        room.Release(reserved);
        Assert.AreEqual(0L, room.Reserved);
        // Not even a line's overhead left: nothing is reserved.
        written = MaxBytes - 16 * 1024 - 20 * 1024;
        Assert.IsFalse(room.TryReserveRecovery(20_000, 0, 0, out reserved), "a reservation with no room for a line");
        Assert.AreEqual((0L, 0L), (reserved, room.Reserved), "a refused reservation left something");
    }

    [TestMethod]
    public void TryReserveRecovery_CountsWhatIsAheadOfTheLine()
    {
        // The queued events (at their written size) and the pending checkpoint states are written before the line; an
        // estimate beyond what is left reserves what is left, and the projected size decides: kept while it fits.
        var written = 500_000L;
        var room = new CaseRoom(MaxBytes, () => written);
        var queued = 400_000L;
        var pending = 100_000L;
        // 1,032,192 - 500,000 - 400,000 - 100,000 = 32,192 bytes: a 10,000-byte estimate (30,480 reserved) fits.
        Assert.IsTrue(room.TryReserveRecovery(10_000, queued, pending, out var reserved));
        Assert.AreEqual(30_480L, reserved);
        room.Release(reserved);
        // A 12,000-byte estimate (32,480) does not: what is left is reserved instead.
        Assert.IsTrue(room.TryReserveRecovery(12_000, queued, pending, out reserved));
        Assert.AreEqual((32_192L, 32_192L), (reserved, room.Reserved), "the reservation was not shrunk to what is left");
        Assert.IsTrue(room.TryKeep(ref reserved, 11_000, queued, pending), "a projected state that fits was refused");
        Assert.AreEqual(11_000 + 20 * 1024, reserved);
        Assert.IsFalse(room.TryKeep(ref reserved, 12_500, queued, pending), "a state larger than fits was kept");
        Assert.AreEqual((11_000 + 20 * 1024, 11_000 + 20 * 1024), (reserved, room.Reserved), "a refused keep changed the reservation");
    }
}
