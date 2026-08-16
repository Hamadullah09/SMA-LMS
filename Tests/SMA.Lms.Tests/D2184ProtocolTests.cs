using Library_Management_system.Rfid.D2184;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>
/// D2184 wire protocol (RFID_ARCHITECTURE.md section 1).
///
/// The expected byte sequences are computed by hand from the protocol document's checksum rule,
/// ((~sum) + 1) &amp; 0xFF, so these tests pin the encoding to the specification rather than to our
/// own implementation.
/// </summary>
public class D2184ProtocolTests
{
    [Fact]
    public void No_data_command_encodes_exactly_as_the_vendor_sdk_does()
    {
        // GetFirmwareVersion, address 1.
        // sum = A0 + 03 + 01 + 72 = 0x116 -> 0x16; checksum = (~0x16 + 1) & FF = 0xEA
        var bytes = new D2184Frame(0x01, D2184Command.GetFirmwareVersion).ToBytes();
        Assert.Equal("A0030172EA", Convert.ToHexString(bytes));
    }

    [Fact]
    public void Real_time_inventory_request_encodes_correctly()
    {
        // sum = A0 + 04 + 01 + 89 + FF = 0x22D -> 0x2D; checksum = (~0x2D + 1) & FF = 0xD3
        var bytes = new D2184Frame(
            0x01, D2184Command.RealTimeInventory, [D2184Defaults.ShortestInventoryRepeat]).ToBytes();

        Assert.Equal("A0040189FFD3", Convert.ToHexString(bytes));
    }

    [Fact]
    public void Frame_survives_a_round_trip()
    {
        var original = new D2184Frame(0x01, D2184Command.RealTimeInventory, [0xFF]);
        var parsed = D2184Frame.TryParse(original.ToBytes());

        Assert.NotNull(parsed);
        Assert.Equal(original.Command, parsed!.Command);
        Assert.Equal(original.Data, parsed.Data);
    }

    [Fact]
    public void Corrupted_checksum_is_rejected_rather_than_misread()
    {
        var bytes = new D2184Frame(0x01, D2184Command.RealTimeInventory, [0xFF]).ToBytes();
        bytes[^1] ^= 0xFF;

        Assert.Null(D2184Frame.TryParse(bytes));
    }

    private static D2184Frame TagFrame(byte[] epc, byte freqAnt = 0x05, byte rssi = 0x50)
    {
        var data = new byte[1 + 2 + epc.Length + 1];
        data[0] = freqAnt;
        data[1] = 0x30;
        data[2] = 0x00;
        epc.CopyTo(data, 3);
        data[^1] = rssi;
        return new D2184Frame(0x01, D2184Command.RealTimeInventory, data);
    }

    [Fact]
    public void Tag_report_decodes_epc_antenna_and_rssi()
    {
        byte[] epc = [0xE2, 0x00, 0x34, 0x12, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        var result = D2184InventoryParser.Parse(TagFrame(epc));

        var tag = Assert.IsType<D2184InventoryResult.Tag>(result);
        Assert.Equal("E20034120102030405060708", tag.Report.Epc);
        Assert.Equal(1, tag.Report.Antenna);      // low 2 bits of 0x05
        Assert.Equal(1, tag.Report.Frequency);    // high 6 bits of 0x05
        Assert.Equal(0x50, tag.Report.Rssi);
    }

    [Fact]
    public void Antenna_comes_from_the_low_two_bits_of_freqant()
    {
        byte[] epc = [0xE2, 0x00, 0x34, 0x12, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        // 0xC7 = 1100 0111 -> antenna 3, frequency 49
        var tag = Assert.IsType<D2184InventoryResult.Tag>(
            D2184InventoryParser.Parse(TagFrame(epc, freqAnt: 0xC7)));

        Assert.Equal(3, tag.Report.Antenna);
        Assert.Equal(49, tag.Report.Frequency);
    }

    [Fact]
    public void End_of_round_summary_is_distinguished_from_a_tag_report()
    {
        // AntId 1, ReadRate 100, TotalRead 300
        byte[] data = [0x01, 0x00, 0x64, 0x00, 0x00, 0x01, 0x2C];

        var result = D2184InventoryParser.Parse(
            new D2184Frame(0x01, D2184Command.RealTimeInventory, data));

        var done = Assert.IsType<D2184InventoryResult.Completed>(result);
        Assert.Equal(1, done.Summary.Antenna);
        Assert.Equal(100, done.Summary.ReadRate);
        Assert.Equal(300, done.Summary.TotalRead);
    }

    [Fact]
    public void Failure_frame_reports_its_error_code()
    {
        var result = D2184InventoryParser.Parse(
            new D2184Frame(0x01, D2184Command.RealTimeInventory, [D2184ErrorCode.NoTagError]));

        var failed = Assert.IsType<D2184InventoryResult.Failed>(result);
        Assert.Equal(D2184ErrorCode.NoTagError, failed.ErrorCode);
    }

    [Fact]
    public void Error_messages_shown_to_staff_contain_no_hex_codes()
    {
        // Specification section 48: never surface raw technical detail at the desk.
        byte[] codes =
        [
            D2184ErrorCode.NoTagError, D2184ErrorCode.AntennaMissing,
            D2184ErrorCode.CommandFail, D2184ErrorCode.TagReadError
        ];

        foreach (var code in codes)
        {
            var message = D2184ErrorCode.FriendlyMessage(code);
            Assert.DoesNotContain("0x", message);
            Assert.DoesNotContain("_", message);   // no technical_snake_case leaking through
        }
    }

    [Fact]
    public void Undersized_tag_report_is_rejected_rather_than_guessed_at()
    {
        // FreqAnt + PC + 1 EPC byte + RSSI: too short to be a real EPC.
        var result = D2184InventoryParser.Parse(
            new D2184Frame(0x01, D2184Command.RealTimeInventory, [0x05, 0x30, 0x00, 0xAA, 0x50]));

        Assert.IsType<D2184InventoryResult.Unrecognised>(result);
    }

    // ---- stream reassembly ----

    [Fact]
    public void Frame_split_across_two_reads_is_reassembled()
    {
        byte[] epc = [0xE2, 0x00, 0x34, 0x12, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var bytes = TagFrame(epc).ToBytes();
        var reader = new D2184FrameReader();

        Assert.Empty(reader.Append(bytes.AsSpan(0, 4)));
        Assert.Single(reader.Append(bytes.AsSpan(4)));
    }

    [Fact]
    public void Several_frames_in_one_read_are_all_returned()
    {
        byte[] epc = [0xE2, 0x00, 0x34, 0x12, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var bytes = TagFrame(epc).ToBytes();

        var burst = new List<byte>();
        burst.AddRange(bytes);
        burst.AddRange(bytes);
        burst.AddRange(bytes);

        Assert.Equal(3, new D2184FrameReader().Append(burst.ToArray()).Count);
    }

    [Fact]
    public void Reader_resynchronises_after_leading_junk()
    {
        byte[] epc = [0xE2, 0x00, 0x34, 0x12, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var bytes = TagFrame(epc).ToBytes();

        var noisy = new List<byte> { 0x11, 0x22, 0x33 };
        noisy.AddRange(bytes);

        var reader = new D2184FrameReader();
        Assert.Single(reader.Append(noisy.ToArray()));
        Assert.Equal(3, reader.DiscardedBytes);
    }
}
