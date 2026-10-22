using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HMWT_MUI.Models;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>Карточка заклинания из справочника — открывается по ссылке
    /// из бестиария. Отдельное окно, чтобы не уводить мастера с карточки монстра.</summary>
    public sealed class SpellDetailWindow : Window
    {
        private SpellDetailWindow()
        {
            Title = "Заклинание";
            Width = 640;
            Height = 720;
            MinWidth = 480;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = (Brush)FindResource("B.Panel");
            Foreground = (Brush)FindResource("B.Ink");
            FontFamily = (FontFamily)FindResource("F.Body");
            UseLayoutRounding = true;
        }

        public static void Show(FrameworkElement source, string spellName)
        {
            if (string.IsNullOrWhiteSpace(spellName)) return;

            var spell = SpellService.LoadSpellByName(spellName);
            if (spell == null)
            {
                MessageDialog.Info(Window.GetWindow(source),
                    "В справочнике заклинаний не нашлось «" + spellName + "». " +
                    "Возможно, нужный справочник ещё не импортирован.",
                    "Заклинание не найдено");
                return;
            }

            var window = new SpellDetailWindow { Owner = Window.GetWindow(source) };
            window.Content = BuildContent(spell);
            window.ShowDialog();
        }

        private static UIElement BuildContent(SpellData spell)
        {
            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new StackPanel();
            titleBlock.Children.Add(new TextBlock
            {
                Text = spell.Name,
                FontFamily = (FontFamily)windowFont(),
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("B.Accent"),
                TextWrapping = TextWrapping.Wrap
            });
            string level = spell.Level <= 0 ? "Заговор" : $"{spell.Level} уровень";
            string school = string.IsNullOrWhiteSpace(spell.School) ? "" : ", " + spell.School.ToLowerInvariant();
            titleBlock.Children.Add(new TextBlock
            {
                Text = level + school + (string.IsNullOrWhiteSpace(spell.Source) ? "" : " · " + spell.Source),
                FontSize = 12,
                Foreground = (Brush)Application.Current.FindResource("B.Mute"),
                Margin = new Thickness(0, 4, 0, 0)
            });
            header.Children.Add(titleBlock);

            var close = new Button
            {
                Content = "✕",
                Width = 30,
                Height = 30,
                FontSize = 15,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.FindResource("B.Mute"),
                BorderThickness = new Thickness(0),
                ToolTip = "Закрыть"
            };
            close.Click += (s, e) => Window.GetWindow(close)?.Close();
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            root.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            AddMeta(body, "Время сотворения", spell.CastingTime);
            AddMeta(body, "Дистанция", spell.Range);
            AddMeta(body, "Компоненты", spell.Components);
            AddMeta(body, "Длительность", spell.Duration,
                (spell.Concentration ? "Концентрация" : "") +
                (spell.Ritual ? (spell.Concentration ? " · " : "") + "Ритуал" : ""));

            body.Children.Add(Separator());
            AddProse(body, spell.Description);
            if (!string.IsNullOrWhiteSpace(spell.AtHigherLevels))
            {
                body.Children.Add(new TextBlock
                {
                    Text = "На больших уровнях",
                    Foreground = (Brush)Application.Current.FindResource("B.Frost"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    Margin = new Thickness(0, 12, 0, 4)
                });
                AddProse(body, spell.AtHigherLevels);
            }
            if (!string.IsNullOrWhiteSpace(spell.SaveNote))
            {
                body.Children.Add(new TextBlock
                {
                    Text = spell.SaveNote,
                    Foreground = (Brush)Application.Current.FindResource("B.Accent"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0)
                });
            }
            if (!string.IsNullOrWhiteSpace(spell.Material))
            {
                AddMeta(body, "Материал", spell.Material);
            }

            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            return root;
        }

        private static FontFamily windowFont()
            => (FontFamily)Application.Current.FindResource("F.Display");

        private static void AddMeta(Panel panel, string label, string value, string extra = null)
        {
            string text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(extra)) text += (text.Length > 0 ? " · " : "") + extra;
            if (text.Length == 0) return;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = (Brush)Application.Current.FindResource("B.Mute"),
                FontSize = 12
            });
            var valueBlock = new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource("B.Ink"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(valueBlock);
            panel.Children.Add(row);
        }

        private static void AddProse(Panel panel, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            panel.Children.Add(new TextBlock
            {
                Text = text.Trim(),
                Foreground = (Brush)Application.Current.FindResource("B.Ink"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        private static Border Separator() => new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.FindResource("B.Line"),
            Margin = new Thickness(0, 8, 0, 12)
        };
    }
}
