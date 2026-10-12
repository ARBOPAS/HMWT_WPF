using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HMWT_MUI.Services
{
    /// <summary>Предыстория персонажа по книге правил.</summary>
    public class BackgroundData
    {
        public int Id { get; set; } = 0;
        public string Name { get; set; } = "";

        /// <summary>Владение навыками: два навыка через запятую.</summary>
        public string SkillProficiencies { get; set; } = "";

        /// <summary>Владение инструментами.</summary>
        public string ToolProficiencies { get; set; } = "";

        /// <summary>Языки, если предыстория их даёт.</summary>
        public string Languages { get; set; } = "";

        /// <summary>Снаряжение, которое выдаётся вместе с предысторией.</summary>
        public string Equipment { get; set; } = "";

        /// <summary>Умение предыстории: как называется и что даёт.</summary>
        public string Feature { get; set; } = "";

        public string Description { get; set; } = "";

        /// <summary>Создана игроком, а не взята из встроенного списка.</summary>
        public bool IsCustom { get; set; }

        /// <summary>Строка для списка: название и короткая расшифровка.</summary>
        public string DisplayLine
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(SkillProficiencies)) parts.Add(SkillProficiencies);

                return parts.Count == 0 ? Name : $"{Name} — {string.Join("; ", parts)}";
            }
        }
    }

    /// <summary>
    /// Справочник предысторий.
    ///
    /// Пользователь просил: игрок либо берёт предысторию из списка, либо создаёт свою.
    /// Встроенные предыстории засеиваются один раз при первом обращении, дальше
    /// их можно править и дополнять своими.
    /// </summary>
    public static class BackgroundService
    {
        /// <summary>Одиннадцать предысторий из книги правил.</summary>
        private static readonly (string Name, string Skills, string Tools, string Equipment, string Feature)[] Standard =
        {
            ("Прислужник", "Проницательность, Религия", "—",
             "Священный символ, молитвенник, 5 палочек благовоний, облачение, обычная одежда, 15 зм",
             "Вера: вас принимают в храмах вашего божества"),

            ("Шарлатан", "Обман, Ловкость рук", "Набор для подделок, набор для игры",
             "Хорошая одежда, набор для подделок, 15 зм",
             "Ложная личность: у вас есть второе имя и документы"),

            ("Преступник", "Обман, Скрытность", "Один набор для игры, воровские инструменты",
             "Лом, тёмная обычная одежда с капюшоном, 15 зм",
             "Криминальные связи: контакт в преступном мире"),

            ("Артист", "Акробатика, Выступление", "Набор для грима, один музыкальный инструмент",
             "Музыкальный инструмент, подарок от поклонника, костюм, 15 зм",
             "Популярность: вас узнают и рады приютить"),

            ("Народный герой", "Выживание, Уход за животными", "Один набор ремесленника, транспорт",
             "Набор ремесленника, лопата, котелок, обычная одежда, 10 зм",
             "Судьба народа: простые люди вас укрывают"),

            ("Ремесленник гильдии", "Проницательность, Убеждение", "Один набор ремесленника",
             "Набор ремесленника, письмо от гильдии, дорожная одежда, 15 зм",
             "Членство в гильдии: поддержка и жильё у гильдии"),

            ("Отшельник", "Медицина, Религия", "Набор травника",
             "Футляр для свитков с заметками, зимнее одеяло, обычная одежда, набор травника, 5 зм",
             "Открытие: вам открылась уникальная тайна"),

            ("Благородный", "История, Убеждение", "Один набор для игры",
             "Хорошая одежда, перстень с печаткой, свиток родословной, 25 зм",
             "Привилегия: к вам относятся с уважением"),

            ("Чужеземец", "Атлетика, Выживание", "Один музыкальный инструмент",
             "Посох, охотничий капкан, трофей с животного, дорожная одежда, 10 зм",
             "Странник: вы всегда помните карту местности"),

            ("Мудрец", "История, Магия", "Два языка на выбор",
             "Чернила и перо, нож для бумаг, письмо коллеги, обычная одежда, 10 зм",
             "Исследователь: вы знаете, где искать нужные сведения"),

            ("Моряк", "Атлетика, Восприятие", "Навигационные инструменты, транспорт (водный)",
             "Кофель-нагель, 50 футов шёлковой верёвки, талисман, дорожная одежда, 10 зм",
             "Проход на судно: вас берут на корабль бесплатно")
        };

        /// <summary>Все предыстории: сначала встроенные, потом свои, по алфавиту.</summary>
        public static List<BackgroundData> LoadAll()
        {
            EnsureSeeded();

            var list = new List<BackgroundData>();
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT id_background, name, skill_proficiencies, tool_proficiencies, languages,
                           equipment, feature, description, is_custom
                    FROM BackgroundReference ORDER BY name", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(Read(r));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundService] LoadAll: {ex.Message}");
            }
            return list;
        }

        /// <summary>Ищет предысторию по названию.</summary>
        public static BackgroundData Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT id_background, name, skill_proficiencies, tool_proficiencies, languages,
                           equipment, feature, description, is_custom
                    FROM BackgroundReference WHERE lower(name) = lower(@n) LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@n", name.Trim());
                using var r = cmd.ExecuteReader();
                return r.Read() ? Read(r) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundService] Find: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Сохраняет предысторию. Свои можно и создавать, и править; встроенные
        /// тоже правятся — если игрок так решил, это его игра.
        /// </summary>
        public static int Save(BackgroundData data, bool markCustom = true)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Name)) return 0;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    INSERT INTO BackgroundReference
                        (name, skill_proficiencies, tool_proficiencies, languages, equipment, feature, description, is_custom)
                    VALUES (@n, @sk, @tl, @lg, @eq, @ft, @ds, @cu)
                    ON CONFLICT(name) DO UPDATE SET
                        skill_proficiencies = excluded.skill_proficiencies,
                        tool_proficiencies  = excluded.tool_proficiencies,
                        languages           = excluded.languages,
                        equipment           = excluded.equipment,
                        feature             = excluded.feature,
                        description         = excluded.description,
                        is_custom           = MAX(BackgroundReference.is_custom, excluded.is_custom),
                        updated_at          = CURRENT_TIMESTAMP", conn);

                cmd.Parameters.AddWithValue("@n", data.Name.Trim());
                cmd.Parameters.AddWithValue("@sk", data.SkillProficiencies ?? "");
                cmd.Parameters.AddWithValue("@tl", data.ToolProficiencies ?? "");
                cmd.Parameters.AddWithValue("@lg", data.Languages ?? "");
                cmd.Parameters.AddWithValue("@eq", data.Equipment ?? "");
                cmd.Parameters.AddWithValue("@ft", data.Feature ?? "");
                cmd.Parameters.AddWithValue("@ds", data.Description ?? "");
                cmd.Parameters.AddWithValue("@cu", markCustom ? 1 : 0);
                cmd.ExecuteNonQuery();

                var found = Find(data.Name);
                return found?.Id ?? 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundService] Save: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Удаляет предысторию. Персонажам, у которых она выбрана, оставляем
        /// прежний текст — иначе у них пропали бы записи.
        /// </summary>
        public static bool Delete(int id)
        {
            if (id <= 0) return false;
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var unlink = new SqliteCommand(
                    "UPDATE CharacterIdAddInfo SET id_background = NULL WHERE id_background = @id", conn))
                {
                    unlink.Parameters.AddWithValue("@id", id);
                    unlink.ExecuteNonQuery();
                }

                using var del = new SqliteCommand("DELETE FROM BackgroundReference WHERE id_background = @id", conn);
                del.Parameters.AddWithValue("@id", id);
                return del.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundService] Delete: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Проставляет персонажу выбранную предысторию и, если он согласен,
        /// отмечает владение навыками из неё.
        /// </summary>
        public static void ApplyToCharacter(int characterId, BackgroundData background, bool applySkills)
        {
            if (background == null) return;

            SaveCharacterLink(characterId, background);

            if (!applySkills) return;

            foreach (var skill in SplitSkills(background.SkillProficiencies))
            {
                try
                {
                    if (!AbilityService.ReadSkillProficiency(characterId, skill))
                        AbilityService.ToggleSkillProficiency(characterId, skill);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackgroundService] навык «{skill}»: {ex.Message}");
                }
            }
        }

        /// <summary>Записывает название и ссылку на предысторию в карточку персонажа.</summary>
        public static void SaveCharacterLink(int characterId, BackgroundData background)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var ensure = new SqliteCommand(
                    "INSERT INTO CharacterIdAddInfo (id_character) SELECT @id " +
                    "WHERE NOT EXISTS (SELECT 1 FROM CharacterIdAddInfo WHERE id_character = @id)", conn))
                {
                    ensure.Parameters.AddWithValue("@id", characterId);
                    ensure.ExecuteNonQuery();
                }

                using var cmd = new SqliteCommand(@"
                    UPDATE CharacterIdAddInfo
                    SET Background = @name, id_background = @bg
                    WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@name", background.Name ?? "");
                cmd.Parameters.AddWithValue("@bg", background.Id > 0 ? (object)background.Id : DBNull.Value);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundService] SaveCharacterLink: {ex.Message}");
            }
        }

        /// <summary>Разбирает строку владений навыками на отдельные названия.</summary>
        public static List<string> SplitSkills(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new List<string>();

            return value
                .Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && s != "—" && s != "-")
                .ToList();
        }

        private static BackgroundData Read(SqliteDataReader r)
        {
            return new BackgroundData
            {
                Id = Convert.ToInt32(r["id_background"]),
                Name = r["name"]?.ToString() ?? "",
                SkillProficiencies = r["skill_proficiencies"]?.ToString() ?? "",
                ToolProficiencies = r["tool_proficiencies"]?.ToString() ?? "",
                Languages = r["languages"]?.ToString() ?? "",
                Equipment = r["equipment"]?.ToString() ?? "",
                Feature = r["feature"]?.ToString() ?? "",
                Description = r["description"]?.ToString() ?? "",
                IsCustom = Convert.ToInt32(r["is_custom"] ?? 0) == 1
            };
        }

        private static bool _seeded;

        /// <summary>
        /// Засеивает встроенные предыстории один раз. Если игрок их правил или
        /// удалял, повторно не навязываем: отметку храним в настройках.
        ///
        /// Вызывается и при старте приложения, чтобы список был готов заранее.
        /// </summary>
        public static void EnsureSeeded()
        {
            // Не заходим повторно: метод может вызываться и при инициализации базы,
            // и при открытии списка
            if (_seeded) return;

            try
            {
                if (DatabaseHelper.GetSetting("BackgroundsSeeded", "") == "1")
                {
                    _seeded = true;
                    return;
                }

                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    foreach (var (name, skills, tools, equipment, feature) in Standard)
                    {
                        using var cmd = new SqliteCommand(@"
                            INSERT OR IGNORE INTO BackgroundReference
                                (name, skill_proficiencies, tool_proficiencies, equipment, feature, is_custom)
                            VALUES (@n, @sk, @tl, @eq, @ft, 0)", conn);
                        cmd.Parameters.AddWithValue("@n", name);
                        cmd.Parameters.AddWithValue("@sk", skills);
                        cmd.Parameters.AddWithValue("@tl", tools);
                        cmd.Parameters.AddWithValue("@eq", equipment);
                        cmd.Parameters.AddWithValue("@ft", feature);
                        cmd.ExecuteNonQuery();
                    }
                }

                DatabaseHelper.SaveSetting("BackgroundsSeeded", "1");
                _seeded = true;
                Debug.WriteLine($"[BackgroundService] встроенные предыстории добавлены: {Standard.Length}");
            }
            catch (Exception ex)
            {
                // Флаг не выставлен: попробуем при следующем обращении,
                // когда база уже будет готова
                Debug.WriteLine($"[BackgroundService] EnsureSeeded: {ex.Message}");
            }
        }
    }
}
