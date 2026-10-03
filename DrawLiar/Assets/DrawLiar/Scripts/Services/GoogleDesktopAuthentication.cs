#if (UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX)
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DrawLiar
{
	internal static class GoogleDesktopAuthentication
	{
		private const string AUTHORIZATION_ENDPOINT = "https://accounts.google.com/o/oauth2/v2/auth";
		internal sealed class Authorization
		{
			public string Code { get; set; }
			public string CodeVerifier { get; set; }
			public string RedirectUri { get; set; }
		}

		public static async Task<Authorization> AuthenticateAsync(string clientId, string nonce, Action<string> openBrowser, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(clientId) || !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
				|| clientId.Length > 256 || string.IsNullOrWhiteSpace(nonce) || nonce.Length > 1024) throw new InvalidOperationException();
			cancellationToken.ThrowIfCancellationRequested();
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromMinutes(4));
			using var callback = new LoopbackSession();
			string verifier = CreateRandomValue();
			// 브라우저 열기는 호출자의 Unity 메인 스레드에서 첫 await 이전에 실행한다.
			openBrowser(BuildAuthorizationUrl(clientId, nonce, callback.RedirectUri, callback.State, verifier));
			string code = await callback.ReceiveCodeAsync(timeout.Token).ConfigureAwait(false);
			timeout.Token.ThrowIfCancellationRequested();
			return new Authorization { Code = code, CodeVerifier = verifier, RedirectUri = callback.RedirectUri };
		}

		internal static string CreateRandomValue()
		{
			byte[] bytes = new byte[32];
			using var random = RandomNumberGenerator.Create();
			random.GetBytes(bytes);
			return Base64Url(bytes);
		}
		private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		internal static string CreateCodeChallenge(string verifier)
		{
			using var hash = SHA256.Create();
			return Base64Url(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
		}
		internal static string BuildAuthorizationUrl(string clientId, string nonce, string redirectUri, string state, string verifier) =>
			AUTHORIZATION_ENDPOINT + "?client_id=" + Uri.EscapeDataString(clientId) + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
			+ "&response_type=code&scope=openid&code_challenge_method=S256&code_challenge=" + CreateCodeChallenge(verifier)
			+ "&state=" + Uri.EscapeDataString(state) + "&nonce=" + Uri.EscapeDataString(nonce) + "&prompt=select_account";

		internal sealed class LoopbackSession : IDisposable
		{
			internal const int MAX_REQUEST_BYTES = 8192;
			private const string CALLBACK_PATH = "/oauth2/callback";
			private readonly TcpListener _listener;
			private readonly string _host;
			public string State { get; } = CreateRandomValue();
			public string RedirectUri { get; }

			public LoopbackSession()
			{
				_listener = new TcpListener(IPAddress.Loopback, 0);
				_listener.Start(4);
				_host = "127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
				RedirectUri = "http://" + _host + CALLBACK_PATH;
			}
			public void Dispose() => _listener.Stop();

			public async Task<string> ReceiveCodeAsync(CancellationToken cancellationToken)
			{
				using var cancellation = cancellationToken.Register(_listener.Stop);
				try
				{
					for (int attempt = 0; attempt < 32; attempt++)
					{
						cancellationToken.ThrowIfCancellationRequested();
						using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
						if (!(client.Client.RemoteEndPoint is IPEndPoint remote) || !IPAddress.IsLoopback(remote.Address)) continue;
						using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
						connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
						using var closeConnection = connectionTimeout.Token.Register(client.Close);
						using var stream = client.GetStream();
						try
						{
							string header = await ReadHeaderAsync(stream, connectionTimeout.Token).ConfigureAwait(false);
							bool valid = TryParseCallback(header, _host, State, out string code, out string error);
							await WriteResponseAsync(stream, valid, connectionTimeout.Token).ConfigureAwait(false);
							if (!valid) continue;
							if (error == "access_denied") throw new OperationCanceledException(cancellationToken);
							if (error != null) throw new InvalidOperationException();
							return code;
						}
						catch (IOException) { cancellationToken.ThrowIfCancellationRequested(); }
						catch (SocketException) { cancellationToken.ThrowIfCancellationRequested(); }
						catch (ObjectDisposedException) { cancellationToken.ThrowIfCancellationRequested(); }
						catch (OperationCanceledException) when (connectionTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
					}
					throw new InvalidOperationException();
				}
				catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
				finally { _listener.Stop(); }
			}

			private static async Task<string> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
			{
				byte[] buffer = new byte[MAX_REQUEST_BYTES];
				int count = 0;
				while (count < buffer.Length)
				{
					int read = await stream.ReadAsync(buffer, count, buffer.Length - count, cancellationToken).ConfigureAwait(false);
					if (read == 0) return null;
					int previous = count;
					count += read;
					for (int i = previous; i < count; i++)
						if ((buffer[i] < 32 && buffer[i] != 13 && buffer[i] != 10 && buffer[i] != 9) || buffer[i] > 126) return null;
					for (int i = Math.Max(0, previous - 3); i <= count - 4; i++)
						if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
							return i + 4 == count ? Encoding.ASCII.GetString(buffer, 0, count) : null;
				}
				return null;
			}

			internal static bool TryParseCallback(string request, string host, string expectedState, out string code, out string error)
			{
				code = null; error = null;
				if (request == null || request.Length > MAX_REQUEST_BYTES || !request.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return false;
				string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
				string[] first = lines[0].Split(' ');
				if (first.Length != 3 || first[0] != "GET" || first[2] != "HTTP/1.1" || !first[1].StartsWith(CALLBACK_PATH + "?", StringComparison.Ordinal)) return false;
				var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				bool hasHost = false;
				for (int i = 1; i < lines.Length - 2; i++)
				{
					int colon = lines[i].IndexOf(':');
					if (colon <= 0) return false;
					string name = lines[i].Substring(0, colon);
					foreach (char c in name) if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && c != '-') return false;
					if (!headers.Add(name)) return false;
					string value = lines[i].Substring(colon + 1).Trim();
					if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) { if (value != host) return false; hasHost = true; }
					if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
						|| name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0") return false;
				}
				if (!hasHost) return false;
				string[] pairs = first[1].Substring(CALLBACK_PATH.Length + 1).Split('&');
				if (pairs.Length > 16) return false;
				var query = new Dictionary<string, string>(StringComparer.Ordinal);
				foreach (string pair in pairs)
				{
					int equals = pair.IndexOf('=');
					if (equals <= 0 || !TryDecode(pair.Substring(0, equals), out string key) || !TryDecode(pair.Substring(equals + 1), out string value)
						|| query.ContainsKey(key)) return false;
					query.Add(key, value);
				}
				if (!query.TryGetValue("state", out string state) || !StatesMatch(state, expectedState)) return false;
				bool hasCode = query.TryGetValue("code", out string candidateCode);
				bool hasError = query.TryGetValue("error", out string candidateError);
				if (hasCode == hasError || hasCode && (string.IsNullOrWhiteSpace(candidateCode) || candidateCode.Length > 1024)
					|| hasError && string.IsNullOrWhiteSpace(candidateError)) return false;
				code = candidateCode; error = candidateError;
				return true;
			}

			private static bool TryDecode(string value, out string decoded)
			{
				decoded = null;
				for (int i = 0; i < value.Length; i++)
				{
					char c = value[i];
					if (c < 33 || c > 126 || c == '#') return false;
					if (c == '%')
					{
						if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return false;
						i += 2;
					}
				}
				decoded = Uri.UnescapeDataString(value.Replace('+', ' '));
				foreach (char c in decoded) if (char.IsControl(c)) return false;
				return true;
			}
			private static bool StatesMatch(string value, string expected)
			{
				if (value.Length != expected.Length) return false;
				int difference = 0;
				for (int i = 0; i < value.Length; i++) difference |= value[i] ^ expected[i];
				return difference == 0;
			}

			private static async Task WriteResponseAsync(NetworkStream stream, bool valid, CancellationToken cancellationToken)
			{
				string text = valid ? "Google 인증 응답을 받았습니다. 이 창을 닫고 게임으로 돌아가 로그인 결과를 확인하세요."
					: "올바르지 않은 인증 요청입니다. 게임으로 돌아가 다시 시도하세요.";
				byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"ko\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>DrawLiar</title><body><h1>DrawLiar</h1><p>" + text + "</p></body></html>");
				byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (valid ? "200 OK" : "400 Bad Request")
					+ "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length
					+ "\r\nConnection: close\r\nCache-Control: no-store\r\nPragma: no-cache\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nX-Content-Type-Options: nosniff\r\n\r\n");
				await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
				await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
			}
		}
	}
}
#endif
