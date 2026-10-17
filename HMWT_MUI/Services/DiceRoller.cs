using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;

namespace HMWT_MUI.Services
{
    public class DiceRoller
    {
        // Один генератор на все броски. Криптографический — чтобы последовательность
        // нельзя было предсказать, а System.Random оставлен только как запасной
        // вариант, если системный источник случайности недоступен.
        private readonly RNGCryptoServiceProvider _cryptoRandom = new RNGCryptoServiceProvider();
        private readonly Random _fallbackRandom = new Random();

        public event EventHandler<DiceRollResult> OnDiceRolled;

        /// <summary>
        /// Бросить кубики с улучшенным рандомом
        /// </summary>
        /// 

        /// <summary>
        /// Разбирает формулу броска: "9d20", "2к6+3", "1d20-2", "1d20+СИЛ", "3d8 + 2".
        /// Понимает и латинскую "d", и русскую "к"/"к". Возвращает количество, грани
        /// и модификатор (модификатор может требовать подстановки характеристики).
        /// </summary>
        public static bool TryParseFormula(string formula, out int count, out int sides, out int modifier,
            out string abilityName, out string error)
        {
            count = 1;
            sides = 20;
            modifier = 0;
            abilityName = null;
            error = null;

            if (string.IsNullOrWhiteSpace(formula))
            {
                error = "Пустая формула";
                return false;
            }

            // Нормализуем: любые буквы кубика («к», «в», «д» с русской раскладки)
            // приводим к «d», убираем пробелы и опускаем регистр
            var text = DiceFormulaParser.Normalize(formula).ToLowerInvariant();

            // Убираем ведущий "+" (например "+2")
            if (text.StartsWith("+")) text = text.Substring(1);

            if (text.Length == 0)
            {
                error = "Пустая формула";
                return false;
            }

            // Отделяем хвост-модификатор: всё после первого "d" и до первого +/- или буквы
            int dIndex = text.IndexOf('d');
            if (dIndex < 0)
            {
                error = "Нет кубика: укажите, например, 2d6";
                return false;
            }

            // Число кубиков (может отсутствовать — тогда 1)
            var countPart = text.Substring(0, dIndex);
            if (countPart.Length > 0)
            {
                if (!int.TryParse(countPart, out count) || count < 1)
                {
                    error = "Не понял количество кубиков";
                    return false;
                }
                if (count > 999) count = 999;
            }
            else count = 1;

            // Грани и модификатор после "d"
            var rest = text.Substring(dIndex + 1);
            int cut = rest.Length;
            for (int i = 1; i < rest.Length; i++)
            {
                if (rest[i] == '+' || rest[i] == '-')
                {
                    cut = i;
                    break;
                }
            }

            var sidesPart = rest.Substring(0, cut);
            var modPart = rest.Substring(cut);

            if (!int.TryParse(sidesPart, out sides) || sides < 2)
            {
                error = "Не понял гранность кубика";
                return false;
            }
            if (sides > 1000) sides = 1000;

            if (modPart.Length > 0)
            {
                int sign = modPart[0] == '-' ? -1 : 1;
                var valuePart = modPart.Substring(1).Trim();

                if (valuePart.Length == 0)
                {
                    error = "Модификатор не указан";
                    return false;
                }

                if (int.TryParse(valuePart, out int flat))
                {
                    modifier = sign * flat;
                }
                else
                {
                    // Модификатор-характеристика: СИЛ, ЛОВ, ТЕЛ, ИНТ, МДР, ХАР (можно по-русски)
                    abilityName = valuePart;
                    modifier = sign; // знак запоминаем, значение подставит вызывающий
                }
            }

            return true;
        }

        /// <summary>
        /// Бросок по свободной формуле: "9d20", "2к6+3", "1d20-1".
        /// abilityResolver нужен, только если в формуле есть характеристика (1d20+СИЛ).
        /// </summary>
        public DiceRollResult RollFormula(string formula, string rollType, Func<string, int?> abilityResolver)
        {
            if (TryParseFormula(formula, out int count, out int sides, out int mod, out string ability, out string error))
            {
                string suffix = "";
                if (!string.IsNullOrEmpty(ability))
                {
                    int? value = abilityResolver?.Invoke(ability);
                    if (value.HasValue)
                    {
                        // sign уже зашит в mod как ±1
                        mod = mod * value.Value;
                        suffix = $" ({ability} {value.Value:+#;-#;0})";
                    }
                    else
                    {
                        mod = 0;
                        suffix = $" (не найдена характеристика «{ability}» — бросок без модификатора)";
                    }
                }

                var result = RollDice(DiceTypeFor(sides), count, mod, rollType);
                if (suffix.Length > 0) result.Description += suffix;
                return result;
            }

            var fallback = RollDice(DiceType.D20, 1, 0, rollType);
            fallback.Description += $" (формула «{formula}» не разобрана: {error})";
            return fallback;
        }

