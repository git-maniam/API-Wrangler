namespace ApiWrangler.Application;

// Enforces a byte budget before System.Text.Json can buffer an unbounded array element.
// JSON syntax validation remains the serializer's responsibility. The caller owns the source.
internal sealed class JsonRecordBoundStream(Stream source, int maxRecordBytes) : Stream
{
    private int depth;
    private int recordBytes;
    private bool inString;
    private bool escaped;

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (!inString && depth == 1 && value is (byte)',' or (byte)']')
            {
                recordBytes = 0;
            }
            else if (depth >= 1)
            {
                if (recordBytes == maxRecordBytes)
                {
                    throw new FormatException("JSON array record exceeds the configured byte limit.");
                }
                recordBytes++;
            }
            if (inString)
            {
                if (escaped) { escaped = false; }
                else if (value == (byte)'\\') { escaped = true; }
                else if (value == (byte)'"') { inString = false; }
            }
            else if (value == (byte)'"') { inString = true; }
            else if (value is (byte)'[' or (byte)'{') { depth++; }
            else if (value is (byte)']' or (byte)'}') { depth--; }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = source.Read(buffer[..Math.Min(buffer.Length, 4096)]);
        Inspect(buffer[..count]);
        return count;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await source.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], cancellationToken).ConfigureAwait(false);
        Inspect(buffer.Span[..count]);
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
