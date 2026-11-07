using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using HMWT_MUI.Helpers;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Лист призыва: то, что видит игрок, когда нажимает на своём жетоне
    /// «Лист призыва…». Короткий блок из библиотеки призывов плюс броски —
    /// d20, атака и урон уходят в общий чат через тот же DiceRoller, что у
    /// ведущего. Раньше с такого жетона открывался бестиарий мастера: он
    /// создавал пустую карточку существа, и игрок ничего не видел.
    /// </summary>
    public partial class SummonCardWindow : Window
    {
        private static readonly Regex AttackBonusRx = new Regex(
            @"\+\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex DamageRx = new Regex(
            @"(\d+)\s*[кdКD]\s*(\d+)(?:\s*([+\-])\s*(\d+))?", RegexOptions.Compiled);

        private readonly FrameworkElement _rollSource;
        private readonly SummonData _summon;

        /// <summary>
        /// Бросок через DiceRoller вызывающего окна: (формула, тип, что) →
        /// получилось ли. Окно само кубики не знает, поэтому карта передаёт
        /// сюда свой бросок — он же публикует результат в чат комнаты.
        /// </summary>
        public Func<string, string, string, bool> RollHandler { get; set; }

        public SummonCardWindow(FrameworkElement rollSource, SummonData summon)
        {
            InitializeComponent();

            _rollSource = rollSource;
            _summon = summon ?? new SummonData { Name = "Призыв" };

            Fill();
        }

        private void Fill()
        {
            NameText.Text = string.IsNullOrWhiteSpace(_summon.Name) ? "Призыв" : _summon.Name;
            KindText.Text = string.IsNullOrWhiteSpace(_summon.CreatureType)
                ? "Призыв персонажа"
                : _summon.CreatureType.Trim();

            var summary = new System.Collections.Generic.List<string>();
            if (_summon.MaxHp > 0) summary.Add($"ХП {_summon.Hp}/{_summon.MaxHp}");
            if (_summon.Ac > 0) summary.Add($"КД {_summon.Ac}");
            SummaryText.Text = string.Join(" · ", summary);

            SpeedText.Text = string.IsNullOrWhiteSpace(_summon.Speed)
                ? ""
                : "Скорость: " + _summon.Speed.Trim();

            StrText.Text = _summon.Str.ToString();
            DexText.Text = _summon.Dex.ToString();
            ConText.Text = _summon.Con.ToString();
            IntText.Text = _summon.Int.ToString();
            WisText.Text = _summon.Wis.ToString();
            ChaText.Text = _summon.Cha.ToString();

            AttacksText.Text = string.IsNullOrWhiteSpace(_summon.Attacks)
                ? "Атаки не записаны."
                : _summon.Attacks.Trim();
            AbilitiesText.Text = string.IsNullOrWhiteSpace(_summon.Abilities)
                ? "Особенностей нет."
                : _summon.Abilities.Trim();

            string photo = PhotoHelper.ResolvePhotoPath(_summon.PhotoPath);
            bool has = !string.IsNullOrEmpty(photo) && File.Exists(photo);
            PhotoView.Source = has ? PhotoHelper.LoadUnlocked(photo) : null;
            NoPhotoText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        }

        // ── броски ──

        private void RollD20_Click(object sender, RoutedEventArgs e)
        {
            Roll("1d20", "Проверка", "d20");
        }

        private void RollAttack_Click(object sender, RoutedEventArgs e)
        {
            int bonus = AttackBonus(_summon.Attacks);
            Roll(DiceFormulaParser.BuildAttackFormula(bonus), "Атака", "Удар");
        }

        private void RollDamage_Click(object sender, RoutedEventArgs e)
        {
            string formula = DamageFormula(_summon.Attacks);
            if (formula == null)
            {
                ShowError("В атаках не записана кость урона (например 2к8+4).");
                return;
            }

            Roll(formula, "Урон", "Удар");
        }

        private void Roll(string formula, string kind, string what)
        {
            try
            {
                if (RollHandler == null)
                {
                    ShowError("Бросок недоступен: окно открыто без карты.");
                    return;
                }

                if (RollHandler(formula, kind, what))
                    ErrorText.Visibility = Visibility.Collapsed;
                else
                    ShowError("Бросок не отправился — проверьте, что открыт персонаж.");
            }
            catch (Exception ex)
            {
                ShowError("Бросок не удался: " + ex.Message);
            }
        }

        private void SendToChat_Click(object sender, RoutedEventArgs e)
        {
            string text =
                $"{_summon.Name}" +
                (string.IsNullOrWhiteSpace(_summon.CreatureType) ? "" : " (" + _summon.CreatureType.Trim() + ")") +
                $": ХП {_summon.Hp}/{_summon.MaxHp}, КД {_summon.Ac}" +
                (string.IsNullOrWhiteSpace(_summon.Speed) ? "" : ", скорость " + _summon.Speed.Trim()) +
                (string.IsNullOrWhiteSpace(_summon.Attacks) ? "" : "\nАтаки: " + _summon.Attacks.Trim());

            try
            {
                var room = Net.RoomSession.Instance;
                if (room == null || !room.IsActive)
                {
                    ShowError("Чат доступен в комнате — подключитесь к мастеру.");
                    return;
                }

                room.PublishChat(text);
                ErrorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError("Не удалось отправить в чат: " + ex.Message);
            }
        }

        private void ShowError(string text)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = Visibility.Visible;
        }

        private static int AttackBonus(string attacks)
        {
            if (string.IsNullOrWhiteSpace(attacks)) return 0;
            var match = AttackBonusRx.Match(attacks);
            return match.Success && int.TryParse(match.Groups[1].Value, out int bonus) ? bonus : 0;
        }

        private static string DamageFormula(string attacks)
        {
            if (string.IsNullOrWhiteSpace(attacks)) return null;
            var match = DamageRx.Match(attacks);
            if (!match.Success) return null;

            string formula = match.Groups[1].Value + "к" + match.Groups[2].Value;
            if (match.Groups[3].Success) formula += match.Groups[3].Value + match.Groups[4].Value;
            return formula;
        }

        // ── окно ──

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
        }
    }
}
