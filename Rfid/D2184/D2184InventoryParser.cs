namespace Library_Management_system.Rfid.D2184;

/// <summary>One tag observation streamed by real-time inventory (command 0x89).</summary>
public sealed record D2184TagReport(
    string Epc,
    ushort ProtocolControl,
    int Antenna,
    int Frequency,
    int Rssi);

/// <summary>End-of-round summary returned when an inventory cycle completes successfully.</summary>
public sealed record D2184InventorySummary(int Antenna, int ReadRate, long TotalRead);

/// <summary>What a 0x89 frame turned out to be.</summary>
public abstract record D2184InventoryResult
{
    public sealed record Tag(D2184TagReport Report) : D2184InventoryResult;
    public sealed record Completed(D2184InventorySummary Summary) : D2184InventoryResult;
    public sealed record Failed(byte ErrorCode) : D2184InventoryResult;
    public sealed record Unrecognised(string Reason) : D2184InventoryResult;
}

/// <summary>
/// Interprets real-time inventory (0x89) frames.
///
/// The protocol overloads command 0x89 for three different payloads, distinguished only by
/// length (protocol document section 2.2.8):
///
///   Len 0x04  -> failure,     data = ErrorCode(1)
///   Len 0x0A  -> end of round, data = AntId(1) + ReadRate(2) + TotalRead(4)
///   otherwise -> tag report,  data = FreqAnt(1) + PC(2) + EPC(N) + RSSI(1)
///
/// NOTE - a genuine ambiguity in the protocol: a tag report with a 3-byte EPC would also carry
/// Len 0x0A and be indistinguishable from an end-of-round summary. Real EPCs are 12 bytes
/// (96-bit) and the library's tags will be standard, so this is safe in practice, but the
/// ambiguity is in the wire format rather than in this code. Tag reports shorter than a 2-byte
/// EPC are rejected rather than guessed at.
/// </summary>
public static class D2184InventoryParser
{
    // Data-section sizes, excluding the address/cmd/checksum counted by Len.
    private const int FailureDataLength = 1;
    private const int CompletedDataLength = 7;

    // FreqAnt(1) + PC(2) + RSSI(1) - everything in a tag report that is not the EPC.
    private const int TagFixedDataLength = 4;

    public static D2184InventoryResult Parse(D2184Frame frame)
    {
        if (frame.Command != D2184Command.RealTimeInventory)
        {
            return new D2184InventoryResult.Unrecognised(
                $"Expected command 0x89, received 0x{frame.Command:X2}.");
        }

        var data = frame.Data;

        return data.Length switch
        {
            FailureDataLength => new D2184InventoryResult.Failed(data[0]),
            CompletedDataLength => ParseCompleted(data),
            _ => ParseTag(data)
        };
    }

    private static D2184InventoryResult ParseCompleted(byte[] data)
    {
        // AntId(1) + ReadRate(2, big-endian) + TotalRead(4, big-endian)
        var antenna = data[0];
        var readRate = (data[1] << 8) | data[2];
        long totalRead = ((long)data[3] << 24) | ((long)data[4] << 16) | ((long)data[5] << 8) | data[6];

        return new D2184InventoryResult.Completed(
            new D2184InventorySummary(antenna, readRate, totalRead));
    }

    private static D2184InventoryResult ParseTag(byte[] data)
    {
        var epcLength = data.Length - TagFixedDataLength;
        if (epcLength < 2)
        {
            return new D2184InventoryResult.Unrecognised(
                $"Tag report too short for an EPC: {data.Length} data bytes.");
        }

        // FreqAnt: high 6 bits = frequency parameter, low 2 bits = antenna id.
        var freqAnt = data[0];
        var antenna = freqAnt & 0x03;
        var frequency = (freqAnt >> 2) & 0x3F;

        var pc = (ushort)((data[1] << 8) | data[2]);
        var epc = Convert.ToHexString(data, 3, epcLength);

        // RSSI is reported as an unsigned byte; the reader's own scale, not dBm.
        var rssi = data[^1];

        return new D2184InventoryResult.Tag(
            new D2184TagReport(epc, pc, antenna, frequency, rssi));
    }
}
