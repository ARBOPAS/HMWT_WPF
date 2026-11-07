using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Файлы чата на стороне хоста.
    ///
    /// Файл не рассылается всем вместе с сообщением: в ленту уходят только имя,
    /// размер и id. Байты лежат в папке комнаты и отдаются по запросу — когда
    /// получатель нажал «Сохранить…» и подтвердил размер. Так гигабайтный xlsx
    /// не приезжает на компьютер каждому, кто открыл комнату.
    /// </summary>
    public static class ChatFileStore
    {
        /// <summary>Папка файлов чата внутри папки комнаты.</summary>
        public static string Folder(string roomId)
        {
            string safeRoom = string.IsNullOrWhiteSpace(roomId) ? "common" : roomId.Trim();
            return Path.Combine(PhotoHelper.GetWritableRoot(), "Rooms", safeRoom, "chat-files");
        }

        /// <summary>Кладёт файл в хранилище комнаты. Возвращает id или "" при ошибке.</summary>
        public static string Save(string roomId, string fileName, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";

            try
            {
                string folder = Folder(roomId);
                Directory.CreateDirectory(folder);

                string id = Guid.NewGuid().ToString("N");
                string safeName = Sanitize(fileName);
                string target = Path.Combine(folder, id + "_" + safeName);

                File.WriteAllBytes(target, bytes);
                return id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatFileStore] Save: {ex.Message}");
                return "";
            }
        }

        /// <summary>Полный путь к файлу по id (null — файла нет).</summary>
        public static string Resolve(string roomId, string fileId)
        {
            if (string.IsNullOrWhiteSpace(fileId)) return null;

            try
            {
                string folder = Folder(roomId);
                if (!Directory.Exists(folder)) return null;

                string match = Directory.EnumerateFiles(folder, fileId + "_*").FirstOrDefault();
                return match != null && File.Exists(match) ? match : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatFileStore] Resolve: {ex.Message}");
                return null;
            }
        }

        /// <summary>Имя файла, которое показал отправитель.</summary>
        public static string DisplayName(string roomId, string fileId)
        {
            string full = Resolve(roomId, fileId);
            if (full == null) return "";

            string name = Path.GetFileName(full);
            int underscore = name.IndexOf('_');
            return underscore >= 0 && underscore + 1 < name.Length
                ? name.Substring(underscore + 1)
                : name;
        }

        public static long Size(string roomId, string fileId)
        {
            try
            {
                string full = Resolve(roomId, fileId);
                return full == null ? 0 : new FileInfo(full).Length;
            }
            catch { return 0; }
        }

        public static void Delete(string roomId, string fileId)
        {
            try
            {
                string full = Resolve(roomId, fileId);
                if (full != null && File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatFileStore] Delete: {ex.Message}");
            }
        }

        /// <summary>Список файлов комнаты: пригодится очистке кэша и отчёту.</summary>
        public static List<string> List(string roomId)
        {
            try
            {
                string folder = Folder(roomId);
                return Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder).ToList()
                    : new List<string>();
            }
            catch { return new List<string>(); }
        }

        /// <summary>
        /// Убирает из имени всё, что нельзя писать в файл: имя приходит от
        /// пользователя и попадает в путь.
        /// </summary>
        public static string Sanitize(string fileName)
        {
            string name = string.IsNullOrWhiteSpace(fileName) ? "файл" : Path.GetFileName(fileName);
            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');

            name = name.Trim().Trim('.');
            if (name.Length == 0) name = "файл";
            if (name.Length > 120) name = name.Substring(name.Length - 120);
            return name;
        }
    }
}
