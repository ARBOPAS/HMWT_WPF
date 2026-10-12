using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Net
{
    /// <summary>
    /// Передача фотографий между игроками.
    ///
    /// Как это работает и почему именно так:
    ///
    /// Порт открыт только у ведущего — у остальных игроков принять входящее
    /// соединение нельзя, роутер его не пропустит. Поэтому фото всегда идёт
    /// ЧЕРЕЗ ХОСТА:
    ///
    ///   1. клиент режет свой файл на части и шлёт их хосту;
    ///   2. хост собирает файл, кладёт его в папку комнаты и рассылает
    ///      те же части остальным участникам;
    ///   3. каждый сохраняет фото у себя и показывает его в отряде.
    ///
    /// Файл режется на части по <see cref="NetVersions.PhotoChunkSize"/>: одно
    /// сообщение с картинкой в несколько мегабайт заблокировало бы канал.
    /// </summary>
    public static class PhotoTransferService
    {
        /// <summary>Есть ли у пути расширение, которое стоит передавать.</summary>
        public static bool IsSupportedImage(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return false;

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            switch (ext)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".webp":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Режет файл на части для отправки.
        ///
        /// Возвращает пустой список, если файл слишком большой: лучше показать
        /// игроку заглушку, чем пытаться передать десятки мегабайт.
        /// </summary>
        public static List<PhotoChunk> Split(string fullPath, string ownerId, string roomId)
        {
            var result = new List<PhotoChunk>();
            if (!IsSupportedImage(fullPath)) return result;

            try
            {
                var bytes = File.ReadAllBytes(fullPath);
                if (bytes.Length == 0 || bytes.Length > NetVersions.PhotoMaxBytes)
                {
                    Debug.WriteLine($"[Фото] файл не передан: {bytes.Length} байт при пределе {NetVersions.PhotoMaxBytes}");
                    return result;
                }

                int size = NetVersions.PhotoChunkSize;
                int total = (bytes.Length + size - 1) / size;
                string ext = Path.GetExtension(fullPath).ToLowerInvariant();

                for (int i = 0; i < total; i++)
                {
                    int offset = i * size;
                    int length = Math.Min(size, bytes.Length - offset);
                    var part = new byte[length];
                    Buffer.BlockCopy(bytes, offset, part, 0, length);

                    result.Add(new PhotoChunk
                    {
                        OwnerId = ownerId,
                        Index = i,
                        Total = total,
                        Extension = ext,
                        Base64 = Convert.ToBase64String(part),
                        TotalBytes = bytes.Length
                    });
                }

                Debug.WriteLine($"[Фото] {Path.GetFileName(fullPath)}: {bytes.Length} байт, частей {total}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Фото] не удалось разбить файл: {ex.Message}");
                result.Clear();
            }

            return result;
        }

        /// <summary>
        /// Сохраняет собранное фото в папке комнаты и возвращает относительный
        /// путь для показа (относительно корня фотографий приложения).
        /// </summary>
        public static string SaveReceived(string roomId, string ownerId, byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length == 0) return "";

            try
            {
                string dir = PhotoHelper.GetRoomPhotoFolder(roomId);
                string safeOwner = MakeSafeName(ownerId);
                string safeExt = string.IsNullOrWhiteSpace(extension) ? ".png" : extension.ToLowerInvariant();

                // Прежние аватары этого игрока убираем: иначе копятся файлы,
                // а расширение могло измениться
                foreach (var old in Directory.GetFiles(dir, safeOwner + "_*"))
                {
                    try { File.Delete(old); } catch { }
                }

                string fileName = $"{safeOwner}_{DateTime.Now:yyyyMMddHHmmss}{safeExt}";
                string full = Path.Combine(dir, fileName);
                File.WriteAllBytes(full, bytes);

                // Относительный путь от корня фотографий: так его поймёт ResolvePhotoPath
                string roomFolder = MakeSafeName(roomId);
                string relative = $"Rooms/{roomFolder}/photos/{fileName}";

                Debug.WriteLine($"[Фото] сохранено: {relative} ({bytes.Length} байт)");
                return relative;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Фото] не удалось сохранить: {ex.Message}");
                return "";
            }
        }

        private static string MakeSafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "player";

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            }
            return sb.Length == 0 ? "player" : sb.ToString();
        }
    }
}
