using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;
using HMWT_MUI.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Рисует карточку существа — как её печатают в книге.
    ///
    /// Главное здесь — интерактивные формулы: попадание «+4» по нажатию бросает
    /// d20 с этим бонусом, а «2к8+6 рубящим» — бросок урона. Мастеру не нужно
    /// ничего вводить руками, поэтому карточку использует и бестиарий, и
    /// предпросмотр в форме создания существа.
    /// </summary>
    public static class MonsterCardRenderer
    {
        /// <summary>Собирает карточку существа. source нужен, чтобы найти окно персонажа с DiceRoller.</summary>
        /// <summary>Расшифровка кода источника из бестиария.</summary>
        public static string SourceTitle(string code)
        {
            switch ((code ?? "").Trim().ToUpperInvariant())
            {
                case "MM": return "Monster Manual";
                case "VGTM": return "Volo's Guide to Monsters";
                case "XGTE": return "Xanathar's Guide to Everything";
                case "MTOF": return "Mordenkainen's Tome of Foes";
                case "SRD": return "SRD (CC-BY-4.0)";
                default: return code ?? "";
            }
        }
        public static StackPanel Build(MonsterData monster, FrameworkElement source, bool showDescription = true, bool compact = false)
        {
            var root = new StackPanel { Margin = new Thickness(0) };
            if (monster == null)
            {
                root.Children.Add(DmUiKit.EmptyState("Существо не выбрано",
                    "Выберите запись слева или создайте новое существо."));
                return root;
            }

            var style = new RollTextStyle
            {
                Text = DmUiKit.B("Ink"),
                Dice = DmUiKit.B("Gold"),
                Damage = DmUiKit.B("Mute"),
                Hint = DmUiKit.B("Mute"),
                Font = DmUiKit.Body,
                FontSize = 13
            };

            // Слева — имя, плашки и характеристики, справа — картинка на всю высоту блока.
            // Раньше фото было 150×150 рядом с заголовком, а плитки характеристик
            // занимали полосу ниже: получалось много пустого места и мелкая картинка.
            var top = new Grid();
            // Пропорции 2:1 — текст всегда получает две трети ширины, фото подстраивается.
            // С фиксированной шириной фото (300) в узком предпросмотре текст сжимался
            // до одной буквы в строку.
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 200 });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel();
            left.Children.Add(BuildHeader(monster));

            var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            chips.Children.Add(DmUiKit.Chip($"КД {monster.ArmorClass}", DmUiKit.B("Frost")));
            chips.Children.Add(DmUiKit.Chip($"ХИТЫ {monster.HitPointsLine()}", DmUiKit.B("Danger")));
            chips.Children.Add(DmUiKit.Chip($"СКОРОСТЬ {monster.SpeedLine()}", DmUiKit.B("Arcane")));
            chips.Children.Add(DmUiKit.Chip($"ПО {monster.ChallengeLine()}", DmUiKit.B("Gold")));
            chips.Children.Add(DmUiKit.Chip($"ИНИЦИАТИВА {(monster.InitiativeModifier >= 0 ? "+" : "")}{monster.InitiativeModifier}", DmUiKit.B("Ok")));
            if (!string.IsNullOrWhiteSpace(monster.Source))
            {
                // Источник показываем подписью, а не голым кодом: иначе непонятно,
                // откуда существо. Полное название — в подсказке.
                var sourceChip = DmUiKit.Chip("ИСТОЧНИК " + monster.Source.ToUpperInvariant(), DmUiKit.B("Mute"));
                sourceChip.ToolTip = SourceTitle(monster.Source);
                chips.Children.Add(sourceChip);
            }
            if (!string.IsNullOrWhiteSpace(monster.Tags)) chips.Children.Add(DmUiKit.Chip(monster.Tags.ToUpperInvariant(), DmUiKit.B("Enchant")));
            left.Children.Add(chips);

            left.Children.Add(BuildAbilities(monster));

            if (compact)
            {
                // Узкая колонка (предпросмотр в редакторе): фото сверху на всю ширину,
                // текст под ним. Иначе фото отжимало текст в одну букву на строку.
                var stacked = new StackPanel();
                var small = BuildPhoto(monster.PhotoPath);
                if (small != null)
                {
                    small.MaxWidth = 280;
                    small.MaxHeight = 220;
                    small.HorizontalAlignment = HorizontalAlignment.Left;
                    small.Margin = new Thickness(0, 0, 0, 10);
                    stacked.Children.Add(small);
                }
                stacked.Children.Add(left);
                root.Children.Add(stacked);
            }
            else
            {
                Grid.SetColumn(left, 0);
                top.Children.Add(left);

                var photo = BuildPhoto(monster.PhotoPath);
                if (photo != null)
                {
                    Grid.SetColumn(photo, 1);
                    top.Children.Add(photo);
                }
                root.Children.Add(top);
            }

            var stats = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            AddStat(stats, "Спасброски", monster.SavingThrows);
            AddStat(stats, "Навыки", monster.Skills);
            AddStat(stats, "Уязвимость к урону", monster.DamageVulnerabilities);
            AddStat(stats, "Сопротивление урону", monster.DamageResistances);
            AddStat(stats, "Иммунитет к урону", monster.DamageImmunities);
            AddStat(stats, "Иммунитет к состояниям", monster.ConditionImmunities);
            AddStat(stats, "Чувства", monster.Senses);
            AddStat(stats, "Языки", monster.Languages);
            AddStat(stats, "Бонус мастерства", monster.ProficiencyBonus > 0 ? $"+{monster.ProficiencyBonus}" : "");
            AddStat(stats, "Среда обитания", monster.Habitat);
            AddStat(stats, "Сокровища", monster.Treasure);
            left.Children.Add(stats);

            // ── черты ──
            foreach (var trait in monster.Traits.Where(t => t != null))
            {
                string name = (trait.Name ?? "").Trim();
                string text = (trait.Text ?? "").Trim();
                if (name.Length == 0 && text.Length == 0) continue;

                var block = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                if (name.Length > 0)
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = name.EndsWith(".") ? name : name + ".",
                        FontSize = 13.5,
                        FontWeight = FontWeights.Bold,
                        FontStyle = FontStyles.Italic,
                        Foreground = DmUiKit.B("Ink"),
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                if (text.Length > 0)
                    block.Children.Add(BuildSpellLinkedText(text, source, style,
                        part => RollDicePart(source, monster, part, "Особенность", name)));
                root.Children.Add(block);
            }

            // ── разделы действий ──
            foreach (var section in ActionSections.All)
            {
                var actions = monster.ActionsOf(section.Key);
                if (actions.Count == 0) continue;

                root.Children.Add(DmUiKit.SectionPanel(section.Title));

                if (section.Key == ActionSections.Legendary && !string.IsNullOrWhiteSpace(monster.LegendaryIntro))
                {
                    root.Children.Add(new TextBlock
                    {
                        Text = monster.LegendaryIntro.Trim(),
                        FontSize = 12,
                        FontStyle = FontStyles.Italic,
                        Foreground = DmUiKit.B("Mute"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 4)
                    });
                }

                if (section.Key == ActionSections.Lair && !string.IsNullOrWhiteSpace(monster.LairText))
                {
                    root.Children.Add(BuildSpellLinkedText(monster.LairText.Trim(), source, style,
                        part => RollDicePart(source, monster, part, "Логово", "Логово")));
                }

                foreach (var action in actions) root.Children.Add(BuildAction(monster, action, source, style));
            }

            if (!string.IsNullOrWhiteSpace(monster.Spells))
                root.Children.Add(BuildTextSection("ЗАКЛИНАНИЯ И ПСИОНИКА", monster.Spells,
                    monster, source, style, true));

            if (!string.IsNullOrWhiteSpace(monster.Equipment))
                root.Children.Add(BuildTextSection("СНАРЯЖЕНИЕ", monster.Equipment, monster, source, style));

            if (showDescription && !string.IsNullOrWhiteSpace(monster.Description))
                root.Children.Add(BuildTextSection("ОПИСАНИЕ", monster.Description, monster, source, style, true));

            return root;
        }

        // ══════════════════════ ЧАСТИ КАРТОЧКИ ══════════════════════

        private static UIElement BuildHeader(MonsterData monster)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(monster.Name) ? "Без имени" : monster.Name,
                FontFamily = DmUiKit.Display,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Gold"),
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(monster.NameEn))
            {
                text.Children.Add(new TextBlock
                {
                    Text = monster.NameEn.Trim(),
                    FontSize = 11.5,
                    FontStyle = FontStyles.Italic,
                    Foreground = DmUiKit.B("Mute")
                });
            }

            string typeLine = monster.TypeLine();
            if (typeLine.Length > 0)
            {
                text.Children.Add(new TextBlock
                {
                    Text = typeLine,
                    FontSize = 13.5,
                    FontStyle = FontStyles.Italic,
                    Foreground = DmUiKit.B("Ink"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            return text;
        }

        private static Border BuildPhoto(string relativePath)
        {
            try
            {
                string full = PhotoHelper.ResolvePhotoPath(relativePath);
                if (string.IsNullOrEmpty(full) || !File.Exists(full)) return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(full);
                bitmap.EndInit();

                return new Border
                {
                    MaxWidth = 340,
                    MaxHeight = 380,
                    Margin = new Thickness(16, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(10),
                    BorderBrush = DmUiKit.B("Line"),
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,
                    // Uniform: картинка вписывается целиком и не обрезается;
                    // DownOnly — маленькие картинки не растягиваем
                    Child = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform,
                        StretchDirection = StretchDirection.DownOnly,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top
                    }
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MonsterCard] фото: {ex.Message}");
                return null;
            }
        }

        private static UIElement BuildAbilities(MonsterData monster)
        {
            // WrapPanel, а не UniformGrid: плитка по ширине содержимого, 
            // а не растянутая на пол-экрана.
            var grid = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), ItemHeight = double.NaN };

            void Add(string label, int score)
            {
                int mod = MonsterData.ModifierOf(score);

                // Всё в одну строку: «СИЛ 17 +3». Раньше три строки в плитке давали
                // много пустого места и растягивали карточку на пол-экрана.
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = DmUiKit.B("Mute"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0)
                });
                panel.Children.Add(new TextBlock
                {
                    Text = score.ToString(),
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    FontFamily = DmUiKit.Display,
                    Foreground = DmUiKit.B("Ink"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                panel.Children.Add(new TextBlock
                {
                    Text = mod >= 0 ? "+" + mod : mod.ToString(),
                    FontSize = 10.5,
                    Foreground = DmUiKit.B("Arcane"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 1, 0, 0)
                });

                grid.Children.Add(new Border
                {
                    Background = DmUiKit.B("Slate"),
                    BorderBrush = DmUiKit.B("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    MinWidth = 86,
                    Child = panel
                });
            }

            Add("СИЛ", monster.Strength);
            Add("ЛОВ", monster.Dexterity);
            Add("ТЕЛ", monster.Constitution);
            Add("ИНТ", monster.Intelligence);
            Add("МДР", monster.Wisdom);
            Add("ХАР", monster.Charisma);
            return grid;
        }

        private static void AddStat(Panel parent, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var caption = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = DmUiKit.B("Mute"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(caption, 0);
            grid.Children.Add(caption);

            var text = new TextBlock
            {
                Text = value.Trim(),
                FontSize = 12,
                Foreground = DmUiKit.B("Ink"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            parent.Children.Add(grid);
        }

        private static UIElement BuildAction(MonsterData monster, MonsterActionData action,
            FrameworkElement source, RollTextStyle style)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

            // Заголовок действия и кнопки броска
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleLine = new WrapPanel();
            titleLine.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(action.Name) ? "Действие" : action.Name.Trim(),
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                FontStyle = FontStyles.Italic,
                Foreground = DmUiKit.B("Ink"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrWhiteSpace(action.Recharge))
                titleLine.Children.Add(DmUiKit.Chip(action.Recharge.Trim().ToUpperInvariant(), DmUiKit.B("Danger")));

            if (ActionSections.Normalize(action.Section) == ActionSections.Legendary && action.LegendaryCost > 0)
                titleLine.Children.Add(DmUiKit.Chip($"СТОИТ {action.LegendaryCost}", DmUiKit.B("Arcane")));

            if (!string.IsNullOrWhiteSpace(action.Kind))
            {
                titleLine.Children.Add(new TextBlock
                {
                    Text = action.Kind.Trim(),
                    FontSize = 10.5,
                    Foreground = DmUiKit.B("Mute"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
            }

            Grid.SetColumn(titleLine, 0);
            head.Children.Add(titleLine);

            // Кнопки «Атака» и «Урон» — быстрый бросок без нажатия на формулу
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            bool hasHit = DiceFormulaParser.TryParseHitBonus(action.HitBonus, out int hitBonus);
            var damageParts = DiceFormulaParser.ParseDamages(action.Damage);

            if (hasHit)
            {
                var name = action.Name ?? "";
                buttons.Children.Add(DmUiKit.Action("🎲 Атака", (s, e) =>
                {
                    DmRolls.RollAttack(source, hitBonus, monster.Name, name);
                }, DmUiKit.ButtonKind.Ghost, $"Бросок атаки: d20 {(hitBonus >= 0 ? "+" : "")}{hitBonus}", 92, 26));
            }

            if (damageParts.Count > 0)
            {
                var name = action.Name ?? "";
                buttons.Children.Add(DmUiKit.Action("🎲 Урон", (s, e) =>
                {
                    foreach (var part in damageParts)
                        DmRolls.RollDamage(source, part.Formula, part.Damage, monster.Name, name);
                }, DmUiKit.ButtonKind.Ghost, "Бросить весь урон этого действия", 92, 26));
            }

            if (buttons.Children.Count > 0)
            {
                Grid.SetColumn(buttons, 1);
                head.Children.Add(buttons);
            }

            panel.Children.Add(head);

            // Попадание: подсвеченный бонус кликабелен
            var hitLine = RollTextRenderer.BuildHitLine(action.HitBonus, action.Damage,
                bonus => DmRolls.RollAttack(source, bonus, monster.Name, action.Name), style);
            if (hitLine != null) panel.Children.Add(hitLine);

            // Дистанции и урон
            string distances = string.Join(", ", new[] { action.Reach, action.Range }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (distances.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = distances,
                    FontSize = 11.5,
                    Foreground = DmUiKit.B("Mute"),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            if (!string.IsNullOrWhiteSpace(action.Damage))
            {
                var damagePanel = new StackPanel { Orientation = Orientation.Horizontal };
                damagePanel.Children.Add(new TextBlock
                {
                    Text = "Урон: ",
                    FontSize = 12,
                    Foreground = DmUiKit.B("Mute"),
                    VerticalAlignment = VerticalAlignment.Top
                });
                damagePanel.Children.Add(RollTextRenderer.Build(action.Damage,
                    part => RollDicePart(source, monster, part, "Урон", action.Name), style));
                panel.Children.Add(damagePanel);
            }

            if (!string.IsNullOrWhiteSpace(action.SaveDc))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = action.SaveDc.Trim(),
                    FontSize = 11.5,
                    Foreground = DmUiKit.B("Frost"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            if (!string.IsNullOrWhiteSpace(action.Description))
                panel.Children.Add(BuildSpellLinkedText(action.Description, source, style,
                    part => RollDicePart(source, monster, part, "Действие", action.Name)));

            return DmUiKit.Panel(panel, new Thickness(10, 8, 10, 8), DmUiKit.B("PanelHi"), DmUiKit.B("Line"), 6,
                new Thickness(0, 2, 0, 2));
        }

        private static TextBlock BuildSpellLinkedText(string text,
            FrameworkElement source, RollTextStyle style, Action<FormulaPart> onDice)
        {
            var block = new TextBlock
            {
                FontFamily = style.Font,
                FontSize = style.FontSize,
                Foreground = style.Text,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = Math.Max(16, style.FontSize * 1.35)
            };
            SpellTextLinker.BuildRich(block, text.Trim(), name =>
            {
                if (Window.GetWindow(source) is CharacterInfoWindow info)
                    info.ShowSpellByName(name);
                else
                    SpellDetailWindow.Show(source, name);
            }, onDice, style);
            return block;
        }

        private static UIElement BuildTextSection(string title, string text, MonsterData monster,
            FrameworkElement source, RollTextStyle style, bool linkSpells = false)
        {
            var panel = new StackPanel();
            panel.Children.Add(DmUiKit.SectionPanel(title));
            if (linkSpells)
                panel.Children.Add(BuildSpellLinkedText(text, source, style,
                    part => RollDicePart(source, monster, part, title, "")));
            else
                panel.Children.Add(RollTextRenderer.Build(text.Trim(),
                    part => RollDicePart(source, monster, part, title, ""), style));
            return panel;
        }

        /// <summary>Бросок по нажатию на формулу внутри текста.</summary>
        private static void RollDicePart(FrameworkElement source, MonsterData monster, FormulaPart part,
            string rollType, string actionName)
        {
            string what = string.IsNullOrWhiteSpace(actionName) ? part.Formula : actionName;
            DmRolls.RollDamage(source, part.Formula, part.Damage, monster?.Name ?? "", what);
        }
    }
}
