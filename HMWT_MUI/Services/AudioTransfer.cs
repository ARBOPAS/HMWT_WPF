using System;
using System.Collections.Generic;
using System.IO;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Приём аудиофайла кусками и локальный кэш. Файл один раз скачивается
    /// в %APPDATA%\HMWT\Audio\Cache и дальше играет без сети.
    /// </summary>
    public static class AudioTransfer
    {
        private sealed class Download
        {
            public byte[][] Parts;
            public string Extension;
            public int Received;
            public int Total;
        }

        private static readonly Dictionary<string, Download> Downloads =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new object();

        /// <summary>Путь к уже скачанному файлу или null.</summary>
        public static string FindCached(string hash, string extension)
        {
            if (string.IsNullOrWhiteSpace(hash)) return null;
            string ext = Normalize(extension);
            string path = Path.Combine(AudioService.CacheDir, hash + ext);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Добавляет кусок. Когда пришли все — склеивает, пишет в кэш и
        /// возвращает путь; иначе null.
        /// </summary>
        public static string AddChunk(AudioChunk chunk)
        {
            if (chunk == null || string.IsNullOrWhiteSpace(chunk.Hash)) return null;

            lock (Gate)
            {
                if (!Downloads.TryGetValue(chunk.Hash, out var download))
                {
                    if (chunk.Total <= 0) return null;
                    download = new Download
                    {
                        Parts = new byte[chunk.Total][],
                        Total = chunk.Total,
                        Extension = Normalize(chunk.Extension)
                    };
                    Downloads[chunk.Hash] = download;
                }

                if (chunk.Index < 0 || chunk.Index >= download.Parts.Length) return null;
                if (download.Parts[chunk.Index] == null)
                {
                    download.Parts[chunk.Index] = Convert.FromBase64String(chunk.Data ?? "");
                    download.Received++;
                }

                if (download.Received < download.Total) return null;

                using var buffer = new MemoryStream();
                foreach (var part in download.Parts)
                {
                    if (part == null) return null;
                    buffer.Write(part, 0, part.Length);
                }

                AudioService.EnsureFolders();
                string path = Path.Combine(AudioService.CacheDir, chunk.Hash + download.Extension);
                File.WriteAllBytes(path, buffer.ToArray());
                Downloads.Remove(chunk.Hash);
                return path;
            }
        }

        private static string Normalize(string extension)
        {
            string ext = (extension ?? "").Trim().ToLowerInvariant();
            if (ext.Length == 0) return ".mp3";
            return ext.StartsWith(".") ? ext : "." + ext;
        }
    }
}
