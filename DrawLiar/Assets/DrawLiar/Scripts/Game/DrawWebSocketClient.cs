using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DrawLiar
{
    public static class DrawWebSocketClient
    {
        private const int MAXIMUM_HEADER_BYTES = 16384;

        public static async Task<WebSocket> ConnectAsync(Uri uri, string certificatePin, CancellationToken cancellation)
        {
            if (uri.Scheme != "wss" && uri.Scheme != "ws" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("웹소켓 주소가 올바르지 않습니다.");
            var tcp = new TcpClient { NoDelay = true };
            Stream stream = null;
            try
            {
                using (cancellation.Register(() => tcp.Dispose()))
                {
                    await tcp.ConnectAsync(uri.Host, uri.Port);
                    stream = tcp.GetStream();
                    if (uri.Scheme == "wss")
                    {
                        var tls = new SslStream(stream, false, (sender, certificate, chain, errors) =>
                            DrawServerTrust.ValidateCertificate(certificate, certificatePin, errors));
                        stream = tls;
                        await tls.AuthenticateAsClientAsync(uri.Host, null, SslProtocols.Tls12, true);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    byte[] nonce = new byte[16];
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
                    string key = Convert.ToBase64String(nonce);
                    string request = "GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Authority
                        + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: " + key + "\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(request);
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellation);
                    var header = new List<byte>();
                    var one = new byte[1];
                    while (header.Count < MAXIMUM_HEADER_BYTES)
                    {
                        if (await stream.ReadAsync(one, 0, 1, cancellation) == 0) throw new IOException("웹소켓 응답이 끊겼습니다.");
                        header.Add(one[0]);
                        int count = header.Count;
                        if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                    }
                    ValidateHandshake(Encoding.ASCII.GetString(header.ToArray()), key);
                    cancellation.ThrowIfCancellationRequested();
                    return WebSocket.CreateFromStream(new OwnedStream(stream, tcp), false, null, TimeSpan.FromSeconds(15));
                }
            }
            catch
            {
                stream?.Dispose();
                tcp.Dispose();
                throw;
            }
        }

        private static void ValidateHandshake(string header, string key)
        {
            if (!header.EndsWith("\r\n\r\n", StringComparison.Ordinal)) throw new IOException("웹소켓 헤더가 너무 큽니다.");
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (!lines[0].StartsWith("HTTP/1.1 101 ", StringComparison.Ordinal)) throw new IOException("웹소켓 연결을 거부했습니다.");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < lines.Length && lines[index].Length > 0; index++)
            {
                int separator = lines[index].IndexOf(':');
                if (separator <= 0) throw new IOException("웹소켓 헤더가 올바르지 않습니다.");
                string name = lines[index].Substring(0, separator).Trim();
                if (fields.ContainsKey(name)) throw new IOException("웹소켓 응답 헤더가 중복됩니다.");
                fields[name] = lines[index].Substring(separator + 1).Trim();
            }
            string expected;
            using (var sha = SHA1.Create()) expected = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            if (!fields.TryGetValue("Sec-WebSocket-Accept", out string actual) || actual != expected
                || !fields.TryGetValue("Upgrade", out string upgrade) || !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
                || !fields.TryGetValue("Connection", out string connection) || Array.FindIndex(connection.Split(','), item => item.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) < 0
                || fields.ContainsKey("Sec-WebSocket-Extensions") || fields.ContainsKey("Sec-WebSocket-Protocol"))
                throw new IOException("웹소켓 전환 응답을 검증할 수 없습니다.");
        }

        private sealed class OwnedStream : Stream
        {
            private readonly Stream _stream;
            private readonly TcpClient _tcp;
            public OwnedStream(Stream stream, TcpClient tcp) { _stream = stream; _tcp = tcp; }
            public override bool CanRead => _stream.CanRead;
            public override bool CanWrite => _stream.CanWrite;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _stream.Flush();
            public override Task FlushAsync(CancellationToken token) => _stream.FlushAsync(token);
            public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => _stream.ReadAsync(buffer, offset, count, token);
            public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => _stream.WriteAsync(buffer, offset, count, token);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) { try { _stream.Dispose(); } finally { _tcp.Dispose(); } }
                base.Dispose(disposing);
            }
        }
    }
}