        /// <summary>Тип кубика по числу граней (для нестандартных — ближайший из известных).</summary>
        private static DiceType DiceTypeFor(int sides)
        {
            return Enum.IsDefined(typeof(DiceType), sides) ? (DiceType)sides : DiceType.D20;
        }

        /// <summary>Бросок по формуле "XdY+Z" (зелья, урон оружия).</summary>
        public DiceRollResult RollFormula(string formula, string rollType)
        {
            int count = 1, sides = 6, mod = 0;
            try
            {
                var plus = formula.Split('+');
                if (plus.Length > 1) mod = int.Parse(plus[1]);
                var d = plus[0].Split('d');
                count = int.Parse(d[0]);
                sides = int.Parse(d[1]);
            }
            catch { /* фолбэк 1d6 */ }

            var type = Enum.IsDefined(typeof(DiceType), sides) ? (DiceType)sides : DiceType.D6;
            return RollDice(type, count, mod, rollType);
        }

        /// <summary>
        /// Бросок выражения из нескольких групп: «1к10+1к6», «2к6+3», «1к8+СИЛ».
        ///
        /// Обычный разбор понимает только одну группу костей, поэтому урон вида
        /// «1к10+1к6» (заклинание, поднятое уровнем выше) терял вторую кость.
        /// Здесь бросается каждая группа, а итог складывается.
        /// </summary>
        public DiceRollResult RollExpression(string formula, string rollType, Func<string, int?> abilityResolver)
        {
            if (DiceFormulaParser.TryParseExpression(formula, out var terms, out _))
            {
                var result = new DiceRollResult
                {
                    Timestamp = DateTime.Now,
                    Description = (formula ?? "").Trim(),
                    Formula = (formula ?? "").Trim(),
                    IndividualRolls = new List<int>(),
                    Dice = new List<RolledDie>(),
                    Total = 0,
                    RollType = rollType
                };

                var notes = new List<string>();

                foreach (var term in terms)
                {
                    if (term.IsDice)
                    {
                        for (int i = 0; i < term.Count; i++)
                        {
                            int roll = GetCryptoRandomRoll(term.Sides);
                            result.IndividualRolls.Add(roll);
                            result.Dice.Add(new RolledDie { Value = roll, Sides = term.Sides, Sign = term.Sign });
                            result.Total += term.Sign * roll;
                        }
                    }
                    else if (term.IsAbility)
                    {
                        int? value = abilityResolver?.Invoke(term.Ability);
                        if (value.HasValue)
                        {
                            result.Total += term.Sign * value.Value;
                            result.Modifier += term.Sign * value.Value;
                            notes.Add($"{term.Ability} {value.Value:+#;-#;0}");
                        }
                        else
                        {
                            notes.Add($"не найдена характеристика «{term.Ability}»");
                        }
                    }
                    else
                    {
                        result.Total += term.Average;   // знак уже учтён
                        result.Modifier += term.Average;
                    }
                }

                if (notes.Count > 0) result.Description += $" ({string.Join(", ", notes)})";

                Debug.WriteLine($"[DiceRoller] {rollType}: {result.Description} = {result.Total}");
                OnDiceRolled?.Invoke(this, result);
                return result;
            }

            // Выражение не разобралось — ведём себя как раньше, с понятной подписью
            var fallback = RollFormula(formula, rollType, abilityResolver);
            return fallback;
        }

        public DiceRollResult RollDice(DiceType type, int count, int modifier, string rollType, string label)
        {
            var result = RollDice(type, count, modifier, rollType);
            if (!string.IsNullOrWhiteSpace(label))
                result.Description = $"{label} ({result.Description})";
            return result;
        }

        public DiceRollResult RollDice(DiceType type, int count, int modifier, string rollType = "Custom")
        {
            var result = new DiceRollResult
            {
                Timestamp = DateTime.Now,
                Description = $"{count}d{(int)type} {(modifier >= 0 ? "+" : "")}{modifier}",
                Formula = $"{count}d{(int)type}" + (modifier == 0 ? "" : (modifier > 0 ? $"+{modifier}" : modifier.ToString())),
                Modifier = modifier,
                IndividualRolls = new List<int>(),
                Dice = new List<RolledDie>(),
                Total = modifier,
                RollType = rollType
            };

            for (int i = 0; i < count; i++)
            {
                // Все кубики бросаются одинаково честно, включая d20:
                // никаких «подкруток на криты» здесь больше нет
                int roll = GetCryptoRandomRoll((int)type);

                result.IndividualRolls.Add(roll);
                result.Dice.Add(new RolledDie { Value = roll, Sides = (int)type });
                result.Total += roll;
            }

            Debug.WriteLine($"[DiceRoller] {rollType}: {result.Description} = {result.Total}");
            OnDiceRolled?.Invoke(this, result);
            return result;
        }

