using System.Text;

namespace Library_Management_system.Rfid.D2184;

/// <summary>
/// One protocol frame.
///
/// Layout (protocol document section 1, vendor SDK MessageTran.cs):
///
///   [0]      0xA0            packet header
///   [1]      Len             byte count AFTER this byte, i.e. address + cmd + data + checksum
///   [2]      Address         reader address
///   [3]      Cmd             command code
///   [4..]    Data            command parameters, may be empty
///   [last]   Checksum        two's complement of the sum of every preceding byte
///
/// Total frame length is therefore Len + 2, and Data length is Len - 3.
/// </summary>
public sealed class D2184Frame
{
    public const byte Header = 0xA0;

    /// <summary>Address + Cmd + Checksum - the three bytes counted by Len besides the data.</summary>
    private const int LenOverhead = 3;

    public byte Address { get; }
    public byte Command { get; }
    public byte[] Data { get; }

    public D2184Frame(byte address, byte command, byte[]? data = null)
    {
        Address = address;
        Command = command;
        Data = data ?? [];
    }

    /// <summary>Serialise to the wire, computing Len and the checksum.</summary>
    public byte[] ToBytes()
    {
        var frame = new byte[Data.Length + 5];
        frame[0] = Header;
        frame[1] = (byte)(Data.Length + LenOverhead);
        frame[2] = Address;
        frame[3] = Command;
        Data.CopyTo(frame, 4);
        frame[^1] = CheckSum(frame, 0, frame.Length - 1);
        return frame;
    }

    /// <summary>
    /// Two's complement checksum over the given span, matching the vendor SDK exactly:
    /// <c>((~sum) + 1) &amp; 0xFF</c>.
    /// </summary>
    public static byte CheckSum(ReadOnlySpan<byte> buffer, int start, int length)
    {
        byte sum = 0;
        for (var i = start; i < start + length; i++)
        {
            sum += buffer[i];
        }

        return (byte)(((~sum) + 1) & 0xFF);
    }

    /// <summary>
    /// Parse one complete frame. Returns null when the buffer does not hold a valid frame -
    /// a bad checksum is treated as "not a frame", never as a frame with wrong data.
    /// </summary>
    public static D2184Frame? TryParse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5 || frame[0] != Header)
        {
            return null;
        }

        var declaredLength = frame[1] + 2;
        if (frame.Length != declaredLength)
        {
            return null;
        }

        var expected = CheckSum(frame, 0, frame.Length - 1);
        if (expected != frame[^1])
        {
            return null;
        }

        var dataLength = frame[1] - LenOverhead;
        var data = dataLength > 0 ? frame.Slice(4, dataLength).ToArray() : [];
        return new D2184Frame(frame[2], frame[3], data);
    }

    public string ToHex() => Convert.ToHexString(ToBytes());

    public override string ToString() =>
        $"A0 len={Data.Length + LenOverhead} addr={Address:X2} cmd={Command:X2} data={Convert.ToHexString(Data)}";
}
