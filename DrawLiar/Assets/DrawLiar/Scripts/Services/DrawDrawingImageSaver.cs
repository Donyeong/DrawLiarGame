using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace DrawLiar
{
    public sealed class DrawDrawingImageSaveResult
    {
        public string FilePath { get; }
        public bool SavedToGallery { get; }
        public bool DownloadedInBrowser { get; }

        internal DrawDrawingImageSaveResult(string filePath, bool savedToGallery = false, bool downloadedInBrowser = false)
        {
            FilePath = filePath ?? "";
            SavedToGallery = savedToGallery;
            DownloadedInBrowser = downloadedInBrowser;
        }
    }

    public static class DrawDrawingImageSaver
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int DrawBrowserDownloadDrawing(byte[] png, int length, string fileName);
#endif

        public static async Task<DrawDrawingImageSaveResult> SaveAsync(Texture2D texture, string fileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            byte[] png = texture.EncodeToPNG();
            if (png == null || png.Length == 0) throw new IOException();
            string name = CreateFileName(fileName);
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            if (DrawBrowserDownloadDrawing(png, png.Length, name) != 1) throw new IOException();
            return new DrawDrawingImageSaveResult("", downloadedInBrowser: true);
#elif UNITY_ANDROID && !UNITY_EDITOR
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AndroidJNI.AttachCurrentThread() < 0) throw new IOException();
                    try
                    {
                        using (var saver = new AndroidJavaClass("com.rascallab.drawliar.media.DrawDrawingImage"))
                        {
                            string location = saver.CallStatic<string>("Save", context, png, name);
                            if (string.IsNullOrEmpty(location)) throw new IOException();
                            return new DrawDrawingImageSaveResult(location, location.StartsWith("content://", StringComparison.Ordinal));
                        }
                    }
                    finally { AndroidJNI.DetachCurrentThread(); }
                }, cancellationToken);
            }
#else
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(directory)) directory = Application.persistentDataPath;
            directory = Path.GetFullPath(Path.Combine(directory, "DrawLiar"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            bool created = false;
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    created = true;
                    await stream.WriteAsync(png, 0, png.Length, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                return new DrawDrawingImageSaveResult(path);
            }
            catch
            {
                if (created)
                {
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
#endif
        }

        private static string CreateFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) value = "DrawLiar";
            if (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4);
            var builder = new StringBuilder();
            foreach (char character in value)
            {
                if (builder.Length >= 80) break;
                builder.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');
            }
            if (builder.Length == 0) builder.Append("DrawLiar");
            return builder + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
        }
    }
}