        /// <summary>
        /// Честный бросок кубика: все значения равновероятны.
        ///
        /// Раньше здесь было два перекоса, из-за которых кубик «врал»:
        /// 1) остаток от знакового числа давал значения около нуля чаще остальных,
        ///    а Math.Abs к тому же мог уронить бросок на int.MinValue;
        /// 2) для d20 работала «умная» подкрутка: при нехватке критов бросок
        ///    18–20 с шансом 15 % превращался в 20, а 1–3 — в 1.
        /// Теперь ни подкруток, ни смещения: младшие значения отбрасываются
        /// (rejection sampling), поэтому каждое число выпадает одинаково часто.
        /// </summary>
        private int GetCryptoRandomRoll(int sides)
        {
            if (sides < 2) return 1;

            try
            {
                uint range = (uint)sides;
                uint limit = uint.MaxValue - (uint.MaxValue % range);
                byte[] buffer = new byte[4];

                // Ждём число из «ровного» диапазона: остаток от него равномерен
                for (int attempt = 0; attempt < 1000; attempt++)
                {
                    _cryptoRandom.GetBytes(buffer);
                    uint value = BitConverter.ToUInt32(buffer, 0);
                    if (value < limit) return (int)(value % range) + 1;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DiceRoller] системный генератор недоступен: {ex.Message}");
            }

            // Запасной путь: обычный генератор. Он тоже равномерный, просто
            // предсказуемый — лучше, чем упасть на броске.
            lock (_fallbackRandom) return _fallbackRandom.Next(1, sides + 1);
        }

        /// <summary>
        /// Проверка навыка (1d20 + bonus)
        /// </summary>
        public DiceRollResult RollSkillCheck(string skillName, int bonus)
        {
            return RollDice(DiceType.D20, 1, bonus, $"Навык: {skillName}");
        }

        /// <summary>
        /// Спасбросок (1d20 + bonus)
        /// </summary>
        public DiceRollResult RollSaveThrow(string abilityName, int bonus)
        {
            return RollDice(DiceType.D20, 1, bonus, $"Спасбросок: {abilityName}");
        }

        /// <summary>
        /// Бросок урона
        /// </summary>
        public DiceRollResult RollDamage(string damageDice, int modifier)
        {
            if (damageDice.Contains('d') && int.TryParse(damageDice.Split('d')[1], out int sides))
            {
                int count = 1;
                if (int.TryParse(damageDice.Split('d')[0], out var c))
                    count = c;
                return RollDice((DiceType)sides, count, modifier, "Урон");
            }
            return RollDice(DiceType.D6, 1, modifier, "Урон");
        }
    }

    public enum DiceType
    {
        D4 = 4,
        D6 = 6,
        D8 = 8,
        D10 = 10,
        D12 = 12,
        D20 = 20,
        D100 = 100
    }

    /// <summary>Один выпавший кубик: значение и гранность. Нужно для показа состава броска.</summary>
    public sealed class RolledDie
    {
        public int Value { get; set; }
        public int Sides { get; set; }

        /// <summary>Знак: вычитаемые кубики (редкий случай) показываем отдельно.</summary>
        public int Sign { get; set; } = 1;

        /// <summary>Выпал максимум на этой кости.</summary>
        public bool IsMax => Value == Sides;

        /// <summary>Выпал минимум.</summary>
        public bool IsMin => Value == 1;

        public override string ToString() => $"{Value} (d{Sides})";
    }

    public class DiceRollResult
    {
        public DateTime Timestamp { get; set; }
        public string Description { get; set; }

        /// <summary>Формула броска: «2к10+3». Показывается в панели результата.</summary>
        public string Formula { get; set; }

        /// <summary>Плоская добавка к броску (без кубиков).</summary>
        public int Modifier { get; set; }

        public List<int> IndividualRolls { get; set; }

        /// <summary>Кости с гранностью — из чего сложился итог.</summary>
        public List<RolledDie> Dice { get; set; } = new List<RolledDie>();

        public int Total { get; set; }
        public string RollType { get; set; }

        /// <summary>Состав броска словами: «7 + 10 + 5 + 6 = 28».</summary>
        public string Breakdown()
        {
            var parts = new List<string>();

            if (Dice != null && Dice.Count > 0)
            {
                foreach (var die in Dice)
                    parts.Add(die.Sign < 0 ? "−" + die.Value : die.Value.ToString());
            }
            else if (IndividualRolls != null)
            {
                foreach (int roll in IndividualRolls) parts.Add(roll.ToString());
            }

            // Добавку печатаем аккуратно: раньше к строке добавлялось «+3»,
            // а потом всё соединялось через « + », и получалось «5 + +3»
            string text = string.Join(" + ", parts);
            if (Modifier != 0)
            {
                string sign = Modifier > 0 ? " + " : " − ";
                text = text.Length == 0 ? Math.Abs(Modifier).ToString() : text + sign + Math.Abs(Modifier);
            }

            if (text.Length == 0) return Total.ToString();
            return text + " = " + Total;
            return string.Join(" + ", parts).Replace("+ −", "−") + " = " + Total;
        }
    }



}
