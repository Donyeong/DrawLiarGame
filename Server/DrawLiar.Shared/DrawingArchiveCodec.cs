using System.IO.Compression;
using System.Text.Json;

namespace DrawLiar.Server;

public static class DrawingArchiveCodec
{
    public static bool TryEncode(RoundDrawingData drawing, out RoundDrawingArchiveData archive)
    {
        archive = new RoundDrawingArchiveData();
        if (drawing == null || drawing.Round is < 1 or > DrawingReplayRules.MAX_ROUNDS
            || drawing.Strokes == null || drawing.Strokes.Length > DrawingReplayRules.MAX_STROKES_PER_ROUND) return false;
        try
        {
            using var payload = new MemoryStream();
            using var compressedLimit = new BoundedStream(payload, DrawingReplayRules.MAX_COMPRESSED_ROUND_BYTES, true);
            using (var gzip = new GZipStream(compressedLimit, CompressionLevel.Fastest, true))
            using (var jsonLimit = new BoundedStream(gzip, DrawingReplayRules.MAX_ROUND_JSON_BYTES, true))
                JsonSerializer.Serialize(jsonLimit, drawing, ServerRuntime.Json);
            archive = new RoundDrawingArchiveData { Round = drawing.Round, Payload = payload.ToArray() };
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    public static RoundDrawingData Decode(RoundDrawingArchiveData archive)
    {
        if (archive == null || archive.Round is < 1 or > DrawingReplayRules.MAX_ROUNDS
            || archive.Payload == null || archive.Payload.Length == 0 || archive.Payload.Length > DrawingReplayRules.MAX_COMPRESSED_ROUND_BYTES)
            throw new InvalidDataException("그림 보관 데이터의 크기가 올바르지 않습니다.");
        try
        {
            using var payload = new MemoryStream(archive.Payload, false);
            using var gzip = new GZipStream(payload, CompressionMode.Decompress);
            using var jsonLimit = new BoundedStream(gzip, DrawingReplayRules.MAX_ROUND_JSON_BYTES, false);
            var drawing = JsonSerializer.Deserialize<RoundDrawingData>(jsonLimit, ServerRuntime.Json);
            if (drawing == null || drawing.Round != archive.Round)
                throw new InvalidDataException("그림 보관 데이터의 라운드가 올바르지 않습니다.");
            return drawing;
        }
        catch (JsonException exception) { throw new InvalidDataException("그림 보관 데이터의 형식이 올바르지 않습니다.", exception); }
        catch (IOException exception)
        { throw new InvalidDataException("그림 보관 데이터를 읽을 수 없습니다.", exception); }
    }

    private sealed class BoundedStream(Stream stream, long limit, bool writable) : Stream
    {
        private long _transferred;
        public override bool CanRead => !writable;
        public override bool CanSeek => false;
        public override bool CanWrite => writable;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = stream.Read(buffer);
            Count(read);
            return read;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Count(buffer.Length);
            stream.Write(buffer);
        }
        private void Count(int count)
        {
            if (count > limit - _transferred) throw new InvalidDataException("그림 보관 데이터의 크기 한도를 초과했습니다.");
            _transferred += count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
