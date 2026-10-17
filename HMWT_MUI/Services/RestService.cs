using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Отдых персонажа: короткий и длинный.
    ///
    /// Длинный отдых возвращает всё сразу — здоровье, ячейки заклинаний, заряды
    /// предметов и часть костей хитов. Короткий — только то, что отмечено как
    /// восстановление на коротком отдыхе, и то, на что игрок готов потратить
    /// кости хитов: они и есть основной ресурс короткого отдыха.
    ///
    /// Ячейки заклинаний на коротком отдыхе не восстанавливаются: в правилах их
    /// возвращает только длинный (кроме договорной магии, отдельного вида ячеек
    /// в программе нет).
    /// </summary>
    public static class RestService
    {
        /// <summary>Какой отдых: короткий или длинный.</summary>
        public enum RestKind { Short, Long }

        /// <summary>Что именно изменил отдых — из этого собирается подпись в окне.</summary>
        public sealed class RestOutcome
        {
            public int HpBefore;
            public int HpAfter;
            public int HpMax;
            public int Healed => Math.Max(0, HpAfter - HpBefore);

            public int HitDiceSpent;
            public int HitDiceRecovered;
            public List<int> HitDiceRolls { get; } = new List<int>();

            public int SlotsRestored;
            public int ItemsRecharged;
            public List<string> RechargedItems { get; } = new List<string>();
        }

        /// <summary>Бросок кости: отдельный генератор, чтобы не зависеть от окна.</summary>
        private static readonly Random Rng = new Random();
        private static readonly object RngLock = new object();

        /// <summary>
        /// Бросок кости восстановления зарядов: «1к3», «2к4+1».
        /// Возвращает 0, если формулы нет или она непонятна — тогда заряды
        /// восстанавливаются до максимума, как было раньше.
        /// </summary>
        private static int RollRechargeDice(string formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return 0;
            try
            {
                if (!DiceFormulaParser.TryParseExpression(formula, out var terms, out _)) return 0;

                int total = 0;
                bool anyDice = false;
                foreach (var term in terms)
                {
                    if (term.IsDice)
                    {
                        anyDice = true;
                        for (int i = 0; i < term.Count; i++) total += term.Sign * RollDie(term.Sides);
                    }
                    else if (term.IsAbility)
                    {
                        return 0;   // характеристики в зарядах не считаем
                    }
                    else
                    {
                        total += term.Sign * term.Modifier;
                    }
                }

                return anyDice ? Math.Max(0, total) : 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestService] кость восстановления «{formula}»: {ex.Message}");
                return 0;
            }
        }
        private static int RollDie(int sides)
        {
            lock (RngLock) return Rng.Next(1, Math.Max(2, sides) + 1);
        }

        #region === ЗДОРОВЬЕ ===

        /// <summary>
        /// Максимум здоровья.
        ///
        /// Лежит в настройках, а не в колонке: так сложилось исторически, и
        /// переезжать ему не за чем — у старых персонажей колонки всё равно нет.
        /// Если настройки не задавали, максимумом считается текущее здоровье,
        /// как это делает шапка листа.
        /// </summary>
        public static int LoadMaxHp(int characterId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                using (var cmd = new SqliteCommand("SELECT Value FROM Settings WHERE Key = @key", conn))
                {
                    cmd.Parameters.AddWithValue("@key", $"Character_{characterId}_MaxHP");
                    var stored = cmd.ExecuteScalar();
                    if (stored != null && stored != DBNull.Value
                        && int.TryParse(stored.ToString(), out int max) && max > 0)
                        return max;
                }

                return ReadHp(conn, characterId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestService] максимум здоровья: {ex.Message}");
                return 0;
            }
        }

        public static int LoadCurrentHp(int characterId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                return ReadHp(conn, characterId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestService] текущее здоровье: {ex.Message}");
                return 0;
            }
        }

        private static int ReadHp(SqliteConnection conn, int characterId)
        {
            using var cmd = new SqliteCommand(
                "SELECT COALESCE(HP, 0) FROM CharacterInfo WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@id", characterId);
            var value = cmd.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        public static void SaveCurrentHp(int characterId, int hp)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                "UPDATE CharacterInfo SET HP = @hp WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@hp", Math.Max(0, hp));
            cmd.Parameters.AddWithValue("@id", characterId);
            cmd.ExecuteNonQuery();
        }

        #endregion

        #region === КОСТИ ХИТОВ ===

        /// <summary>
        /// Кость хитов персонажа: размер, сколько всего и сколько потрачено.
        ///
        /// Одного размера достаточно: программа ведёт персонажа одной строкой
        /// класса, а не набором уровней разных классов — смешивать кости разных
        /// размеров здесь негде.
        /// </summary>
        public static (int Die, int Total, int Used) LoadHitDice(int characterId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    SELECT COALESCE(HitDie, 8), COALESCE(HitDiceTotal, 0), COALESCE(HitDiceUsed, 0)
                    FROM CharacterInfo WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    int die = reader.IsDBNull(0) ? 8 : Convert.ToInt32(reader.GetValue(0));
                    int total = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                    int used = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                    return (NormalizeDie(die), Math.Max(0, total), Math.Max(0, Math.Min(used, Math.Max(0, total))));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestService] кости хитов: {ex.Message}");
            }
            return (8, 0, 0);
        }

        /// <summary>Допустимые размеры кости: d6, d8, d10, d12 — как в книге.</summary>
        public static readonly int[] HitDieSizes = { 6, 8, 10, 12 };

        public static int NormalizeDie(int die)
        {
            if (die <= 6) return 6;
            if (die <= 8) return 8;
            if (die <= 10) return 10;
            return 12;
        }

        /// <summary>
        /// Записывает настройку костей. Потраченных костей не может быть больше
        /// всего запаса: иначе после уменьшения запаса осталось бы «потрачено 5
        /// из 3», и отдых врал бы про доступное.
        /// </summary>
        public static void SaveHitDiceSettings(int characterId, int die, int total)
        {
            total = Math.Max(0, Math.Min(total, 99));
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(@"
                UPDATE CharacterInfo
                SET HitDie = @die,
                    HitDiceTotal = @total,
                    HitDiceUsed = MIN(COALESCE(HitDiceUsed, 0), @total)
                WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@die", NormalizeDie(die));
            cmd.Parameters.AddWithValue("@total", total);
            cmd.Parameters.AddWithValue("@id", characterId);
            cmd.ExecuteNonQuery();
        }

        public static void SaveHitDiceUsed(int characterId, int used)
        {
            using var conn = DatabaseHelper.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(
                "UPDATE CharacterInfo SET HitDiceUsed = @used WHERE id_character = @id", conn);
            cmd.Parameters.AddWithValue("@used", Math.Max(0, used));
            cmd.Parameters.AddWithValue("@id", characterId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Модификатор характеристики: половина разницы с 10, округление вниз.</summary>
        public static int AbilityModifier(int score) => (int)Math.Floor((score - 10) / 2.0);

        /// <summary>Модификатор ТЕЛОСЛОЖЕНИЯ: от него зависит лечение костями хитов.</summary>
        public static int ConstitutionModifier(int characterId)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT COALESCE(Constitution, 10) FROM CharacterInfo WHERE id_character = @id", conn);
                cmd.Parameters.AddWithValue("@id", characterId);
                var value = cmd.ExecuteScalar();
                return AbilityModifier(value == null || value == DBNull.Value ? 10 : Convert.ToInt32(value));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestService] телосложение: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region === СКОЛЬКО ВЕРНЁТ ОТДЫХ ===

        /// <summary>Сколько ячеек заклинаний сейчас потрачено.</summary>
        public static int CountSpentSlots(int characterId)
        {
            try
            {
                return SpellService.LoadSlotsDetails(characterId).Where(s => s.Level > 0).Sum(s => s.Used);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestService] потраченные ячейки: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Сколько предметов отдых вернёт в полный заряд.
        ///
        /// Только считает и ничего не меняет: этим числом окно отдыха показывает,
        /// что будет, ДО нажатия. Раньше подсчёт заодно и заряжал предметы, и
        /// после этого отдых уже ничего не восстанавливал.
        /// </summary>
        public static int CountItemsToRecharge(int characterId, string rest)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                return CountIncompleteItems(conn, characterId, RechargeFilter(rest));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestService] подсчёт зарядов: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Какие предметы заряжаются на этом отдыхе. Короткий — только отмеченное
        /// как «на коротком отдыхе»; длинный — всё, что восстанавливается: то, что
        /// заряжается на коротком, к этому моменту и так полное.
        /// </summary>
        private static string RechargeFilter(string rest)
            => rest == "short"
                ? "recharge = 'short'"
                : "COALESCE(recharge, 'long') <> 'none'";

        private static int RechargeItems(int characterId, string rest, List<string> recharged)
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                string filter = RechargeFilter(rest);

                // Берём неполные предметы вместе с костью восстановления: часть
                // предметов возвращает не всё сразу, а сколько выпадет на кости
                var items = new List<(int Id, string Name, int Charges, int Max, string Dice)>();
                using (var select = new SqliteCommand($@"
                    SELECT id_item, name, COALESCE(charges, 0) AS charges,
                           COALESCE(max_charges, 0) AS max_charges,
                           COALESCE(recharge_dice, '') AS recharge_dice
                    FROM Inventory
                    WHERE id_character = @id AND COALESCE(max_charges, 0) > 0
                      AND COALESCE(charges, 0) < max_charges AND {filter}
                    ORDER BY name", conn))
                {
                    select.Parameters.AddWithValue("@id", characterId);
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        items.Add((Convert.ToInt32(reader["id_item"]),
                                   reader["name"]?.ToString() ?? "",
                                   Convert.ToInt32(reader["charges"]),
                                   Convert.ToInt32(reader["max_charges"]),
                                   reader["recharge_dice"]?.ToString() ?? ""));
                    }
                }

                foreach (var item in items)
                {
                    int rolled = RollRechargeDice(item.Dice);
                    int charges = rolled > 0 ? Math.Min(item.Max, item.Charges + rolled) : item.Max;

                    using (var update = new SqliteCommand("UPDATE Inventory SET charges = @ch WHERE id_item = @id", conn))
                    {
                        update.Parameters.AddWithValue("@ch", charges);
                        update.Parameters.AddWithValue("@id", item.Id);
                        update.ExecuteNonQuery();
                    }

                    // В подписи видно и бросок: «Кольцо — 1к3: +2 (3/3)»
                    recharged.Add(rolled > 0
                        ? $"{item.Name} — {item.Dice.Trim()}: +{rolled} ({charges}/{item.Max})"
                        : item.Name);
                }

                return recharged.Count;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestService] заряды предметов: {ex.Message}");
                return 0;
            }
        }

        private static int CountIncompleteItems(SqliteConnection conn, int characterId, string filter)
        {
            using var cmd = new SqliteCommand($@"
                SELECT COUNT(*) FROM Inventory
                WHERE id_character = @id AND COALESCE(max_charges, 0) > 0
                  AND COALESCE(charges, 0) < max_charges AND {filter}", conn);
            cmd.Parameters.AddWithValue("@id", characterId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        #endregion

        #region === КОРОТКИЙ ОТДЫХ ===

        /// <summary>
        /// Короткий отдых: игрок тратит кости хитов и получает лечение.
        ///
        /// Каждая кость лечит на бросок плюс модификатор ТЕЛОСЛОЖЕНИЯ, но не
        /// меньше одного хита: при отрицательном модификаторе отдых иначе отнимал
        /// бы здоровье, а это уже не отдых.
        /// </summary>
        public static RestOutcome ShortRest(int characterId, int diceToSpend)
        {
            var outcome = new RestOutcome();
            var (die, total, used) = LoadHitDice(characterId);

            int available = Math.Max(0, total - used);
            int spend = Math.Max(0, Math.Min(diceToSpend, available));

            outcome.HpBefore = LoadCurrentHp(characterId);
            outcome.HpMax = LoadMaxHp(characterId);
            outcome.HitDiceSpent = spend;

            int constitution = ConstitutionModifier(characterId);
            int healed = 0;
            for (int i = 0; i < spend; i++)
            {
                int roll = RollDie(die);
                outcome.HitDiceRolls.Add(roll);
                healed += Math.Max(1, roll + constitution);
            }

            // Лечение не может уменьшить здоровье, даже если максимум стал ниже
            // текущего: отдых — не урон
            outcome.HpAfter = Math.Max(outcome.HpBefore, Math.Min(outcome.HpMax, outcome.HpBefore + healed));
            if (outcome.HpAfter != outcome.HpBefore) SaveCurrentHp(characterId, outcome.HpAfter);
            if (spend > 0) SaveHitDiceUsed(characterId, used + spend);

            outcome.ItemsRecharged = RechargeItems(characterId, "short", outcome.RechargedItems);
            return outcome;
        }

        #endregion

        #region === ДЛИННЫЙ ОТДЫХ ===

        /// <summary>
        /// Длинный отдых: полное здоровье, все ячейки заклинаний, заряды
        /// предметов и половина запаса костей хитов.
        ///
        /// Всё меняется в одной транзакции: если что-то посередине сорвётся,
        /// персонаж остался бы с полным здоровьем и пустыми ячейками — непонятно,
        /// отдохнул он или нет.
        /// </summary>
        public static RestOutcome LongRest(int characterId)
        {
            var outcome = new RestOutcome();
            outcome.HpBefore = LoadCurrentHp(characterId);
            outcome.HpMax = LoadMaxHp(characterId);
            outcome.HpAfter = Math.Max(outcome.HpBefore, outcome.HpMax);

            var (_, total, used) = LoadHitDice(characterId);

            // В книге длинный отдых возвращает половину запаса костей, но не
            // меньше одной — иначе персонажу с одной костью отдых не давал бы ничего
            int recoverLimit = total > 0 ? Math.Max(1, total / 2) : 0;
            int recovered = Math.Min(used, recoverLimit);

            DatabaseHelper.RunInTransaction((conn, tx) =>
            {
                using (var hp = new SqliteCommand(
                    "UPDATE CharacterInfo SET HP = @hp WHERE id_character = @id", conn, tx))
                {
                    hp.Parameters.AddWithValue("@hp", outcome.HpAfter);
                    hp.Parameters.AddWithValue("@id", characterId);
                    hp.ExecuteNonQuery();
                }

                // Ячейки: сначала считаем, сколько было потрачено — это число
                // нужно для подписи, после обнуления его уже не узнать
                using (var count = new SqliteCommand(
                    "SELECT COALESCE(SUM(used_slots), 0) FROM CharacterSpellSlots WHERE id_character = @id AND slot_level > 0",
                    conn, tx))
                {
                    count.Parameters.AddWithValue("@id", characterId);
                    var value = count.ExecuteScalar();
                    outcome.SlotsRestored = value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
                }

                using (var slots = new SqliteCommand(
                    "UPDATE CharacterSpellSlots SET used_slots = 0 WHERE id_character = @id", conn, tx))
                {
                    slots.Parameters.AddWithValue("@id", characterId);
                    slots.ExecuteNonQuery();
                }

                if (recovered > 0)
                {
                    using var dice = new SqliteCommand(
                        "UPDATE CharacterInfo SET HitDiceUsed = @used WHERE id_character = @id", conn, tx);
                    dice.Parameters.AddWithValue("@used", used - recovered);
                    dice.Parameters.AddWithValue("@id", characterId);
                    dice.ExecuteNonQuery();
                }
            });

            outcome.HitDiceRecovered = recovered;
            outcome.ItemsRecharged = RechargeItems(characterId, "long", outcome.RechargedItems);
            return outcome;
        }

        #endregion
    }
}
