using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Вкладка «Атаки»: всё, чем персонаж бьёт, в одном месте.
    ///
    /// Атаки собираются из предметов инвентаря и из заклинаний с уроном, а можно
    /// завести свою. Дальше запись живёт сама: попадание, несколько частей урона
    /// (например «1к8+3 рубящим» и «1к6 огнём»), дистанция, свойства. Это
    /// избавляет от уловки «заведу второй предмет, чтобы был второй тип урона».
    /// </summary>
    public sealed class AttacksView : UserControl
    {
        private readonly int _characterId;
        private readonly string _characterName;

        private readonly StackPanel _listHost = new StackPanel();
        private readonly TextBlock _status = DmUiKit.Value("", 11, DmUiKit.B("Mute"));

        private Grid _root;
        private Border _editorLayer;
        private StackPanel _editorForm;
        private TextBlock _editorTitle;
        private TextBlock _editorError;
        private TextBlock _editorPreview;

        private AttackData _draft;
        private bool _draftIsNew;
        private readonly List<DamagePart> _draftParts = new List<DamagePart>();

        /// <summary>Черновик — серия (мультиатака): вместо урона собираем список атак.</summary>
        private bool _draftMulti;
        private readonly List<int> _draftChildren = new List<int>();

        // Поля формы
        private TextBox _fName, _fBonus, _fReach, _fRange, _fProperties, _fNote;
        private ComboBox _fSection, _fAbility, _fDamageAbility;
        private CheckBox _fAuto, _fProficient;
        private TextBlock _fBonusHint;
        private StackPanel _partsHost;

        /// <summary>Список выбранных атак серии и список доступных — для формы мультиатаки.</summary>
        private StackPanel _childrenHost;
        private StackPanel _availableHost;

        private List<AttackData> _attacks = new List<AttackData>();

        public AttacksView(int characterId)
        {
            _characterId = characterId;
            _characterName = ReadCharacterName();
            BuildShell();
            Loaded += (s, e) => Refresh();
        }

        private string ReadCharacterName()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand("SELECT name FROM CharacterInfo WHERE id_character=@id", conn);
                cmd.Parameters.AddWithValue("@id", _characterId);
                return cmd.ExecuteScalar()?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AttacksView] имя персонажа: {ex.Message}");
                return "";
            }
        }

        #region === ОБОЛОЧКА ===

        private void BuildShell()
        {
            _root = new Grid { Background = Brushes.Transparent };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(DmUiKit.Title("АТАКИ"));
            _status.VerticalAlignment = VerticalAlignment.Bottom;
            _status.Margin = new Thickness(12, 0, 0, 3);
            title.Children.Add(_status);
            Grid.SetColumn(title, 0);
            titleRow.Children.Add(title);

            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(AddWithMargin(DmUiKit.Action("＋ Своя атака", (s, e) => OpenEditor(new AttackData
            {
                CharacterId = _characterId,
                Source = AttackSources.Manual,
                Name = "Новая атака"
            }, true), DmUiKit.ButtonKind.Primary, "Завести атаку вручную", 140, 30)));

            buttons.Children.Add(AddWithMargin(DmUiKit.Action("＋ Мультиатака", (s, e) => OpenEditor(new AttackData
            {
                CharacterId = _characterId,
                Source = AttackSources.Manual,
                Name = "Новая серия"
            }, true, multi: true), DmUiKit.ButtonKind.Ghost, "Серия из нескольких атак одним нажатием", 165, 30)));

            buttons.Children.Add(AddWithMargin(DmUiKit.Action("⚔ Из инвентаря", (s, e) => CollectFromInventory(),
                DmUiKit.ButtonKind.Ghost, "Добавить атаки по оружию из инвентаря", 150, 30)));

            buttons.Children.Add(AddWithMargin(DmUiKit.Action("✦ Из заклинаний", (s, e) => CollectFromSpells(),
                DmUiKit.ButtonKind.Ghost, "Добавить атаки по подготовленным заклинаниям с уроном", 150, 30)));

            buttons.Children.Add(AddWithMargin(DmUiKit.Action("Пересчитать попадание", (s, e) => Recalculate(true),
                DmUiKit.ButtonKind.Ghost, "Обновить попадание по характеристикам и мастерству", 190, 30)));

            Grid.SetColumn(buttons, 1);
            titleRow.Children.Add(buttons);
            header.Children.Add(titleRow);

            header.Children.Add(DmUiKit.Hint(
                "Попадание считается от характеристики и бонуса мастерства — или задайте своё число. " +
                "Частей урона может быть несколько: «Ещё один тип урона» в правке атаки. " +
                "Серия («Мультиатака») бросает несколько атак одним нажатием: укус и два удара когтями."));

            Grid.SetRow(header, 0);
            _root.Children.Add(header);

            // Прокрутку создаём один раз и привязываем именно её: если сделать
            // DmUiKit.Scroll дважды, список остаётся в первой обёртке, а в окно
            // попадает вторая — пустая
            var listScroll = DmUiKit.Scroll(_listHost);
            Grid.SetRow(listScroll, 1);
            _root.Children.Add(listScroll);

            BuildEditorLayer();
            Content = _root;
        }

        private static UIElement AddWithMargin(UIElement element)
        {
            if (element is FrameworkElement framework) framework.Margin = new Thickness(0, 0, 6, 6);
            return element;
        }

        #endregion

        #region === СПИСОК АТАК ===

        /// <summary>Перечитывает атаки из базы и рисует список.</summary>
        public void Refresh()
        {
            try
            {
                AttackService.EnsureTables();
                _attacks = AttackService.LoadAll(_characterId);
                AttackService.Recalculate(_attacks, AttackService.ProficiencyBonus(_characterId));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AttacksView] загрузка: {ex.Message}");
                _attacks = new List<AttackData>();
            }

            RenderList();
        }

        private void RenderList()
        {
            _listHost.Children.Clear();

            int prof = AttackService.ProficiencyBonus(_characterId);
            string ability = AttackAbilities.Title(_attacks.FirstOrDefault(a => a.AutoBonus)?.Ability ?? "str");
            _status.Text = _attacks.Count == 0
                ? "атак пока нет"
                : $"атак: {_attacks.Count} · мастерство +{prof} · попадание от {ability.ToLowerInvariant()}";

            if (_attacks.Count == 0)
            {
                _listHost.Children.Add(DmUiKit.EmptyState("Атак пока нет",
                    "Нажмите «Из инвентаря», чтобы собрать атаки по оружию, «Из заклинаний» — по заклинаниям с уроном, " +
                    "или заведите свою атаку вручную."));
                return;
            }

            foreach (var attack in _attacks)
                _listHost.Children.Add(attack.IsMulti ? BuildSeriesCard(attack) : BuildAttackCard(attack));
        }

        /// <summary>
        /// Карточка серии (мультиатаки): список входящих атак и одна кнопка на всю серию.
        ///
        /// Серия ссылается на обычные атаки, поэтому каждую из них по-прежнему можно
        /// править отдельно и бросать саму по себе.
        /// </summary>
        private UIElement BuildSeriesCard(AttackData series)
        {
            var byId = new Dictionary<int, AttackData>();
            foreach (var item in _attacks)
                if (item.Id > 0 && !item.IsMulti) byId[item.Id] = item;

            var children = new List<AttackData>();
            foreach (int id in series.CleanChildIds())
                if (byId.TryGetValue(id, out var child)) children.Add(child);

            var card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();

            var nameRow = new WrapPanel();
            nameRow.Children.Add(new TextBlock
            {
                Text = series.Name,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Ink"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            nameRow.Children.Add(DmUiKit.Chip("МУЛЬТИАТАКА", DmUiKit.B("Enchant")));
            nameRow.Children.Add(DmUiKit.Chip(AttackData.CountWord(children.Count).ToUpperInvariant(), DmUiKit.B("Mute")));
            nameRow.Children.Add(DmUiKit.Chip(series.SectionTitle, DmUiKit.B("Arcane")));
            if (!string.IsNullOrWhiteSpace(series.Note))
                nameRow.Children.Add(DmUiKit.Chip(series.Note.ToUpperInvariant(), DmUiKit.B("Mute")));
            info.Children.Add(nameRow);

            var style = new RollTextStyle
            {
                Text = DmUiKit.B("Ink"),
                Dice = DmUiKit.B("Gold"),
                Hint = DmUiKit.B("Mute"),
                Font = DmUiKit.Body,
                FontSize = 12
            };

            if (children.Count == 0)
            {
                info.Children.Add(DmUiKit.Hint("В серии пока нет атак — нажмите «правка» и выберите, из чего она состоит."));
            }
            else
            {
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    var row = new WrapPanel { Margin = new Thickness(0, i == 0 ? 4 : 0, 0, 0) };

                    row.Children.Add(new TextBlock
                    {
                        Text = (i + 1) + ". " + child.Name,
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = DmUiKit.B("Ink"),
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var hitLink = RollTextRenderer.BuildHitLink(child.BonusLine().Replace(" к попаданию", ""),
                        bonus => DmRolls.RollAttack(this, bonus, _characterName, $"{series.Name}: {child.Name}"), style);
                    if (hitLink != null)
                    {
                        var hitBlock = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                        hitBlock.Inlines.Add(hitLink);
                        row.Children.Add(hitBlock);
                    }

                    var parts = child.CleanParts();
                    if (parts.Count > 0)
                    {
                        row.Children.Add(new TextBlock
                        {
                            Text = "·",
                            FontSize = 12,
                            Foreground = DmUiKit.B("Mute"),
                            Margin = new Thickness(6, 0, 6, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        });

                        var damageText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
                        var local = child;
                        for (int p = 0; p < parts.Count; p++)
                        {
                            if (p > 0) damageText.Inlines.Add(new System.Windows.Documents.Run(" + ") { Foreground = DmUiKit.B("Mute") });
                            var parsed = DiceFormulaParser.ParseFirstDamage(parts[p].Formula);
                            if (parsed != null)
                            {
                                var part = parts[p];
                                damageText.Inlines.Add(RollTextRenderer.BuildDiceLink(parsed,
                                    x => RollDamagePart(local, x.Formula, x.Damage, 0), style));
                            }
                            else
                            {
                                damageText.Inlines.Add(new System.Windows.Documents.Run(parts[p].Formula) { Foreground = DmUiKit.B("Ink") });
                            }
                        }
                        row.Children.Add(damageText);
                    }

                    info.Children.Add(row);
                }

                info.Children.Add(DmUiKit.Hint($"Всего за серию: {SeriesAverage(children)} урона в среднем, если всё попадёт"));
            }

            Grid.SetColumn(info, 0);
            card.Children.Add(info);

            // ── кнопки серии ──
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

            var seriesButton = DmUiKit.Action("🎲 Серия", (s, e) => RollSeries(series, children),
                DmUiKit.ButtonKind.Primary, "Бросить попадание и урон каждой атаки серии", 110, 28);
            seriesButton.Margin = new Thickness(0, 0, 6, 0);
            controls.Children.Add(seriesButton);

            var editButton = DmUiKit.Icon("✎", (s, e) => OpenEditor(series, false, multi: true), "Править серию", 28);
            editButton.Margin = new Thickness(0, 0, 6, 0);
            controls.Children.Add(editButton);

            controls.Children.Add(DmUiKit.Icon("✕", (s, e) => DeleteAttack(series), "Удалить серию", 28, DmUiKit.ButtonKind.Soft));

            Grid.SetColumn(controls, 1);
            card.Children.Add(controls);

            return DmUiKit.Panel(card, new Thickness(12, 10, 12, 10), DmUiKit.B("Panel"), DmUiKit.B("Enchant"), 8,
                new Thickness(0, 0, 0, 8));
        }

        /// <summary>Средний урон серии: сумма средних по атакам (добавка от характеристики — у каждой своя).</summary>
        private static int SeriesAverage(List<AttackData> children)
        {
            int total = 0;
            foreach (var child in children)
            {
                int bonus = AttackService.DamageBonus(child);
                var parts = child.CleanParts();
                for (int i = 0; i < parts.Count; i++)
                {
                    string formula = parts[i].Formula;
                    if (i == 0 && bonus != 0) formula += bonus > 0 ? "+" + bonus : bonus.ToString();
                    total += DiceFormulaParser.AverageExpression(formula);
                }
            }
            return total;
        }

        /// <summary>Бросает всю серию по порядку: попадание и урон каждой атаки.</summary>
        private void RollSeries(AttackData series, List<AttackData> children)
        {
            if (children.Count == 0)
            {
                DmRolls.Toast(this, "В серии пока нет атак — добавьте их в правке", true);
                return;
            }

            foreach (var child in children)
            {
                DmRolls.RollAttack(this, child.AttackBonus, _characterName, $"{series.Name}: {child.Name}");
                if (child.HasDamage) RollAllDamage(child);
            }

            DmRolls.Toast(this, $"{series.Name}: брошено {AttackData.CountWord(children.Count)}");
        }

        private UIElement BuildAttackCard(AttackData attack)
        {
            var card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();

            // ── имя и метки ──
            var nameRow = new WrapPanel();
            nameRow.Children.Add(new TextBlock
            {
                Text = attack.Name,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Ink"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            nameRow.Children.Add(DmUiKit.Chip(attack.SourceTitle, SourceAccent(attack.Source)));
            if (attack.Section != AttackSections.Action)
                nameRow.Children.Add(DmUiKit.Chip(attack.SectionTitle, DmUiKit.B("Arcane")));
            if (!string.IsNullOrWhiteSpace(attack.Note))
                nameRow.Children.Add(DmUiKit.Chip(attack.Note.ToUpperInvariant(), DmUiKit.B("Mute")));
            info.Children.Add(nameRow);

            // ── попадание: число кликабельно ──
            var style = new RollTextStyle
            {
                Text = DmUiKit.B("Ink"),
                Dice = DmUiKit.B("Gold"),
                Hint = DmUiKit.B("Mute"),
                Font = DmUiKit.Body,
                FontSize = 12
            };

            var attackRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            attackRow.Children.Add(new TextBlock
            {
                Text = "Попадание: ",
                FontSize = 12,
                Foreground = DmUiKit.B("Mute"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var bonusLink = RollTextRenderer.BuildHitLink(attack.BonusLine().Replace(" к попаданию", ""),
                bonus => RollAttack(attack, bonus), style);
            if (bonusLink != null)
            {
                // Hyperlink — это Inline, в панель его не положить: оборачиваем в TextBlock
                var hitBlock = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                hitBlock.Inlines.Add(bonusLink);
                attackRow.Children.Add(hitBlock);
            }
            else
            {
                attackRow.Children.Add(DmUiKit.Value(attack.BonusLine(), 12));
            }

            if (!attack.AutoBonus)
                attackRow.Children.Add(new TextBlock
                {
                    Text = "  (своё число)",
                    FontSize = 10,
                    Foreground = DmUiKit.B("Mute"),
                    VerticalAlignment = VerticalAlignment.Center
                });

            string distance = attack.DistanceLine();
            if (distance.Length > 0)
            {
                attackRow.Children.Add(new TextBlock
                {
                    Text = "   " + distance,
                    FontSize = 11,
                    Foreground = DmUiKit.B("Frost"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            info.Children.Add(attackRow);

            // ── урон: каждая часть со своей формулой ──
            var parts = attack.CleanParts();
            if (parts.Count > 0)
            {
                int damageBonus = AttackService.DamageBonus(attack);

                var damageRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                damageRow.Children.Add(new TextBlock
                {
                    Text = "Урон: ",
                    FontSize = 12,
                    Foreground = DmUiKit.B("Mute"),
                    VerticalAlignment = VerticalAlignment.Top
                });

                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                int totalAverage = 0;

                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (i > 0) text.Inlines.Add(new System.Windows.Documents.Run(" + ") { Foreground = DmUiKit.B("Mute") });

                    // Добавка от характеристики — только к первой части, как в правилах
                    string formula = part.Formula;
                    if (i == 0 && damageBonus != 0)
                        formula += damageBonus > 0 ? "+" + damageBonus : damageBonus.ToString();

                    var parsed = DiceFormulaParser.ParseFirstDamage(formula);
                    if (parsed != null)
                    {
                        text.Inlines.Add(RollTextRenderer.BuildDiceLink(parsed,
                            p => RollDamagePart(attack, p.Formula, p.Damage, i), style));
                        totalAverage += DiceFormulaParser.AverageExpression(formula);
                    }
                    else
                    {
                        text.Inlines.Add(new System.Windows.Documents.Run(formula) { Foreground = DmUiKit.B("Ink") });
                    }

                    string typeWord = SpellService.DamageTypeLabel(part.DamageType);
                    if (string.IsNullOrWhiteSpace(typeWord)) typeWord = part.DamageType ?? "";
                    if (!string.IsNullOrWhiteSpace(typeWord))
                        text.Inlines.Add(new System.Windows.Documents.Run(" " + typeWord) { Foreground = DmUiKit.B("Mute") });
                }

                damageRow.Children.Add(text);
                info.Children.Add(damageRow);

                if (totalAverage > 0)
                    info.Children.Add(DmUiKit.Hint($"В среднем за попадание: {totalAverage}"));
            }
            else
            {
                info.Children.Add(DmUiKit.Hint("Урон не указан — впишите формулу в правке атаки."));
            }

            if (!string.IsNullOrWhiteSpace(attack.Properties))
            {
                info.Children.Add(new TextBlock
                {
                    Text = attack.Properties,
                    FontSize = 10.5,
                    Foreground = DmUiKit.B("Mute"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            Grid.SetColumn(info, 0);
            card.Children.Add(info);

            // ── кнопки ──
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

            var attackButton = DmUiKit.Action("🎲 Атака", (s, e) => RollAttack(attack, attack.AttackBonus),
                DmUiKit.ButtonKind.Primary, "Бросок попадания: d20 с бонусом атаки", 96, 28);
            attackButton.Margin = new Thickness(0, 0, 6, 0);
            controls.Children.Add(attackButton);

            if (attack.HasDamage)
            {
                var damageButton = DmUiKit.Action("🎲 Урон", (s, e) => RollAllDamage(attack),
                    DmUiKit.ButtonKind.Ghost, "Бросить весь урон этой атаки", 96, 28);
                damageButton.Margin = new Thickness(0, 0, 6, 0);
                controls.Children.Add(damageButton);
            }

            var editButton = DmUiKit.Icon("✎", (s, e) => OpenEditor(attack, false), "Править атаку", 28);
            editButton.Margin = new Thickness(0, 0, 6, 0);
            controls.Children.Add(editButton);

            controls.Children.Add(DmUiKit.Icon("✕", (s, e) => DeleteAttack(attack), "Удалить атаку", 28, DmUiKit.ButtonKind.Soft));

            Grid.SetColumn(controls, 1);
            card.Children.Add(controls);

            return DmUiKit.Panel(card, new Thickness(12, 10, 12, 10), DmUiKit.B("Panel"), DmUiKit.B("Line"), 8,
                new Thickness(0, 0, 0, 8));
        }

        private static Brush SourceAccent(string source)
        {
            switch (AttackSources.Normalize(source))
            {
                case AttackSources.Inventory: return DmUiKit.B("Frost");
                case AttackSources.Spell: return DmUiKit.B("Arcane");
                default: return DmUiKit.B("Ok");
            }
        }

        #endregion

        #region === БРОСКИ ===

        private void RollAttack(AttackData attack, int bonus)
        {
            DmRolls.RollAttack(this, bonus, _characterName, attack.Name);
        }

        private void RollDamagePart(AttackData attack, string formula, DamageKind kind, int partIndex)
        {
            int damageBonus = partIndex == 0 ? AttackService.DamageBonus(attack) : 0;
            string final = formula;
            if (damageBonus != 0) final += damageBonus > 0 ? "+" + damageBonus : damageBonus.ToString();

            DmRolls.RollDamage(this, final, kind, _characterName, attack.Name);
        }

        private void RollAllDamage(AttackData attack)
        {
            var parts = attack.CleanParts();
            if (parts.Count == 0)
            {
                DmRolls.Toast(this, "У этой атаки не указан урон", true);
                return;
            }

            var picked = DiceFormulaParser.ParseFirstDamage(parts[0].Formula);
            RollDamagePart(attack, picked != null ? picked.Formula : parts[0].Formula,
                picked != null ? picked.Damage : DamageKind.None, 0);

            for (int i = 1; i < parts.Count; i++)
            {
                var parsed = DiceFormulaParser.ParseDamages(parts[i].Formula);
                if (parsed.Count == 0) continue;
                DmRolls.RollDamage(this, parsed[0].Formula, parsed[0].Damage, _characterName, attack.Name);
            }
        }

        #endregion

        #region === СБОР И ДЕЙСТВИЯ ===

        private void CollectFromInventory()
        {
            try
            {
                int added = AttackService.CollectFromInventory(_characterId);
                Refresh();

                DmRolls.Toast(this, added > 0
                    ? $"Добавлено атак по оружию: {added}"
                    : "Нового оружия в инвентаре не нашлось (или атаки уже собраны)", added == 0);
            }
            catch (Exception ex)
            {
                DmRolls.Toast(this, $"Не удалось собрать атаки: {ex.Message}", true);
            }
        }

        private void CollectFromSpells()
        {
            try
            {
                int added = AttackService.CollectFromSpells(_characterId);
                int removed = AttackService.LastRemovedSpells;
                Refresh();

                // Про убранные говорим отдельно: заклинание перестало быть
                // подготовленным, значит и атаки по нему быть не должно
                string removedPart = removed > 0 ? $" Убрано неподготовленных: {removed}." : "";

                DmRolls.Toast(this, added > 0
                    ? $"Добавлено атак по подготовленным заклинаниям: {added}.{removedPart}"
                    : (removed > 0
                        ? $"Атаки обновлены:{removedPart}"
                        : "Подготовленных заклинаний с уроном не нашлось (или атаки уже собраны)"),
                    added == 0 && removed == 0);
            }
            catch (Exception ex)
            {
                DmRolls.Toast(this, $"Не удалось собрать атаки: {ex.Message}", true);
            }
        }

        private void Recalculate(bool notify)
        {
            try
            {
                int prof = AttackService.ProficiencyBonus(_characterId);
                int changed = AttackService.Recalculate(_attacks, prof);
                Refresh();

                if (notify)
                    DmRolls.Toast(this, changed > 0
                        ? $"Попадание пересчитано у атак: {changed}"
                        : "Попадание уже верное");
            }
            catch (Exception ex)
            {
                DmRolls.Toast(this, $"Не удалось пересчитать: {ex.Message}", true);
            }
        }

        private void DeleteAttack(AttackData attack)
        {
            bool confirmed = Windows.MessageDialog.Confirm(this,
                $"Удалить атаку «{attack.Name}»?\n\n" +
                "Атака, собранная из предмета или заклинания, вернётся, если снова нажать «Собрать».",
                "Удаление атаки", "Удалить");
            if (!confirmed) return;

            if (AttackService.Delete(attack.Id))
            {
                Refresh();
                DmRolls.Toast(this, $"Атака «{attack.Name}» удалена");
            }
            else
            {
                DmRolls.Toast(this, "Не удалось удалить атаку", true);
            }
        }

        #endregion

        #region === ФОРМА АТАКИ ===

        private void BuildEditorLayer()
        {
            _editorForm = new StackPanel { Margin = new Thickness(16, 8, 8, 16) };
            _editorTitle = DmUiKit.Title("АТАКА");
            _editorTitle.Margin = new Thickness(0, 0, 0, 8);
            _editorError = DmUiKit.Value("", 11.5, DmUiKit.B("Danger"));
            _editorError.Margin = new Thickness(0, 6, 0, 0);
            _editorPreview = DmUiKit.Value("", 11.5, DmUiKit.B("Frost"));
            _editorPreview.TextWrapping = TextWrapping.Wrap;
            _editorPreview.Margin = new Thickness(0, 10, 0, 0);

            var card = DmUiKit.Panel(DmUiKit.Scroll(_editorForm), new Thickness(16), DmUiKit.B("Panel"),
                DmUiKit.B("GoldEdge"), 10);
            card.Width = 900;
            card.MaxHeight = 900;

            _editorLayer = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x05, 0x07, 0x0E)),
                Visibility = Visibility.Collapsed,
                Child = card,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Panel.SetZIndex(_editorLayer, 100);
            Grid.SetRow(_editorLayer, 0);
            Grid.SetRowSpan(_editorLayer, 2);
            _root.Children.Add(_editorLayer);
        }

        /// <summary>Открывает форму: isNew — новая атака, иначе правка существующей.</summary>
        private void OpenEditor(AttackData attack, bool isNew, bool multi = false)
        {
            _draft = attack?.Clone() ?? new AttackData { CharacterId = _characterId };
            _draft.CharacterId = _characterId;
            _draftIsNew = isNew || _draft.Id <= 0;
            _draftMulti = multi || _draft.IsMulti;

            _draftParts.Clear();
            foreach (var part in _draft.CleanParts()) _draftParts.Add(part.Clone());

            _draftChildren.Clear();
            foreach (int id in _draft.CleanChildIds()) _draftChildren.Add(id);

            string what = _draftMulti ? (isNew ? "НОВАЯ СЕРИЯ" : "ПРАВКА СЕРИИ: ") : (isNew ? "НОВАЯ АТАКА" : "ПРАВКА: ");
            _editorTitle.Text = _draftIsNew
                ? (_draftMulti ? "НОВАЯ СЕРИЯ" : "НОВАЯ АТАКА")
                : what + _draft.Name.ToUpperInvariant();
            _editorError.Text = "";

            BuildEditorForm();
            RefreshEditorPreview();

            _editorLayer.Visibility = Visibility.Visible;
        }

        private void CloseEditor()
        {
            _editorLayer.Visibility = Visibility.Collapsed;
        }

        private void BuildEditorForm()
        {
            _editorForm.Children.Clear();

            // ── основное ──
            _editorForm.Children.Add(DmUiKit.SectionPanel("ОСНОВНОЕ",
                "источник: " + AttackSources.Title(_draft.Source)));

            _fName = DmUiKit.Field(_draft.Name, 320, "Название атаки");
            _fSection = DmUiKit.ComboPairs(
                AttackSections.All.Select(s => new KeyValuePair<string, string>(s.Key, s.Title)),
                _draft.Section, 200);

            var nameBlock = DmUiKit.FieldBlock("НАЗВАНИЕ *", _fName);
            _editorForm.Children.Add(new WrapPanel
            {
                Children = { nameBlock, DmUiKit.FieldBlock("КОГДА ИСПОЛЬЗУЕТСЯ", _fSection) }
            });

            if (_draftMulti)
            {
                // ── серия: состав ──
                //
                // У серии нет своего попадания и урона: она только собирает атаки
                // по порядку. Сами атаки правятся как обычно — в своих карточках
                _editorForm.Children.Add(DmUiKit.SectionPanel("СОСТАВ СЕРИИ",
                    "атаки бросаются по порядку, у каждой своё попадание и урон"));

                _childrenHost = new StackPanel();
                _editorForm.Children.Add(_childrenHost);
                _availableHost = new StackPanel();
                _editorForm.Children.Add(_availableHost);
                RebuildSeriesEditor();
            }
            else
            {
            // ── попадание ──
            _editorForm.Children.Add(DmUiKit.SectionPanel("ПОПАДАНИЕ",
                "авторасчёт: модификатор характеристики + бонус мастерства"));

            _fAuto = DmUiKit.Check("Считать автоматически", _draft.AutoBonus);
            _fAuto.Checked += (s, e) => { _draft.AutoBonus = true; UpdateBonusFields(); };
            _fAuto.Unchecked += (s, e) => { _draft.AutoBonus = false; UpdateBonusFields(); };

            _fProficient = DmUiKit.Check("Есть владение", _draft.Proficient);
            _fProficient.Checked += (s, e) => { _draft.Proficient = true; UpdateBonusFields(); };
            _fProficient.Unchecked += (s, e) => { _draft.Proficient = false; UpdateBonusFields(); };

            var flags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            flags.Children.Add(_fAuto);
            flags.Children.Add(_fProficient);
            _editorForm.Children.Add(flags);

            _fAbility = DmUiKit.ComboPairs(
                AttackAbilities.All.Select(a => new KeyValuePair<string, string>(a.Key, a.Title)),
                _draft.Ability, 200);
            _fAbility.SelectionChanged += (s, e) => { _draft.Ability = DmUiKit.ComboKey(_fAbility); UpdateBonusFields(); };

            _fBonus = DmUiKit.Field(_draft.AttackBonus.ToString(), 100, "Своё число попадания, например +7");
            _fBonus.TextChanged += (s, e) =>
            {
                if (!_draft.AutoBonus && int.TryParse((_fBonus.Text ?? "").Trim().Replace("+", ""), out int value))
                    _draft.AttackBonus = Math.Max(-20, Math.Min(40, value));
                RefreshEditorPreview();
            };

            _fBonusHint = DmUiKit.Hint("");
            _fBonusHint.Margin = new Thickness(0, 0, 0, 0);

            var bonusBlock = DmUiKit.FieldBlock("ХАРАКТЕРИСТИКА", _fAbility, null, 210);
            var manualBlock = DmUiKit.FieldBlock("ПОПАДАНИЕ (СВОЁ ЧИСЛО)", _fBonus, null, 240);

            _editorForm.Children.Add(new WrapPanel { Children = { bonusBlock, manualBlock } });
            _editorForm.Children.Add(_fBonusHint);

            // ── урон ──
            _editorForm.Children.Add(DmUiKit.SectionPanel("УРОН",
                "частей может быть несколько: «1к8+3 рубящим» и отдельно «1к6 огнём»"));

            _fDamageAbility = DmUiKit.ComboPairs(
                AttackAbilities.All.Select(a => new KeyValuePair<string, string>(a.Key, a.Title)),
                _draft.DamageAbility, 240);
            _fDamageAbility.SelectionChanged += (s, e) =>
            {
                _draft.DamageAbility = DmUiKit.ComboKey(_fDamageAbility);
                RefreshEditorPreview();
            };
            _editorForm.Children.Add(new WrapPanel
            {
                Children = { DmUiKit.FieldBlock("ДОБАВКА К УРОНУ ОТ ХАРАКТЕРИСТИКИ", _fDamageAbility) }
            });

            _partsHost = new StackPanel();
            _editorForm.Children.Add(_partsHost);
            RebuildDraftParts();

            var addPart = DmUiKit.Action("＋ Ещё один тип урона", (s, e) =>
            {
                CollectDraftFields();
                _draftParts.Add(new DamagePart());
                RebuildDraftParts();
                RefreshEditorPreview();
            }, DmUiKit.ButtonKind.Ghost, null, 200, 28);
            _editorForm.Children.Add(addPart);

            }   // конец ветки обычной атаки

            // ── прочее ──
            //
            // У серии дистанции и свойств нет: они у каждой входящей атаки свои
            if (!_draftMulti)
                _editorForm.Children.Add(DmUiKit.SectionPanel("ДИСТАНЦИЯ И СВОЙСТВА"));

            _fNote = DmUiKit.Field(_draft.Note, 200, "Заметка: «только по цели внизу»");

            if (_draftMulti)
            {
                _editorForm.Children.Add(DmUiKit.FieldBlock("ЗАМЕТКА", _fNote, null, 420));
            }
            else
            {
                _fReach = DmUiKit.Field(_draft.Reach, 150, "Например: 5 футов");
                _fRange = DmUiKit.Field(_draft.Range, 170, "Например: 80/320 футов");
                _fProperties = DmUiKit.Field(_draft.Properties, 260, "двуручное, тяжёлое, метательное");

                _editorForm.Children.Add(new WrapPanel
                {
                    Children =
                    {
                        DmUiKit.FieldBlock("ДОСЯГАЕМОСТЬ", _fReach),
                        DmUiKit.FieldBlock("ДИСТАНЦИЯ", _fRange),
                        DmUiKit.FieldBlock("СВОЙСТВА", _fProperties),
                        DmUiKit.FieldBlock("ЗАМЕТКА", _fNote)
                    }
                });
            }

            _editorForm.Children.Add(_editorPreview);
            _editorForm.Children.Add(_editorError);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            actions.Children.Add(DmUiKit.Action("💾 Сохранить", (s, e) => SaveDraft(), DmUiKit.ButtonKind.Primary, null, 140, 32));
            var cancel = DmUiKit.Action("Отмена", (s, e) => CloseEditor(), DmUiKit.ButtonKind.Ghost, null, 110, 32);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(cancel);
            _editorForm.Children.Add(actions);

            UpdateBonusFields();
        }

        /// <summary>
        /// Перерисовывает состав серии: сверху выбранные атаки по порядку, снизу —
        /// те, которые можно добавить. Порядок меняется стрелками: в серии он важен.
        /// </summary>
        private void RebuildSeriesEditor()
        {
            if (_childrenHost == null || _availableHost == null) return;

            _childrenHost.Children.Clear();
            _availableHost.Children.Clear();

            var byId = new Dictionary<int, AttackData>();
            foreach (var item in _attacks)
                if (item.Id > 0 && !item.IsMulti && item.Id != _draft.Id) byId[item.Id] = item;

            // ── выбранные ──
            if (_draftChildren.Count == 0)
            {
                _childrenHost.Children.Add(DmUiKit.Hint(
                    "Серия пустая. Добавьте атаки из списка ниже — их можно бросать и по отдельности."));
            }
            else
            {
                for (int i = 0; i < _draftChildren.Count; i++)
                {
                    int id = _draftChildren[i];
                    byId.TryGetValue(id, out var child);
                    _childrenHost.Children.Add(BuildSeriesRow(id, child, i));
                }
            }

            // ── доступные для добавления ──
            var available = _attacks.Where(a => a.Id > 0 && !a.IsMulti && !_draftChildren.Contains(a.Id)).ToList();

            _availableHost.Children.Add(new TextBlock
            {
                Text = available.Count == 0
                    ? "Других атак нет — заведите их кнопками «＋ Своя атака», «⚔ Из инвентаря», «✦ Из заклинаний»."
                    : "ДОБАВИТЬ В СЕРИЮ",
                FontFamily = DmUiKit.Display,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Mute"),
                Margin = new Thickness(0, 12, 0, 6)
            });

            foreach (var candidate in available)
                _availableHost.Children.Add(BuildAvailableRow(candidate));

            RefreshEditorPreview();
        }

        /// <summary>Строка выбранной атаки: номер, название, попадание и урон, стрелки и удаление.</summary>
        private UIElement BuildSeriesRow(int id, AttackData child, int index)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = (index + 1) + ". " + (child != null ? child.Name : "атака удалена"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = child != null ? DmUiKit.B("Ink") : DmUiKit.B("Danger")
            });

            if (child != null)
            {
                text.Children.Add(new TextBlock
                {
                    Text = child.Describe(),
                    FontSize = 10.5,
                    Foreground = DmUiKit.B("Mute"),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var up = DmUiKit.Icon("▲", (s, e) => MoveChild(index, -1), "Выше", 26, DmUiKit.ButtonKind.Ghost);
            up.Margin = new Thickness(0, 0, 4, 0);
            controls.Children.Add(up);

            var down = DmUiKit.Icon("▼", (s, e) => MoveChild(index, 1), "Ниже", 26, DmUiKit.ButtonKind.Ghost);
            down.Margin = new Thickness(0, 0, 4, 0);
            controls.Children.Add(down);

            controls.Children.Add(DmUiKit.Icon("✕", (s, e) =>
            {
                if (index >= 0 && index < _draftChildren.Count) _draftChildren.RemoveAt(index);
                RebuildSeriesEditor();
            }, "Убрать из серии", 26, DmUiKit.ButtonKind.Soft));

            Grid.SetColumn(controls, 1);
            grid.Children.Add(controls);

            return DmUiKit.Panel(grid, new Thickness(10, 6, 10, 6), DmUiKit.B("Slate"), DmUiKit.B("Line"), 6);
        }

        /// <summary>Строка доступной атаки с кнопкой «добавить».</summary>
        private UIElement BuildAvailableRow(AttackData candidate)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = candidate.Name,
                FontSize = 12,
                Foreground = DmUiKit.B("Ink")
            });
            text.Children.Add(new TextBlock
            {
                Text = candidate.Describe(),
                FontSize = 10,
                Foreground = DmUiKit.B("Mute"),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var add = DmUiKit.Action("＋", (s, e) =>
            {
                if (_draftChildren.Count >= AttackData.MaxMultiChildren)
                {
                    DmRolls.Toast(this, $"В серии не больше {AttackData.MaxMultiChildren} атак", true);
                    return;
                }
                if (!_draftChildren.Contains(candidate.Id)) _draftChildren.Add(candidate.Id);
                RebuildSeriesEditor();
            }, DmUiKit.ButtonKind.Ghost, "Добавить атаку в серию", 34, 26);
            Grid.SetColumn(add, 1);
            grid.Children.Add(add);

            return grid;
        }

        /// <summary>Двигает атаку в серии выше или ниже.</summary>
        private void MoveChild(int index, int delta)
        {
            int target = index + delta;
            if (index < 0 || index >= _draftChildren.Count) return;
            if (target < 0 || target >= _draftChildren.Count) return;

            int value = _draftChildren[index];
            _draftChildren.RemoveAt(index);
            _draftChildren.Insert(target, value);
            RebuildSeriesEditor();
        }

        private void UpdateBonusFields()
        {
            if (_fBonus == null) return;

            _fBonus.IsEnabled = !_draft.AutoBonus;
            if (_fAbility != null) _fAbility.IsEnabled = !_draft.AutoBonus;
            if (_fProficient != null) _fProficient.IsEnabled = !_draft.AutoBonus;

            int prof = AttackService.ProficiencyBonus(_characterId);
            int score = AttackService.AbilityScore(_characterId, _draft.Ability);
            int mod = AttackService.AbilityModifier(score);

            if (_draft.AutoBonus)
            {
                int total = mod + (_draft.Proficient ? prof : 0);
                _draft.AttackBonus = total;
                if (_fBonus != null) _fBonus.Text = total.ToString();
                if (_fBonusHint != null)
                    _fBonusHint.Text = $"{AttackAbilities.Title(_draft.Ability)} {score} ({(mod >= 0 ? "+" : "")}{mod})" +
                                       (_draft.Proficient ? $" + мастерство +{prof}" : "") +
                                       $" = {(total >= 0 ? "+" : "")}{total}";
            }
            else if (_fBonusHint != null)
            {
                _fBonusHint.Text = "Попадание задано вручную — расчёт не применяется.";
            }

            RefreshEditorPreview();
        }

        private void RebuildDraftParts()
        {
            if (_partsHost == null) return;
            _partsHost.Children.Clear();

            if (_draftParts.Count == 0)
                _partsHost.Children.Add(DmUiKit.Hint("Урон не указан. Добавьте часть: формула и тип урона."));

            for (int i = 0; i < _draftParts.Count; i++) _partsHost.Children.Add(BuildDraftPartRow(_draftParts[i], i));
        }

        private UIElement BuildDraftPartRow(DamagePart part, int index)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var formula = DmUiKit.Field(part.Formula, 120, "Формула: 1к8+3");
            formula.TextChanged += (s, e) => { part.Formula = formula.Text; RefreshEditorPreview(); };
            Grid.SetColumn(formula, 0);
            grid.Children.Add(formula);

            var typeText = string.IsNullOrWhiteSpace(part.DamageType)
                ? ""
                : SpellService.DamageTypeLabel(part.DamageType);

            // Ширину не задаём: список растягивается по своей колонке.
            // Ноль здесь означал бы невидимое поле — так и выглядело пустое место
            var typeBox = DmUiKit.Combo(DamageTypeChoices(), typeText, true);
            typeBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            typeBox.LostFocus += (s, e) => { part.DamageType = SpellService.NormalizeDamageType(typeBox.Text); RefreshEditorPreview(); };
            typeBox.SelectionChanged += (s, e) => { part.DamageType = SpellService.NormalizeDamageType(typeBox.Text); RefreshEditorPreview(); };
            Grid.SetColumn(typeBox, 2);
            grid.Children.Add(typeBox);

            var perLevel = DmUiKit.Field(part.PerLevelDamage, 120, "За уровень выше: 1к6");
            perLevel.TextChanged += (s, e) => { part.PerLevelDamage = perLevel.Text; RefreshEditorPreview(); };
            Grid.SetColumn(perLevel, 4);
            grid.Children.Add(perLevel);

            var note = DmUiKit.Field(part.Note, 150, "Уточнение");
            note.TextChanged += (s, e) => { part.Note = note.Text; };
            Grid.SetColumn(note, 6);
            grid.Children.Add(note);

            var remove = DmUiKit.Icon("✕", (s, e) =>
            {
                if (index >= 0 && index < _draftParts.Count) _draftParts.RemoveAt(index);
                RebuildDraftParts();
                RefreshEditorPreview();
            }, "Убрать эту часть урона", 28, DmUiKit.ButtonKind.Soft);
            Grid.SetColumn(remove, 7);
            grid.Children.Add(remove);

            return new StackPanel
            {
                Children =
                {
                    DmUiKit.Label("ФОРМУЛА · ТИП УРОНА · ЗА УРОВЕНЬ ВЫШЕ · УТОЧНЕНИЕ"),
                    grid
                }
            };
        }

        private static List<string> DamageTypeChoices()
        {
            var list = new List<string> { "" };
            foreach (var (_, ru) in SpellService.DamageTypes)
                if (!list.Contains(ru)) list.Add(ru);
            return list;
        }

        private void CollectDraftFields()
        {
            if (_draft == null || _fName == null) return;

            _draft.Name = (_fName.Text ?? "").Trim();
            _draft.Section = DmUiKit.ComboKey(_fSection);
            _draft.Note = (_fNote?.Text ?? "").Trim();

            if (_draftMulti)
            {
                // У серии нет своих попадания и урона — только список атак
                _draft.ChildIds = _draftChildren.Where(id => id > 0).Distinct().ToList();
                _draft.DamageParts = new List<DamagePart>();
                _draft.AttackBonus = 0;
                return;
            }

            _draft.Ability = DmUiKit.ComboKey(_fAbility);
            _draft.DamageAbility = DmUiKit.ComboKey(_fDamageAbility);
            _draft.Reach = (_fReach?.Text ?? "").Trim();
            _draft.Range = (_fRange?.Text ?? "").Trim();
            _draft.Properties = (_fProperties?.Text ?? "").Trim();
            _draft.DamageParts = _draftParts.Where(p => p != null && !p.IsEmpty).Select(p => p.Clone()).ToList();
        }

        private void RefreshEditorPreview()
        {
            if (_editorPreview == null || _draft == null) return;

            string draftName = string.IsNullOrWhiteSpace(_draft.Name)
                ? (_draftMulti ? "серия" : "атака")
                : _draft.Name;

            // ── серия: перечисляем состав и средний урон ──
            if (_draftMulti)
            {
                var byId = new Dictionary<int, AttackData>();
                foreach (var item in _attacks)
                    if (item.Id > 0) byId[item.Id] = item;

                var names = new List<string>();
                int average = 0;

                foreach (int id in _draftChildren)
                {
                    if (!byId.TryGetValue(id, out var child)) { names.Add("удалённая атака"); continue; }
                    names.Add(child.Name);
                    average += SeriesAverage(new List<AttackData> { child });
                }

                _editorPreview.Text = names.Count == 0
                    ? $"{draftName}: серия пустая"
                    : $"{draftName}: {AttackData.CountWord(names.Count)} — " + string.Join(" → ", names) +
                      (average > 0 ? $" · в среднем {average} урона за серию" : "");
                return;
            }

            int bonus = _draft.AutoBonus
                ? AttackService.AbilityModifier(AttackService.AbilityScore(_characterId, _draft.Ability))
                  + (_draft.Proficient ? AttackService.ProficiencyBonus(_characterId) : 0)
                : _draft.AttackBonus;

            int damageBonus = string.Equals(AttackAbilities.Normalize(_draft.DamageAbility), AttackAbilities.None, StringComparison.OrdinalIgnoreCase)
                ? 0
                : AttackService.AbilityModifier(AttackService.AbilityScore(_characterId, _draft.DamageAbility));

            var lines = new List<string>();
            var parts = _draftParts.Where(p => p != null && !p.IsEmpty).ToList();

            for (int i = 0; i < parts.Count; i++)
            {
                string formula = parts[i].Formula;
                if (i == 0 && damageBonus != 0) formula += damageBonus > 0 ? "+" + damageBonus : damageBonus.ToString();

                string type = SpellService.DamageTypeLabel(parts[i].DamageType);
                string line = formula + (string.IsNullOrWhiteSpace(type) ? "" : " " + type);

                int average = DiceFormulaParser.AverageExpression(formula);
                if (average > 0) line += $" (в среднем {average})";
                lines.Add(line);
            }

            string bonusText = (bonus >= 0 ? "+" : "") + bonus;
            _editorPreview.Text = lines.Count == 0
                ? $"{draftName}: {(bonusText)} к попаданию, урон не указан"
                : $"{draftName}: {bonusText} к попаданию · " + string.Join(" + ", lines);
        }

        private void SaveDraft()
        {
            CollectDraftFields();

            if (_draftMulti && _draftChildren.Count == 0)
            {
                _editorError.Text = "В серии должна быть хотя бы одна атака.";
                DmRolls.Toast(this, _editorError.Text, true);
                return;
            }

            if (!_draft.Validate(out string error))
            {
                _editorError.Text = error;
                DmRolls.Toast(this, error, true);
                return;
            }

            if (!_draftMulti && _draft.AutoBonus)
            {
                _draft.AttackBonus = AttackService.AbilityModifier(AttackService.AbilityScore(_characterId, _draft.Ability))
                                     + (_draft.Proficient ? AttackService.ProficiencyBonus(_characterId) : 0);
            }

            int id = AttackService.Save(_draft);
            if (id <= 0)
            {
                _editorError.Text = "Не удалось сохранить атаку — проверьте базу данных.";
                return;
            }

            CloseEditor();
            Refresh();
            DmRolls.Toast(this, _draftIsNew
                ? $"{(_draftMulti ? "Серия" : "Атака")} «{_draft.Name}» добавлена"
                : $"{(_draftMulti ? "Серия" : "Атака")} «{_draft.Name}» сохранена");
        }

        #endregion
    }
}
