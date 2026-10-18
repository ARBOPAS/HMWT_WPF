using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Конструктор элементов в стиле приложения для инструментов ДМа.
    ///
    /// Бестиарий и трекер собираются кодом: полей у существа очень много, и
    /// разметка на каждое поле превратилась бы в тысячи строк. Здесь же стиль
    /// задан в одном месте — цвет, шрифт, скругление и поведение кнопок
    /// совпадают с остальными вкладками.
    /// </summary>
    public static class DmUiKit
    {
        // ══════════════════════ ПАЛИТРА ══════════════════════

        public static Brush B(string key)
        {
            var found = Application.Current?.TryFindResource("B." + key) as Brush;
            if (found != null) return found;

            switch (key)
            {
                case "Void": return Frozen(Color.FromRgb(0x09, 0x0C, 0x16));
                case "Slate": return Frozen(Color.FromRgb(0x11, 0x17, 0x29));
                case "Panel": return Frozen(Color.FromRgb(0x15, 0x1B, 0x2E));
                case "PanelHi": return Frozen(Color.FromRgb(0x1E, 0x25, 0x40));
                case "Line": return Frozen(Color.FromRgb(0x2B, 0x33, 0x52));
                case "Ink": return Frozen(Color.FromRgb(0xE6, 0xEA, 0xF5));
                case "Mute": return Frozen(Color.FromRgb(0x8B, 0x95, 0xB4));
                case "Gold": return Frozen(Color.FromRgb(0x5F, 0xD4, 0xDD));
                case "GoldSoft": return Frozen(Color.FromRgb(0x2A, 0x6A, 0x74));
                case "Arcane": return Frozen(Color.FromRgb(0xA9, 0x8B, 0xEC));
                case "Frost": return Frozen(Color.FromRgb(0x4F, 0xB6, 0xAC));
                case "Danger": return Frozen(Color.FromRgb(0xC0, 0x56, 0x4F));
                case "Ok": return Frozen(Color.FromRgb(0x5B, 0xB9, 0x74));
                case "Enchant": return Frozen(Color.FromRgb(0xC0, 0x56, 0x8F));
                case "Backdrop": return Frozen(Color.FromRgb(0x05, 0x07, 0x0E));
                case "GoldEdge": return Frozen(Color.FromRgb(0x5F, 0xD4, 0xDD));
                default: return Frozen(Colors.Gray);
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public static FontFamily Font(string key)
        {
            var found = Application.Current?.TryFindResource("F." + key) as FontFamily;
            if (found != null) return found;
            return key == "Display" ? new FontFamily("Cinzel, Segoe UI") : new FontFamily("Segoe UI");
        }

        public static FontFamily Display => Font("Display");
        public static FontFamily Body => Font("Body");

        /// <summary>Кисть с прозрачностью — для мягких подложек и подсветок.</summary>
        public static Brush Fade(Brush source, byte alpha)
        {
            if (source is SolidColorBrush solid)
            {
                var color = solid.Color;
                var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
                brush.Freeze();
                return brush;
            }
            return source;
        }

        // ══════════════════════ ТЕКСТ ══════════════════════

        public static TextBlock Title(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = Display,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = B("Gold"),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>Заголовок раздела с чертой под ним — то, что нужно вставлять в форму.</summary>
        public static StackPanel SectionPanel(string text, string hint = null)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 6) };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = text,
                FontFamily = Display,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = B("Gold")
            });
            if (!string.IsNullOrWhiteSpace(hint))
            {
                header.Children.Add(new TextBlock
                {
                    Text = "   " + hint,
                    FontSize = 10,
                    Foreground = B("Mute"),
                    VerticalAlignment = VerticalAlignment.Bottom
                });
            }
            panel.Children.Add(header);
            panel.Children.Add(new Border { Height = 1, Background = B("Line"), Margin = new Thickness(0, 4, 0, 0) });
            return panel;
        }

        public static TextBlock Label(string text, double fontSize = 10.5)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = B("Mute"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            };
        }

        public static TextBlock Value(string text, double fontSize = 13, Brush brush = null, bool bold = false)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = brush ?? B("Ink"),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap
            };
        }

        public static TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = B("Mute"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
        }

        // ══════════════════════ КОНТЕЙНЕРЫ ══════════════════════

        public static Border Panel(UIElement child, Thickness padding, Brush background = null,
            Brush border = null, double radius = 8, Thickness? margin = null)
        {
            return new Border
            {
                Background = background ?? B("Panel"),
                BorderBrush = border ?? B("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(radius),
                Padding = padding,
                Margin = margin ?? new Thickness(0),
                Child = child
            };
        }

        public static StackPanel Stack(params UIElement[] children)
        {
            var panel = new StackPanel();
            foreach (var child in children) if (child != null) panel.Children.Add(child);
            return panel;
        }

        public static StackPanel Horizontal(double gap = 8, params UIElement[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var child in children)
            {
                if (child == null) continue;
                if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, gap, 0);
                panel.Children.Add(child);
            }
            return panel;
        }

        /// <summary>Поле формы: подпись сверху, поле снизу.</summary>
        public static StackPanel FieldBlock(string label, UIElement control, string hint = null, double width = double.NaN)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
            if (double.IsNaN(width) == false) panel.Width = width;
            if (!string.IsNullOrWhiteSpace(label)) panel.Children.Add(Label(label));
            panel.Children.Add(control);
            if (!string.IsNullOrWhiteSpace(hint)) panel.Children.Add(Hint(hint));
            return panel;
        }

        // ══════════════════════ ПОЛЯ ВВОДА ══════════════════════

        public static TextBox Field(string value, double width = double.NaN, string tooltip = null,
            bool multiline = false, double height = double.NaN)
        {
            var box = new TextBox
            {
                Text = value ?? "",
                Background = B("Slate"),
                Foreground = B("Ink"),
                BorderBrush = B("Line"),
                BorderThickness = new Thickness(1),
                CaretBrush = B("Gold"),
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 12,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                ToolTip = tooltip,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = multiline
            };
            if (!double.IsNaN(width)) box.Width = width;
            if (multiline)
            {
                box.Height = double.IsNaN(height) ? 70 : height;
                box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                box.Height = double.IsNaN(height) ? 28 : height;
            }
            return box;
        }

        public static ComboBox Combo(IEnumerable<string> items, string selected, bool editable = false,
            double width = double.NaN)
        {
            var combo = new ComboBox
            {
                Background = B("Slate"),
                Foreground = B("Ink"),
                BorderBrush = B("Line"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 12,
                Height = 28,
                IsEditable = editable
            };

            ApplyDarkListStyle(combo);

            if (!double.IsNaN(width)) combo.Width = width;

            foreach (string item in items ?? Enumerable.Empty<string>())
                combo.Items.Add(item);

            if (!string.IsNullOrWhiteSpace(selected))
            {
                if (!combo.Items.Contains(selected)) combo.Items.Add(selected);
                combo.SelectedItem = selected;
            }
            else if (combo.Items.Count > 0 && !editable)
            {
                combo.SelectedIndex = 0;
            }

            return combo;
        }

        /// <summary>Комбобокс с парами «ключ — подпись»: наружу отдаёт ключ.</summary>
        public static ComboBox ComboPairs(IEnumerable<KeyValuePair<string, string>> items, string selectedKey,
            double width = double.NaN)
        {
            var combo = new ComboBox
            {
                Background = B("Slate"),
                Foreground = B("Ink"),
                BorderBrush = B("Line"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 12,
                Height = 28
            };
            ApplyDarkListStyle(combo);
            if (!double.IsNaN(width)) combo.Width = width;

            ComboBoxItem toSelect = null;
            foreach (var pair in items ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                var item = new ComboBoxItem
                {
                    Content = pair.Value,
                    Tag = pair.Key,
                    Background = B("Slate"),
                    Foreground = B("Ink")
                };
                combo.Items.Add(item);
                if (string.Equals(pair.Key, selectedKey, StringComparison.OrdinalIgnoreCase)) toSelect = item;
            }

            if (toSelect != null) combo.SelectedItem = toSelect;
            else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            return combo;
        }

        /// <summary>Ключ выбранного элемента ComboPairs.</summary>
        public static string ComboKey(ComboBox combo)
        {
            return (combo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        }

        /// <summary>
        /// Надевает на список тёмный стиль из общих токенов.
        ///
        /// Именно SetResourceReference, а не присваивание Style: в словаре
        /// ресурсов лежит отложенная ссылка, и обычное присваивание падает
        /// с «Unable to cast MS.Internal.NamedObject to Style».
        /// </summary>
        public static void ApplyDarkListStyle(ComboBox combo)
        {
            if (combo == null) return;

            try
            {
                combo.SetResourceReference(Control.StyleProperty, "DmComboBox");
                combo.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "DmComboBoxItem");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmUiKit] стиль списка: {ex.Message}");
            }
        }

        public static CheckBox Check(string text, bool isChecked)
        {
            var check = new CheckBox
            {
                Content = text,
                IsChecked = isChecked,
                Foreground = B("Ink"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 12, 4)
            };

            // Свой вид галочки, как в остальном приложении: на системной
            // чекбокс выбивался из тёмной темы
            try
            {
                if (Application.Current != null)
                {
                    var style = Application.Current.TryFindResource("CustomCheckBox") as Style;
                    if (style != null) check.Style = style;
                }
            }
            catch { }

            return check;
        }

        // ══════════════════════ КНОПКИ ══════════════════════

        public enum ButtonKind
        {
            Primary,
            Ghost,
            Danger,
            Soft
        }

        /// <summary>
        /// Кнопка действия. Если передан <paramref name="icon"/> — слева от текста
        /// рисуется иконка набора; если в самом тексте остался значок-эмодзи,
        /// он срезается (так миграция со эмодзи на иконки идёт по одной кнопке).
        /// </summary>
        public static Button Action(string text, RoutedEventHandler onClick, ButtonKind kind = ButtonKind.Primary,
            string tooltip = null, double width = double.NaN, double height = 30, string icon = null)
        {
            Brush background, foreground, hover;

            switch (kind)
            {
                case ButtonKind.Danger:
                    background = B("Danger");
                    foreground = Frozen(Color.FromRgb(0x1A, 0x08, 0x08));
                    hover = Fade(B("Danger"), 0xCC);
                    break;
                case ButtonKind.Ghost:
                    background = B("PanelHi");
                    foreground = B("Ink");
                    hover = B("Line");
                    break;
                case ButtonKind.Soft:
                    background = B("Slate");
                    foreground = B("Mute");
                    hover = B("Line");
                    break;
                default:
                    background = B("Gold");
                    foreground = Frozen(Color.FromRgb(0x04, 0x22, 0x2A));
                    hover = Fade(B("Gold"), 0xCC);
                    break;
            }

            string caption = StripLeadingSymbol(text);

            // Иконку можно не передавать: если подпись начиналась со значка
            // («🎲 Атака»), он уже срезан — выводим иконку из него
            if (string.IsNullOrWhiteSpace(icon)) icon = IconLibrary.IconForSymbol(text);

            var label = new TextBlock
            {
                Text = caption,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            object content = label;
            if (!string.IsNullOrWhiteSpace(icon) && IconLibrary.Has(icon) && caption.Length > 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var iconView = new IconView
                {
                    Kind = icon,
                    IconSize = 16,
                    IconBrush = foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0)
                };
                row.Children.Add(iconView);
                row.Children.Add(label);
                content = row;
            }

            var button = new Button
            {
                Content = content,
                Background = background,
                Foreground = foreground,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 5, 12, 5),
                Height = height,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!double.IsNaN(width)) button.Width = width;

            button.Template = BuildButtonTemplate(background, hover, 6);
            if (onClick != null) button.Click += onClick;
            return button;
        }

        /// <summary>
        /// Если текст начинается со значка (эмодзи или символа) — возвращает текст
        /// без него и с одним пробелом в начале. Иначе текст не меняется.
        /// </summary>
        public static string StripLeadingSymbol(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";

            int i = 0;
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            if (i == 0 || i >= text.Length) return text;

            // Значок в начале, за ним текст: оставляем только текст
            string tail = text.Substring(i).TrimStart();
            return tail.Length == 0 ? text : tail;
        }

        /// <summary>
        /// Маленькая кнопка-значок. Принимает ИЛИ имя иконки набора («close»,
        /// «edit», «arrowUp»), ИЛИ обычный глиф («✕», «＋») — старые вызовы
        /// продолжают работать, а переход на иконки делается правкой строки.
        /// </summary>
        public static Button Icon(string glyphOrKind, RoutedEventHandler onClick, string tooltip = null,
            double size = 26, ButtonKind kind = ButtonKind.Ghost)
        {
            // Принимаем и имя иконки («close»), и значок («✕») — второе нужно,
            // пока не все места переписаны на имена
            string resolved = IconLibrary.Has(glyphOrKind)
                ? glyphOrKind.Trim()
                : IconLibrary.IconForSymbol(glyphOrKind);

            if (!string.IsNullOrEmpty(resolved))
            {
                return IconButton(resolved, onClick, tooltip, size, kind);
            }

            var button = Action(glyphOrKind, onClick, kind, tooltip, size, size);
            button.Padding = new Thickness(0);
            if (button.Content is TextBlock text)
            {
                text.FontSize = 13;
                text.TextAlignment = TextAlignment.Center;
                text.HorizontalAlignment = HorizontalAlignment.Center;
            }
            return button;
        }

        /// <summary>Кнопка только с иконкой, без подписи.</summary>
        public static Button IconButton(string kind, RoutedEventHandler onClick, string tooltip = null,
            double size = 26, ButtonKind kindOfButton = ButtonKind.Ghost, bool filled = false)
        {
            var button = Action("", onClick, kindOfButton, tooltip, size, size);
            button.Padding = new Thickness(0);

            var iconView = new IconView
            {
                Kind = kind,
                IconSize = Math.Max(12, size - 10),
                IconBrush = button.Foreground,
                Filled = filled,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            button.Content = iconView;
            return button;
        }

        /// <summary>
        /// Иконка с подписью для любого места интерфейса: и меню, и заголовка.
        /// Принимает имя иконки («dice») или значок («🎲») — что удобнее на месте.
        /// </summary>
        public static StackPanel IconText(string iconOrSymbol, string text, double iconSize = 16,
            Brush brush = null, double gap = 8)
        {
            string kind = IconLibrary.Has(iconOrSymbol) ? iconOrSymbol : IconLibrary.IconForSymbol(iconOrSymbol);
            string caption = IconLibrary.Has(iconOrSymbol) ? (text ?? "") : StripLeadingSymbol(text);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(kind))
            {
                row.Children.Add(new IconView
                {
                    Kind = kind,
                    IconSize = iconSize,
                    IconBrush = brush ?? B("Ink"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, gap, 0)
                });
            }

            if (!string.IsNullOrEmpty(caption))
                row.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });

            return row;
        }

        /// <summary>
        /// Ставит иконку в готовый элемент (значок тоста, подпись заголовка).
        /// Принимает имя иконки или значок; если ничего не подошло — элемент прячется,
        /// чтобы в интерфейсе не осталось пустого квадрата.
        /// </summary>
        public static void ApplyIcon(IconView view, string iconOrSymbol, Brush brush = null)
        {
            if (view == null) return;

            string kind = IconLibrary.Has(iconOrSymbol) ? iconOrSymbol.Trim() : IconLibrary.IconForSymbol(iconOrSymbol);
            view.Kind = kind ?? "";
            if (brush != null) view.IconBrush = brush;
            view.Visibility = string.IsNullOrEmpty(kind) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>То же для мест, где значок пока ещё текст: подменяет текст на иконку.</summary>
        public static void ApplyIcon(System.Windows.Controls.TextBlock host, string iconOrSymbol, Brush brush = null)
        {
            if (host == null) return;

            string kind = IconLibrary.Has(iconOrSymbol) ? iconOrSymbol.Trim() : IconLibrary.IconForSymbol(iconOrSymbol);
            host.Text = kind ?? "";
            if (brush != null) host.Foreground = brush;
        }

        /// <summary>
        /// Заменяет строковую подпись кнопки на «иконка + текст».
        ///
        /// Нужно там, где кнопка создаётся вручную, а не через <see cref="Action"/>:
        /// значок в начале подписи («🎲 Бросить») превращается в иконку набора.
        /// Если значка нет — кнопка не меняется.
        /// </summary>
        public static void ApplyIcon(Button button, string icon = null)
        {
            if (button == null) return;
            if (!(button.Content is string text) || string.IsNullOrWhiteSpace(text)) return;

            string kind = string.IsNullOrWhiteSpace(icon) ? IconLibrary.IconForSymbol(text) : icon;
            if (string.IsNullOrEmpty(kind)) return;

            button.Content = IconText(kind, text, 15, button.Foreground);
        }

        /// <summary>
        /// Шаблон кнопки со скруглением и подсветкой при наведении.
        /// Строится кодом: у динамических кнопок нет разметки, а вид должен
        /// совпадать с кнопками остальных вкладок.
        /// </summary>
        private static ControlTemplate BuildButtonTemplate(Brush normal, Brush hover, double radius)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "bd";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
            template.Triggers.Add(hoverTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "bd"));
            template.Triggers.Add(disabledTrigger);

            return template;
        }

        /// <summary>Разделительная вертикальная черта между блоками кнопок.</summary>
        public static Border Separator(double height = 26)
        {
            return new Border
            {
                Width = 1,
                Height = height,
                Background = B("Line"),
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>Плашка-метка: «ВЕДУЩИЙ», «РАУНД 2», «СВОЁ».</summary>
        public static Border Chip(string text, Brush accent = null, bool filled = false)
        {
            var color = accent ?? B("Gold");
            var border = new Border
            {
                Background = filled ? color : Fade(color, 0x26),
                BorderBrush = color,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = filled ? B("Void") : color,
                    FontFamily = Display
                }
            };
            return border;
        }

        /// <summary>Кружок состояния (как у отряда в подвале).</summary>
        public static Ellipse Dot(Brush color, double size = 9)
        {
            return new Ellipse
            {
                Width = size,
                Height = size,
                Fill = color,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        /// <summary>Пустое состояние: объясняем, что делать, вместо пустого экрана.</summary>
        public static Border EmptyState(string title, string hint)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = Display,
                FontSize = 14,
                Foreground = B("Mute"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = B("Mute"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 420,
                Margin = new Thickness(0, 6, 0, 0)
            });

            return new Border
            {
                Background = Fade(B("Slate"), 0x88),
                BorderBrush = B("Line"),
                BorderThickness = new Thickness(1, 1, 1, 1),
                CornerRadius = new CornerRadius(8),
                Child = panel,
                Margin = new Thickness(0, 8, 0, 8)
            };
        }

        /// <summary>Обёртка прокрутки в стиле вкладок.</summary>
        public static ScrollViewer Scroll(UIElement child, bool horizontal = false)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0),
                Content = child
            };
        }
    }
}
