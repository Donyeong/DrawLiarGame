using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DrawLiar.Server;

public sealed record VerifiedAdReward(string Ticket, string UserId, string TransactionId, DateTimeOffset RewardedAt);

public sealed class AdMobSsvVerifier(HttpClient client)
{
    private const string KEYS_URL = "https://www.gstatic.com/admob/reward/verifier-keys.json";
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private IReadOnlyDictionary<long, byte[]> _keys = new Dictionary<long, byte[]>();
    private DateTimeOffset _keysFetchedAt;

    public async Task<VerifiedAdReward?> VerifyAsync(string rawQuery, string expectedAdUnit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rawQuery) || rawQuery.Length > 16384 || rawQuery[0] != '?') throw Invalid();
        string query = rawQuery[1..];
        int signatureOffset = query.IndexOf("&signature=", StringComparison.Ordinal);
        if (signatureOffset < 1) throw Invalid();
        string content = query[..signatureOffset];
        string[] ending = query[(signatureOffset + 1)..].Split('&');
        if (ending.Length != 2 || !ending[0].StartsWith("signature=", StringComparison.Ordinal)
            || !ending[1].StartsWith("key_id=", StringComparison.Ordinal)
            || !long.TryParse(ending[1][7..], NumberStyles.None, CultureInfo.InvariantCulture, out long keyId)) throw Invalid();
        byte[] signature;
        try
        {
            string encoded = Decode(ending[0][10..]).Replace('-', '+').Replace('_', '/');
            signature = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
        }
        catch (Exception exception) when (exception is FormatException or UriFormatException) { throw Invalid(); }
        byte[] key = await KeyAsync(keyId, cancellationToken);
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out int consumed);
            if (consumed != key.Length || !ecdsa.VerifyData(Encoding.UTF8.GetBytes(content), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) throw Invalid();
        }
        catch (CryptographicException) { throw Invalid(); }
        // 서명이 유효한 Google 요청 중 보상 대상이 아닌 콜백(콘솔 URL 확인 등)은 null로 수락한다.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in content.Split('&'))
        {
            int split = pair.IndexOf('=');
            if (split < 1) return null;
            try { if (!parameters.TryAdd(Decode(pair[..split]), Decode(pair[(split + 1)..]))) return null; }
            catch (UriFormatException) { return null; }
        }
        string Value(string name) => parameters.TryGetValue(name, out string? value) ? value : "";
        string adUnit = Value("ad_unit");
        if (adUnit != expectedAdUnit && adUnit != expectedAdUnit[(expectedAdUnit.LastIndexOf('/') + 1)..]) return null;
        string ticket = Value("custom_data"), user = Value("user_id"), transaction = Value("transaction_id");
        if (ticket.Length != 64 || !ticket.All(Uri.IsHexDigit) || user.Length != 64 || !user.All(Uri.IsHexDigit)
            || transaction.Length is < 1 or > 256 || transaction.Any(char.IsControl)
            || !long.TryParse(Value("timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp)
            || !int.TryParse(Value("reward_amount"), NumberStyles.None, CultureInfo.InvariantCulture, out int reward) || reward < 1)
            return null;
        DateTimeOffset rewardedAt;
        try { rewardedAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp); }
        catch (ArgumentOutOfRangeException) { return null; }
        return new VerifiedAdReward(ticket, user, transaction, rewardedAt);
    }

    private async Task<byte[]> KeyAsync(long keyId, CancellationToken cancellationToken)
    {
        await _keyGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _keysFetchedAt >= TimeSpan.FromHours(6)
                || !_keys.ContainsKey(keyId) && now - _keysFetchedAt >= TimeSpan.FromMinutes(1))
            {
                try
                {
                    using var response = await client.GetAsync(KEYS_URL, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is > 65536) throw new HttpRequestException();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[4096];
                    int count;
                    while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
                    {
                        if (buffer.Length + count > 65536) throw new HttpRequestException();
                        buffer.Write(chunk, 0, count);
                    }
                    using var document = JsonDocument.Parse(buffer.ToArray());
                    var keys = new Dictionary<long, byte[]>();
                    foreach (var item in document.RootElement.GetProperty("keys").EnumerateArray())
                        keys.Add(item.GetProperty("keyId").GetInt64(), Convert.FromBase64String(item.GetProperty("base64").GetString()!));
                    if (keys.Count is < 1 or > 32) throw new JsonException();
                    _keys = keys; _keysFetchedAt = now;
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException
                    or InvalidOperationException or KeyNotFoundException or ArgumentException)
                { throw new ApiException("AdsUnavailable", 503); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new ApiException("AdsUnavailable", 503); }
            }
            return _keys.TryGetValue(keyId, out var key) ? key : throw Invalid();
        }
        finally { _keyGate.Release(); }
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", " "));
    private static ApiException Invalid() => new("InvalidAdVerification", 403);
}
