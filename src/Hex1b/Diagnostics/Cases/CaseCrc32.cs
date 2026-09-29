namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// CRC-32 (IEEE 802.3, the zlib polynomial) of each artifact line, so a reader can verify every line it
/// uses. Hex1b does not reference System.IO.Hashing; this is its only use.
/// </summary>
internal static class CaseCrc32
{
    private static readonly uint[] Table = BuildTable();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
