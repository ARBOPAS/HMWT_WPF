using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using HMWT_MUI.Helpers;
using HMWT_MUI.Net;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Профиль ведущего.
    ///
    /// У ведущего может не быть своего персонажа — он его и не должен показывать.
    /// Но в отряде ему нужно имя и аватар, поэтому профиль хранится отдельно
    /// в настройках и в папке фотографий.
    /// </summary>
    public static class DmProfileService
    {
        private const string NameKey = "DmProfile_Name";
        private const string ClassKey = "DmProfile_Class";
        private const string PhotoKey = "DmProfile_Photo";

        /// <summary>Задан ли профиль ведущего хотя бы частично.</summary>
        public static bool HasProfile
            => !string.IsNullOrWhiteSpace(DisplayName) || !string.IsNullOrWhiteSpace(PhotoPath);

        public static string DisplayName => SafeGet(NameKey);
        public static string Subtitle => SafeGet(ClassKey);
        public static string PhotoPath => SafeGet(PhotoKey);

        /// <summary>Сохраняет профиль ведущего.</summary>
        public static void Save(string displayName, string subtitle)
        {
            try
            {
                DatabaseHelper.SaveSetting(NameKey, (displayName ?? "").Trim());
                DatabaseHelper.SaveSetting(ClassKey, (subtitle ?? "").Trim());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmProfile] Save: {ex.Message}");
            }
        }

        /// <summary>
        /// Копирует выбранный файл в папку фотографий и запоминает путь.
        /// Прежний файл удаляем, чтобы не копить мусор.
        /// </summary>
        public static string SavePhoto(string sourceFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                    return "";

                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "DmProfile");
                Directory.CreateDirectory(dir);

                string extension = Path.GetExtension(sourceFilePath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                // Убираем прежние файлы: расширение могло измениться
                foreach (var old in Directory.GetFiles(dir, "dm_avatar_*"))
                {
                    try { File.Delete(old); } catch { }
                }

                string fileName = $"dm_avatar_{DateTime.Now:yyyyMMddHHmmss}{extension.ToLowerInvariant()}";
                File.Copy(sourceFilePath, Path.Combine(dir, fileName), true);

                string relative = $"DmProfile/{fileName}";
                DatabaseHelper.SaveSetting(PhotoKey, relative);
                return relative;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmProfile] SavePhoto: {ex.Message}");
                return "";
            }
        }

        /// <summary>Убирает аватар ведущего вместе с файлом.</summary>
        public static void ClearPhoto()
        {
            try
            {
                string relative = PhotoPath;
                if (!string.IsNullOrEmpty(relative))
                {
                    string full = PhotoHelper.ResolvePhotoPath(relative);
                    if (!string.IsNullOrEmpty(full) && File.Exists(full))
                    {
                        try { File.Delete(full); } catch { }
                    }
                }
                DatabaseHelper.SaveSetting(PhotoKey, "");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmProfile] ClearPhoto: {ex.Message}");
            }
        }

        private static string SafeGet(string key)
        {
            try { return DatabaseHelper.GetSetting(key, ""); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmProfile] чтение {key}: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Готовит данные участника для комнаты.
        ///
        /// Если профиль ведущего задан, он имеет приоритет: ведущий может играть
        /// без своего персонажа и всё равно показываться в отряде с именем и аватаром.
        /// </summary>
        public static PlayerInfo BuildPlayer(string fallbackName)
        {
            string photo = PhotoPath;
            string name = DisplayName;

            if (!string.IsNullOrWhiteSpace(photo) || !string.IsNullOrWhiteSpace(name))
            {
                // Настройки могли остаться от прежней версии: приложение подскажёт
                // пользователю, если файл аватара пропал
                if (!string.IsNullOrWhiteSpace(photo))
                    Debug.WriteLine($"[DmProfile] аватар: {photo}");

                return new PlayerInfo
                {
                    Name = string.IsNullOrWhiteSpace(name) ? fallbackName : name,
                    CharacterName = name,
                    CharacterClass = Subtitle,
                    AvatarPath = photo,
                    Hp = 0,
                    MaxHp = 0
                };
            }

            return new PlayerInfo { Name = fallbackName };
        }
    }
}
