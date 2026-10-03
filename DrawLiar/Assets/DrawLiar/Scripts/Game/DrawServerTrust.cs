using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UnityEngine.Networking;

namespace DrawLiar
{
    public static class DrawServerTrust
    {
        public static bool ValidateCertificate(X509Certificate certificate, string pin, SslPolicyErrors errors)
        {
            if (string.IsNullOrWhiteSpace(pin)) return errors == SslPolicyErrors.None;
            if (certificate == null) return false;
            string normalized = pin.Replace(":", "").Replace(" ", "").ToUpperInvariant();
            if (normalized.Length != 64) return false;
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-", "") != normalized) return false;
            using (var serverCertificate = new X509Certificate2(certificate))
                return DateTime.UtcNow >= serverCertificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= serverCertificate.NotAfter.ToUniversalTime();
        }
        public static CertificateHandler CreateCertificateHandler(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin)) return null;
            return new PinnedCertificateHandler(pin);
        }

        private sealed class PinnedCertificateHandler : CertificateHandler
        {
            private readonly string _pin;
            public PinnedCertificateHandler(string pin)
            {
                _pin = pin.Replace(":", "").Replace(" ", "").ToUpperInvariant();
                if (_pin.Length != 64) throw new ArgumentException("서버 인증서 SHA-256 설정이 올바르지 않습니다.");
            }
            protected override bool ValidateCertificate(byte[] certificateData)
            {
                if (certificateData == null) return false;
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(certificateData)).Replace("-", "") != _pin) return false;
                using (var certificate = new X509Certificate2(certificateData))
                    return DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime();
            }
        }

    }
}
