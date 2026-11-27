using System.Security.Cryptography;

namespace V12.Pak;

/// <summary>
/// Write-only stream that rolls over into numbered part files, so a pak can be
/// produced in chunks without writing a whole archive first and splitting it.
///
/// The parts are a byte-level split of one gzip stream. Each part's size and
/// SHA-256 are recorded as it closes, which gives a delivery layer something to
/// verify and resume against. When everything fits in one part, that part is
/// renamed to the logical <c>.v12pak</c> path on dispose, so a small build looks
/// exactly like an unsplit one.
/// </summary>
public sealed class V12PakPartStream : Stream
{
    private readonly string _logicalPath;
    private readonly string _directory;
    private readonly long _maxPartBytes;
    private readonly List<string> _parts = new();
    private readonly Dictionary<string, V12PakPartInfo> _partInfo = new(StringComparer.OrdinalIgnoreCase);

    private FileStream? _current;
    private string? _currentPath;
    private IncrementalHash? _hash;
    private long _currentBytes;
    private long _written;
    private bool _disposed;

    public V12PakPartStream(string logicalPakPath, long maxPartBytes)
    {
        if (string.IsNullOrWhiteSpace(logicalPakPath))
            throw new ArgumentException("A logical pak path is required.", nameof(logicalPakPath));
        if (maxPartBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPartBytes), "Part size must be positive.");

        _logicalPath = Path.GetFullPath(logicalPakPath);
        _directory = Path.GetDirectoryName(_logicalPath)!;
        _maxPartBytes = maxPartBytes;

        Directory.CreateDirectory(_directory);

        // A previous build may have left a whole pak or more parts than this one
        // will produce; either would be picked up by discovery.
        if (File.Exists(_logicalPath)) File.Delete(_logicalPath);
        foreach (string stale in V12PakParts.EnumeratePartFiles(_logicalPath))
        {
            try { File.Delete(stale); }
            catch (IOException) { }
        }
    }

    /// <summary>Part paths written so far, in order. Becomes one logical pak when only one part was needed.</summary>
    public IReadOnlyList<string> PartPaths => _parts;

    /// <summary>Size and SHA-256 per part file name. Empty for a single-part build.</summary>
    public IReadOnlyDictionary<string, V12PakPartInfo> PartInfo => _partInfo;

    /// <summary>True when more than one part file was written.</summary>
    public bool IsSplit => _parts.Count > 1;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (buffer.Length > 0)
        {
            if (_current is null) OpenNextPart();

            long room = _maxPartBytes - _currentBytes;
            if (room <= 0)
            {
                OpenNextPart();
                continue;
            }

            int count = (int)Math.Min(buffer.Length, room);
            _current!.Write(buffer[..count]);
            _hash!.AppendData(buffer[..count]);
            buffer = buffer[count..];

            _currentBytes += count;
            _written += count;
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> one = stackalloc byte[1];
        one[0] = value;
        Write(one);
    }

    public override void Flush() => _current?.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            if (disposing)
            {
                SealCurrentPart();

                if (_parts.Count == 1)
                {
                    PromoteSinglePart();
                    _partInfo.Clear();
                }
            }
        }

        base.Dispose(disposing);
    }

    private void OpenNextPart()
    {
        SealCurrentPart();

        if (_parts.Count >= 999)
        {
            throw new InvalidOperationException(
                "A split pak is limited to 999 parts. Increase the part size.");
        }

        _currentPath = V12PakParts.PartPath(_logicalPath, _parts.Count + 1);
        _parts.Add(_currentPath);

        _current = new FileStream(
            _currentPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1 << 16);

        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _currentBytes = 0;
    }

    private void SealCurrentPart()
    {
        if (_current is null || _hash is null || _currentPath is null)
            return;

        _current.Dispose();
        _current = null;

        byte[] digest = _hash.GetHashAndReset();
        _hash.Dispose();
        _hash = null;

        _partInfo[Path.GetFileName(_currentPath)] = new V12PakPartInfo(
            _currentBytes,
            Convert.ToHexString(digest).ToLowerInvariant());

        _currentPath = null;
        _currentBytes = 0;
    }

    private void PromoteSinglePart()
    {
        string only = _parts[0];
        if (string.Equals(only, _logicalPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (File.Exists(_logicalPath)) File.Delete(_logicalPath);
        File.Move(only, _logicalPath);
        _parts[0] = _logicalPath;
    }
}
