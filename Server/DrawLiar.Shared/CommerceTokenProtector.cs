using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace DrawLiar.Server;

public sealed class CommerceTokenProtector
{
    private readonly byte[] _key;

    public CommerceTokenProtector(byte[] key)
    {
        if (key.Length != 32) throw new InvalidOperationException("결제 토큰 암호화 키는 32바이트여야 합니다.");
        _key = (byte[])key.Clone();
    }

    public static CommerceTokenProtector? Create(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Commerce:GooglePlay:Enabled")) return null;
        string path = configuration["Commerce:GooglePlay:TokenEncryptionKeyFile"] ?? "";
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException("Commerce:GooglePlay:TokenEncryptionKeyFile에 영구 보존할 별도 32바이트 키 파일의 절대 경로가 필요합니다.");
        try { return new CommerceTokenProtector(File.ReadAllBytes(path)); }
        catch { throw new InvalidOperationException("결제 토큰 암호화 키 파일을 읽을 수 없거나 32바이트가 아닙니다."); }
    }

    public string Protect(CommerceStoredOrder order, string proof)
    {
        byte[] source = Encoding.UTF8.GetBytes(proof), payload = new byte[12 + 16 + source.Length];
        RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(payload.AsSpan(0, 12), source, payload.AsSpan(28), payload.AsSpan(12, 16), Binding(order));
        CryptographicOperations.ZeroMemory(source);
        return Convert.ToBase64String(payload);
    }

    public string Unprotect(CommerceStoredOrder order, string protectedProof)
    {
        byte[] payload = Convert.FromBase64String(protectedProof);
        if (payload.Length < 28) throw new CryptographicException("잘못된 결제 토큰 암호문입니다.");
        byte[] source = new byte[payload.Length - 28];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), source, Binding(order));
            return Encoding.UTF8.GetString(source);
        }
        finally { CryptographicOperations.ZeroMemory(source); }
    }

    private static byte[] Binding(CommerceStoredOrder order) => Encoding.UTF8.GetBytes(
        order.AccountId + ":" + order.Order.OrderId + ":" + order.Order.Provider + ":" + order.AppScope + ":" + order.Order.StoreProductId);
}
