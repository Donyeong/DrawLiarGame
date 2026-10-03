#if !UNITY_SERVER && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR) || DRAWLIAR_WINDOWS_GUEST_STORAGE_TESTS)
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DrawLiar
{
    internal static class WindowsGuestCredentials
    {
        private const string FILE_NAME = "guest-v1.bin";
        private const int MAX_PROTECTED_LENGTH = 16384;
        private const uint CRYPTPROTECT_UI_FORBIDDEN = 1;
        private static readonly byte[] ENTROPY = Encoding.UTF8.GetBytes("com.rascallab.drawliar.guest.v1");
        private static readonly Encoding UTF8 = new UTF8Encoding(false, true);

        public static string Load(string directory)
        {
            string path = Path.Combine(directory, FILE_NAME);
            if (!File.Exists(path)) return null;
            byte[] encrypted = null;
            byte[] plaintext = null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (stream.Length <= 0 || stream.Length > MAX_PROTECTED_LENGTH) throw new CryptographicException();
                    encrypted = new byte[(int)stream.Length];
                    int offset = 0;
                    while (offset < encrypted.Length)
                    {
                        int read = stream.Read(encrypted, offset, encrypted.Length - offset);
                        if (read == 0) throw new CryptographicException();
                        offset += read;
                    }
                }
                plaintext = Transform(encrypted, false);
                if (plaintext.Length > 4096) throw new CryptographicException();
                return UTF8.GetString(plaintext);
            }
            finally { Clear(encrypted); Clear(plaintext); }
        }

        public static string Save(string directory, string payload, bool onlyIfMissing)
        {
            byte[] plaintext = null;
            byte[] encrypted = null;
            string temporary = null;
            try
            {
                if (string.IsNullOrEmpty(payload) || payload.Length > 4096) throw new CryptographicException();
                string path = Path.Combine(directory, FILE_NAME);
                if (onlyIfMissing && File.Exists(path)) return Load(directory);
                plaintext = UTF8.GetBytes(payload);
                encrypted = Transform(plaintext, true);
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(encrypted, 0, encrypted.Length);
                    stream.Flush(true);
                }
                if (!onlyIfMissing && File.Exists(path)) File.Replace(temporary, path, null);
                else
                {
                    try { File.Move(temporary, path); }
                    catch (IOException) when (onlyIfMissing && File.Exists(path)) { return Load(directory); }
                }
                temporary = null;
                return payload;
            }
            finally
            {
                Clear(plaintext); Clear(encrypted);
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public static void Delete(string directory) => File.Delete(Path.Combine(directory, FILE_NAME));

        private static byte[] Transform(byte[] source, bool protect)
        {
            DataBlob input = default;
            DataBlob extra = default;
            DataBlob output = default;
            try
            {
                input = Allocate(source);
                extra = Allocate(ENTROPY);
                bool success = protect
                    ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
                if (!success || output.Data == IntPtr.Zero || output.Length <= 0 || output.Length > MAX_PROTECTED_LENGTH)
                    throw new CryptographicException();
                byte[] result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { Free(input, false); Free(extra, false); Free(output, true); }
        }

        private static DataBlob Allocate(byte[] bytes)
        {
            IntPtr data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);
            return new DataBlob { Length = bytes.Length, Data = data };
        }

        private static void Free(DataBlob blob, bool local)
        {
            if (blob.Data == IntPtr.Zero) return;
            for (int index = 0; index < blob.Length; index++) Marshal.WriteByte(blob.Data, index, 0);
            if (local) LocalFree(blob.Data);
            else Marshal.FreeHGlobal(blob.Data);
        }

        private static void Clear(byte[] bytes) { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("Crypt32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("Kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
#endif
