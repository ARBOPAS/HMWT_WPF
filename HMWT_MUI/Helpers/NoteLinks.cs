using System;
using System.Diagnostics;
using System.IO;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Открытие ссылок из заметок.
    ///
    /// В заметках встречаются две разные вещи: обычные адреса сайтов и ссылки
    /// на прикреплённые файлы (NoteAttachments/…). Первые отдаём браузеру как
    /// есть, вторые сначала превращаем в полный путь на диске — иначе Windows
    /// не поймёт, о каком файле речь.
    /// </summary>
    internal static class NoteLinks
    {
        /// <summary>Похоже ли на адрес сайта или почты (а не на файл заметки).</summary>
        public static bool IsWebUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            string value = url.Trim();
            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Полный путь к файлу заметки по ссылке из разметки.</summary>
        public static string ResolveLocal(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                if (File.Exists(url)) return url;

                var full = NotePaths.Resolve(url);
                return full != null && File.Exists(full) ? full : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Открывает ссылку средствами Windows (браузер, почта, программа файла).</summary>
        public static bool Open(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            try
            {
                if (IsWebUrl(url))
                {
                    Process.Start(url);
                    return true;
                }

                string local = ResolveLocal(url);
                if (local != null)
                {
                    Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
                    return true;
                }

                // Не сайт и не наш файл — пробуем как есть, вдруг это другая схема
                Process.Start(url);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Заметки] ссылка не открыта ({url}): {ex.Message}");
                return false;
            }
        }
    }
}
