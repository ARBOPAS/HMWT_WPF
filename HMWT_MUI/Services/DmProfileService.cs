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
        /// <summary>
        /// Участник отряда от этого приложения: имя — ИМЯ ПЕРСОНАЖА, а не профиль
        /// и не учётная запись Windows. Своё имя подставляется только если его
        /// включили в настройках; без персонажа показывается «Игрок».
        /// </summary>
        public static PlayerInfo BuildPartyPlayer(int characterId = 0)
        {
            string characterName = "";
            int foundId = 0;
            int hp = 0, armor = 0, level = 1;
            string charClass = "", race = "", avatar = "";

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                // Конкретный персонаж важнее «последнего открытого»: два окна на
                // одном компьютере иначе берут одного и того же персонажа.
                int id = characterId > 0 ? characterId : LastCharacterId(conn);
                if (id <= 0) return new PlayerInfo { Name = "Игрок" };

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name, class, race, HP, ArmorClass, PhotoPath FROM CharacterInfo WHERE id_character = @id";
                cmd.Parameters.AddWithValue("@id", id);

                using var r = cmd.ExecuteReader();
                if (!r.Read()) return new PlayerInfo { Name = "Игрок" };

                foundId = id;
                characterName = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                charClass = r.IsDBNull(1) ? "" : r.GetString(1);
                race = r.IsDBNull(2) ? "" : r.GetString(2);
                hp = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                armor = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                avatar = r.IsDBNull(5) ? "" : r.GetString(5);

                r.Close();
                using var levelCmd = conn.CreateCommand();
                levelCmd.CommandText = "SELECT Level FROM LevelTable WHERE id_character = @id LIMIT 1";
                levelCmd.Parameters.AddWithValue("@id", foundId);
                var levelValue = levelCmd.ExecuteScalar();
                if (levelValue != null && levelValue != DBNull.Value) level = Convert.ToInt32(levelValue);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmProfile] участник отряда: {ex.Message}");
                try
                {
                    string dir = PhotoHelper.GetWritableRoot();
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "party.log"),
                        DateTime.Now.ToString("HH:mm:ss") + " участник отряда: " + ex + "\r\n");
                }
                catch { }
            }

            // Максимум HP хранится не в CharacterInfo, а в настройках персонажа:
            // оттуда же его берёт шапка листа. Без этого в отряд уходило
            // «185/185» вместо «185/210».
            int maxHp = 0;
            try
            {
                if (foundId > 0)
                {
                    maxHp = DatabaseHelper.GetCharacterMaxHp(foundId);
                    if (maxHp <= 0) maxHp = hp;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmProfile] максимум здоровья: {ex.Message}");
                maxHp = hp;
            }

            try
            {
                string dir = PhotoHelper.GetWritableRoot();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "room-log.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} участник: персонаж {characterId} «{characterName}», " +
                    $"хиты {hp}/{maxHp}, класс «{charClass}», аватар «{avatar}», в отряде «{characterName}»\r\n");
            }
            catch { }

            if (string.IsNullOrWhiteSpace(characterName)) return new PlayerInfo { Name = "Игрок", CharacterId = foundId };

            // В отряде у всех одно имя — имя персонажа. Личного профиля для
            // отряда больше нет: броски и трекер и так показывают персонажа.
            return new PlayerInfo
            {
                CharacterId = foundId,
                CharacterName = characterName,
                CharacterClass = charClass,
                CharacterRace = race,
                Hp = hp,
                MaxHp = maxHp,
                ArmorClass = armor,
                Level = level,
                AvatarPath = avatar,
                Name = characterName
            };
        }

        /// <summary>Персонаж, которого открывали последним, иначе первый по списку.</summary>
        private static int LastCharacterId(Microsoft.Data.Sqlite.SqliteConnection conn)
        {
            try
            {
                using var last = conn.CreateCommand();
                last.CommandText = "SELECT Value FROM Settings WHERE Key = 'LastCharacterId' LIMIT 1";
                var stored = last.ExecuteScalar();
                if (stored != null && stored != DBNull.Value &&
                    int.TryParse(Convert.ToString(stored), out int lastId) && lastId > 0)
                {
                    using var check = conn.CreateCommand();
                    check.CommandText = "SELECT COUNT(*) FROM CharacterInfo WHERE id_character = @id";
                    check.Parameters.AddWithValue("@id", lastId);
                    if (Convert.ToInt32(check.ExecuteScalar()) > 0) return lastId;
                }

                using var first = conn.CreateCommand();
                first.CommandText = "SELECT id_character FROM CharacterInfo ORDER BY id_character LIMIT 1";
                var value = first.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
            catch { return 0; }
        }
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
