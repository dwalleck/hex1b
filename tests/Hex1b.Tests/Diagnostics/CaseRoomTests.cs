using Hex1b.Diagnostics.Cases;

namespace Hex1b.Tests.Diagnostics;

[TestClass]
public class CaseRoomTests
{
    private const long MaxBytes = 1024 * 1024;

    [TestMethod]
    public void Tiers_AreNetOfReservations_ClaimsBoundedByThem_ReleasedOnce()
    {
        // Each tier is the bound less its reserve less the reservations; a line claims its bytes under the tier net of
        // them; a kept line takes its reservation back once, however many times its write is attempted.
        var room = new CaseRoom(MaxBytes);
        Assert.AreEqual((MaxBytes - 16 * 1024, MaxBytes - 8 * 1024, MaxBytes - 2 * 1024), (room.EventLimit, room.RangeLimit, room.ClosingLimit));
        Assert.IsTrue(room.TryReserveRecovery(100_000, 0, 0, out var reserved));
        Assert.AreEqual(100_000 + 4 * 1024 + 16 * 1024, reserved);
        Assert.AreEqual((MaxBytes - 16 * 1024 - reserved, MaxBytes - 8 * 1024 - reserved, MaxBytes - 2 * 1024 - reserved), (room.EventLimit, room.RangeLimit, room.ClosingLimit));
        var left = room.EventLimit;
        Assert.IsFalse(room.TryClaim(CaseRoom.Tier.Events, left + 1), "a line crossing the events tier net of the reservation was claimed");
        Assert.AreEqual(0L, room.BytesWritten, "a refused claim counted its bytes");
        Assert.IsTrue(room.TryClaim(CaseRoom.Tier.Events, left));
        Assert.AreEqual(left, room.BytesWritten);
        Assert.IsTrue(room.TryClaim(CaseRoom.Tier.Ranges, 8 * 1024), "the ranges tier has its reserve above the events tier");
        Assert.IsFalse(room.TryClaim(CaseRoom.Tier.Ranges, 1));
        Assert.IsTrue(room.TryKeep(ref reserved, 30_000, 0, 0), "a smaller state than estimated is kept");
        Assert.AreEqual(30_000 + 20 * 1024, reserved);
        room.ReleaseLine(7, reserved);
        room.ReleaseLine(7, reserved);
        Assert.AreEqual(0L, room.Reserved, "a retried line released its reservation twice");
        Assert.IsTrue(room.TryClaim(CaseRoom.Tier.Events, reserved), "the released room is not free for lines");
    }

    [TestMethod]
    public void TryReserveRecovery_CountsEveryLineClaimedBeforeIt()
    {
        // A line the writer claimed before the reservation is counted by the check, as its bytes: the reservation
        // shrinks to what is left behind it. A line claimed after it is bounded by it.
        var room = new CaseRoom(MaxBytes);
        room.Count(900_000);
        Assert.IsTrue(room.TryClaim(CaseRoom.Tier.Events, 100_000), "fixture: the writer's line");
        Assert.IsTrue(room.TryReserveRecovery(20_000, 0, 0, out var reserved));
        Assert.AreEqual(MaxBytes - 16 * 1024 - 1_000_000, reserved, "the claimed line was not counted");
        Assert.AreEqual(reserved, room.Reserved);
        Assert.IsFalse(room.TryClaim(CaseRoom.Tier.Events, 1), "a line claimed after the reservation crossed into it");
        room.Release(reserved);
        Assert.IsTrue(room.TryClaim(CaseRoom.Tier.Events, 1));
        // Not even a line's overhead left: nothing is reserved.
        room.Count(room.EventLimit - room.BytesWritten - 20 * 1024);
        Assert.IsFalse(room.TryReserveRecovery(20_000, 0, 0, out reserved), "a reservation with no room for a line");
        Assert.AreEqual((0L, 0L), (reserved, room.Reserved), "a refused reservation left something");
    }

    [TestMethod]
    public void TryReserveRecovery_CountsWhatIsAheadOfTheLine()
    {
        // The queued events (at their written size) and the pending checkpoint states are written before the line; an
        // estimate beyond what is left reserves what is left, and the projected size decides: kept while it fits.
        var room = new CaseRoom(MaxBytes);
        room.Count(500_000);
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
