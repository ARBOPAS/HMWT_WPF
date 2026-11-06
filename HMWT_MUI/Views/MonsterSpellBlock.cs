using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMWT_MUI.Helpers;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Блок «Заклинания и псионика» в карточке существа.
    ///
    /// Раньше весь блок был одним абзацем: «Заговоры (по желанию): … 1 круг (4):
    /// Щит, Туманный шаг. 2 круг (3): …» — глазами такое не читается, особенно
    /// у лича с восемью кругами. Теперь каждый круг — своя строка: слева подпись
    /// с числом ячеек, справа плашки заклинаний. Плашка известного справочнику
    /// заклинания нажимается и открывает его описание.
    ///
    /// Цвета берутся из токенов темы через PaletteBrush: у блока нет своих
    /// цветов, и в светлой теме он не останется тёмным пятном.
    /// </summary>
    public static class MonsterSpellBlock
    {
        private static readonly Color PanelHiFallback = Color.FromRgb(0x1B, 0x23, 0x38);

        /// <summary>
        /// Строки по кругам. resolveName возвращает каноническое название
        /// заклинания из справочника или null, если такого нет; openSpell
        /// открывает описание.
        /// </summary>
        public static Panel Build(MonsterSpellSheet sheet,
            Func<string, string> resolveName, Action<string> openSpell)
        {
            var host = new StackPanel();
            if (sheet == null || !sheet.IsStructured) return host;

            foreach (var group in sheet.Groups)
                host.Children.Add(BuildRow(group, resolveName, openSpell));

            return host;
        }

        private static UIElement BuildRow(MonsterSpellGroup group,
            Func<string, string> resolveName, Action<string> openSpell)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── слева: круг и ячейки ──
            var head = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 10, 0)
            };
            head.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontFamily = DisplayFont(),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = AccentFor(group.Level),
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(group.Slots))
            {
                head.Children.Add(new TextBlock
                {
                    Text = group.Slots,
                    FontSize = 9.5,
                    Foreground = Br("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                    Margin = new Thickness(0, 1, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            Grid.SetColumn(head, 0);
            grid.Children.Add(head);

            // ── справа: плашки заклинаний ──
            var right = new StackPanel();
            var chips = new WrapPanel();
            foreach (string spell in group.Spells)
                chips.Children.Add(BuildChip(spell, resolveName, openSpell));
            right.Children.Add(chips);

            if (!string.IsNullOrWhiteSpace(group.Note))
            {
                right.Children.Add(new TextBlock
                {
                    Text = group.Note,
                    FontSize = 10.5,
                    Foreground = Br("B.Mute", Color.FromRgb(0x8B, 0x95, 0xB4)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }

            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            return new Border
            {
                Background = Br("B.PanelHi", PanelHiFallback),
                BorderBrush = Br("B.Line", Color.FromRgb(0x2B, 0x33, 0x52)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 5),
                Margin = new Thickness(0, 2, 0, 3),
                Child = grid
            };
        }

        private static UIElement BuildChip(string raw, Func<string, string> resolveName, Action<string> openSpell)
        {
            string display = MonsterSpellSheet.DisplayName(raw);

            string canonical = null;
            try { canonical = resolveName?.Invoke(raw); } catch { }
            bool known = !string.IsNullOrWhiteSpace(canonical);

            var text = new TextBlock
            {
                Text = display,
                FontFamily = BodyFont(),
                FontSize = 12,

                // Название читается всегда одинаково: подбор справочника не
                // настолько полный, чтобы красить половину списка серым.
                // «Нет в справочнике» видно по подсказке и по курсору.
                Foreground = Br("B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5)),
                TextWrapping = TextWrapping.NoWrap
            };

            var chip = new Border
            {
                Background = Br("B.Slate", Color.FromRgb(0x11, 0x17, 0x29)),
                BorderBrush = Br("B.Line", Color.FromRgb(0x2B, 0x33, 0x52)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 5, 4),
                Child = text,
                ToolTip = known
                    ? "Открыть заклинание «" + canonical + "»"
                    : raw + " — нет в справочнике заклинаний"
            };

            if (known && openSpell != null)
            {
                chip.Cursor = Cursors.Hand;
                chip.MouseEnter += (s, e) =>
                {
                    chip.BorderBrush = Br("B.Frost", Color.FromRgb(0x74, 0xA9, 0xFF));
                    text.Foreground = Br("B.Frost", Color.FromRgb(0x74, 0xA9, 0xFF));
                };
                chip.MouseLeave += (s, e) =>
                {
                    chip.BorderBrush = Br("B.Line", Color.FromRgb(0x2B, 0x33, 0x52));
                    text.Foreground = Br("B.Ink", Color.FromRgb(0xE6, 0xEA, 0xF5));
                };
                chip.MouseLeftButtonUp += (s, e) =>
                {
                    try { openSpell(canonical); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MonsterSpell] открытие: {ex.Message}"); }
                };
            }

            return chip;
        }

        /// <summary>Цвет подписи круга: заговоры, младшие, средние, старшие.</summary>
        private static Brush AccentFor(int level)
        {
            if (level == 0) return Br("B.Enchant", Color.FromRgb(0xD8, 0x6F, 0xB0));
            if (level >= 7) return Br("B.Accent", Color.FromRgb(0x5F, 0xD4, 0xDD));
            if (level >= 4) return Br("B.Arcane", Color.FromRgb(0xA9, 0x8B, 0xEC));
            return Br("B.Frost", Color.FromRgb(0x74, 0xA9, 0xFF));
        }

        private static Brush Br(string key, Color fallback) => PaletteBrush.Get(key, fallback);

        private static FontFamily DisplayFont()
            => Application.Current?.TryFindResource("F.Display") as FontFamily
               ?? new FontFamily("Cinzel, Constantia, Cambria, Georgia, Segoe UI");

        private static FontFamily BodyFont()
            => Application.Current?.TryFindResource("F.Body") as FontFamily
               ?? new FontFamily("Segoe UI, Calibri");
    }
}
