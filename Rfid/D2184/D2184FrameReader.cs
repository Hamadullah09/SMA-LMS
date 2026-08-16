namespace Library_Management_system.Rfid.D2184;

/// <summary>
/// Reassembles frames from a TCP or serial byte stream.
///
/// Neither transport preserves message boundaries: one read can deliver half a frame, or three
/// frames plus a fragment. The vendor SDK handles this with a 4096-byte scratch buffer and index
/// arithmetic (ReaderMethod.RunReceiveDataCallback); this is the same idea written so that a
/// desynchronised stream recovers instead of silently dropping data.
///
/// Not thread-safe by design - one instance belongs to one connection, which has one read loop.
/// </summary>
public sealed class D2184FrameReader
{
    private const int MaxBufferBytes = 8192;

    private readonly List<byte> _buffer = new(1024);

    /// <summary>Bytes discarded while resynchronising. Surfaced for reader health diagnostics.</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>
    /// Append newly received bytes and return every complete frame now available.
    /// Incomplete trailing data is retained for the next call.
    /// </summary>
    public IReadOnlyList<D2184Frame> Append(ReadOnlySpan<byte> incoming)
    {
        if (_buffer.Count + incoming.Length > MaxBufferBytes)
        {
            // A stream this desynchronised will not recover by accumulating more of it.
            DiscardedBytes += _buffer.Count;
            _buffer.Clear();
        }

        _buffer.AddRange(incoming);

        var frames = new List<D2184Frame>();

        while (true)
        {
            var headerIndex = _buffer.IndexOf(D2184Frame.Header);
            if (headerIndex < 0)
            {
                // Nothing usable in the buffer at all.
                DiscardedBytes += _buffer.Count;
                _buffer.Clear();
                break;
            }

            if (headerIndex > 0)
            {
                // Junk before the header - drop it and resynchronise.
                DiscardedBytes += headerIndex;
                _buffer.RemoveRange(0, headerIndex);
            }

            // Need at least header + len to know how long the frame is.
            if (_buffer.Count < 2)
            {
                break;
            }

            var frameLength = _buffer[1] + 2;
            if (frameLength < 5)
            {
                // Impossible length - this 0xA0 was payload, not a header.
                DiscardedBytes++;
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < frameLength)
            {
                // Frame not fully arrived yet.
                break;
            }

            var candidate = CollectionsMarshalSlice(frameLength);
            var frame = D2184Frame.TryParse(candidate);

            if (frame is null)
            {
                // Checksum failed, so this 0xA0 was not really a frame start. Skip one byte and
                // look for the next header rather than discarding the whole candidate - the real
                // frame may begin inside it.
                DiscardedBytes++;
                _buffer.RemoveAt(0);
                continue;
            }

            frames.Add(frame);
            _buffer.RemoveRange(0, frameLength);
        }

        return frames;
    }

    public void Reset()
    {
        _buffer.Clear();
    }

    private byte[] CollectionsMarshalSlice(int length)
    {
        var slice = new byte[length];
        _buffer.CopyTo(0, slice, 0, length);
        return slice;
    }
}
