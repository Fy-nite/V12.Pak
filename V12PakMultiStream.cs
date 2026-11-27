namespace V12.Pak;

/// <summary>
/// Read-only, seekable <see cref="Stream"/> over a split pak's parts, presented
/// as the one archive they encode.
///
/// The parts are a byte-level split, not one gzip member each, so this is a
/// plain concatenation. The loader's cache hash and the gzip/tar reader both see
/// a normal stream and need no special case.
/// </summary>
public sealed class V12PakMultiStream : Stream
{
    private readonly List<FileStream> _parts;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;

    private V12PakMultiStream(List<FileStream> parts)
    {
        _parts = parts;
        _starts = new long[parts.Count];

        long offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            _starts[i] = offset;
            offset += parts[i].Length;
        }
        _length = offset;
    }

    /// <summary>Opens every part. The caller owns nothing; dispose this to close them.</summary>
    public static V12PakMultiStream Open(IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("At least one pak part is required.", nameof(parts));

        var streams = new List<FileStream>(parts.Count);
        try
        {
            foreach (string path in parts)
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Split pak part not found: {path}", path);

                streams.Add(new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1 << 16,
                    FileOptions.None));
            }

            return new V12PakMultiStream(streams);
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    /// <summary>Number of parts backing this stream.</summary>
    public int PartCount => _parts.Count;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0 || _position >= _length)
            return 0;

        int total = 0;
        while (buffer.Length > 0 && _position < _length)
        {
            int index = IndexAt(_position);
            FileStream part = _parts[index];
            long local = _position - _starts[index];

            part.Position = local;
            int want = (int)Math.Min(buffer.Length, part.Length - local);
            if (want <= 0) break;

            int read = part.Read(buffer[..want]);
            if (read <= 0) break;

            buffer = buffer[read..];
            _position += read;
            total += read;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
            throw new IOException("Attempted to seek before the start of the split pak.");

        _position = Math.Min(target, _length);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var part in _parts) part.Dispose();
            _parts.Clear();
        }

        base.Dispose(disposing);
    }

    private int IndexAt(long position)
    {
        int index = Array.BinarySearch(_starts, position);
        if (index < 0) index = ~index - 1;
        return Math.Clamp(index, 0, _parts.Count - 1);
    }
}
