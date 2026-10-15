using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMWT_MUI.Services;
using HMWT_MUI.Helpers;

namespace HMWT_MUI
{
    /// <summary>
    /// Парсер Markdown ↔ FlowDocument для нативного редактора заметок.
    /// Поддерживает: заголовки, списки (в т.ч. чек-листы), таблицы, цитаты,
    /// блоки кода, картинки (с размером), wiki-ссылки [[...]], выделения.
    /// </summary>
    public static class MarkdownParser
    {
        #region === ЦВЕТА ТЕМЫ ===

        private static readonly Color Frost = (Color)ColorConverter.ConvertFromString("#4FB6AC");
        private static readonly Color Arcane = (Color)ColorConverter.ConvertFromString("#9A7BD6");
        private static readonly Color Ink = (Color)ColorConverter.ConvertFromString("#ECE4D0");
        private static readonly Color Mute = (Color)ColorConverter.ConvertFromString("#8B859C");
        private static readonly Color Slate = (Color)ColorConverter.ConvertFromString("#15151F");
        private static readonly Color Line = (Color)ColorConverter.ConvertFromString("#322E44");

        #endregion

        #region === СЛУЖЕБНЫЕ ТИПЫ ===

        private class Ctx
        {
            public string BaseFolder;
        }

        /// <summary>Ссылка на заметку внутри документа.</summary>
        public static readonly DependencyProperty WikiTargetProperty =
            DependencyProperty.RegisterAttached("WikiTarget", typeof(string), typeof(MarkdownParser),
                new PropertyMetadata(null));

        /// <summary>Относительный путь картинки/вложения в Markdown.</summary>
        public static readonly DependencyProperty ImageSourceProperty =
            DependencyProperty.RegisterAttached("ImageSource", typeof(string), typeof(MarkdownParser),
                new PropertyMetadata(null));

        /// <summary>Ширина картинки в пикселях: 0 — не задана, -1 — оригинальный размер.</summary>
        public static readonly DependencyProperty ImageWidthProperty =
            DependencyProperty.RegisterAttached("ImageWidth", typeof(int), typeof(MarkdownParser),
                new PropertyMetadata(0));

        /// <summary>Контейнер картинки — по нему находим изображение при клике.</summary>
        public static readonly DependencyProperty ImageContainerProperty =
            DependencyProperty.RegisterAttached("ImageContainer", typeof(InlineUIContainer), typeof(MarkdownParser),
                new PropertyMetadata(null));

        public static void SetWikiTarget(DependencyObject target, string value) => target?.SetValue(WikiTargetProperty, value);
        public static string GetWikiTarget(DependencyObject target) => target?.GetValue(WikiTargetProperty) as string;

        public static void SetImageSource(DependencyObject target, string value) => target?.SetValue(ImageSourceProperty, value);
        public static string GetImageSource(DependencyObject target) => target?.GetValue(ImageSourceProperty) as string;

        public static void SetImageWidth(DependencyObject target, int value) => target?.SetValue(ImageWidthProperty, value);
        public static int GetImageWidth(DependencyObject target) => target == null ? 0 : (int)target.GetValue(ImageWidthProperty);

        public static void SetImageContainer(DependencyObject target, InlineUIContainer value) => target?.SetValue(ImageContainerProperty, value);
        public static InlineUIContainer GetImageContainer(DependencyObject target) => target?.GetValue(ImageContainerProperty) as InlineUIContainer;

        private const string TaskUnchecked = "☐";
        private const string TaskChecked = "☑";

        #endregion

        #region === Markdown → FlowDocument ===

        /// <summary>
        /// Разбирает Markdown в FlowDocument. baseFolder — корень, относительно которого
        /// разрешаются пути картинок (обычно папка CharacterPhoto).
        /// </summary>
        public static void LoadInto(FlowDocument doc, string markdown, string baseFolder)
        {
            var ctx = new Ctx { BaseFolder = baseFolder ?? "" };

            doc.Blocks.Clear();

            if (string.IsNullOrEmpty(markdown))
                return;

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var i = 0;

            while (i < lines.Length)
            {
                var line = lines[i];

                // Пустая строка
                if (string.IsNullOrWhiteSpace(line))
                {
                    i++;
                    continue;
                }

                // --- Горизонтальная линия ---
                if (IsHorizontalRule(line))
                {
                    doc.Blocks.Add(new Paragraph
                    {
                        BorderBrush = new SolidColorBrush(Line),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(0, 10, 0, 10),
                        FontSize = 1,
                        LineHeight = 1
                    });
                    i++;
                    continue;
                }

                // --- Блок кода ``` ---
                if (line.TrimStart().StartsWith("```"))
                {
                    doc.Blocks.Add(ParseCodeBlock(lines, ref i));
                    continue;
                }

                // --- Заголовок ---
                var heading = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (heading.Success)
                {
                    doc.Blocks.Add(CreateHeading(heading.Groups[2].Value, heading.Groups[1].Value.Length, ctx));
                    i++;
                    continue;
                }

                // --- Цитата ---
                if (line.TrimStart().StartsWith(">"))
                {
                    doc.Blocks.Add(ParseBlockquote(line, ctx));
                    i++;
                    continue;
                }

                // --- Таблица ---
                if (line.Contains("|") && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    doc.Blocks.Add(ParseTable(lines, ref i, ctx));
                    continue;
                }

                // --- Списки ---
                // Пробел после маркера не обязателен: «1.текст» тоже пункт списка
                if (IsBullet(line) || IsNumbered(line))
                {
                    doc.Blocks.Add(ParseList(lines, ref i, ctx));
                    continue;
                }

                // --- Абзац ---
                doc.Blocks.Add(CreateParagraph(line, ctx));
                i++;
            }
        }

        /// <summary>Совместимость: разобрать Markdown в новый FlowDocument.</summary>
        public static FlowDocument Parse(string markdown, string baseFolder = null)
        {
            var doc = CreateDocument();
            LoadInto(doc, markdown, baseFolder);
            return doc;
        }

        private static FlowDocument CreateDocument()
        {
            return new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Ink),
                PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity
            };
        }

        private static bool IsHorizontalRule(string line)
        {
            var t = line.Trim();
            return Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$");
        }

        private static bool IsTableSeparator(string line)
        {
            var t = line.Trim().Trim('|').Trim();
            return t.Length > 0 && Regex.IsMatch(t, @"^[:\-\s|]+$") && t.Contains("-");
        }

        private static Paragraph CreateHeading(string text, int level, Ctx ctx)
        {
            var para = new Paragraph { Margin = new Thickness(0, 14, 0, 6) };
            ParseInlines(text, para.Inlines, ctx);

            switch (level)
            {
                case 1:
                    para.FontSize = 26;
                    para.FontWeight = FontWeights.Bold;
                    para.Foreground = new SolidColorBrush(Frost);
                    break;
                case 2:
                    para.FontSize = 21;
                    para.FontWeight = FontWeights.Bold;
                    para.Foreground = new SolidColorBrush(Ink);
                    break;
                case 3:
                    para.FontSize = 17;
                    para.FontWeight = FontWeights.SemiBold;
                    para.Foreground = new SolidColorBrush(Arcane);
                    break;
                default:
                    para.FontSize = 15;
                    para.FontWeight = FontWeights.SemiBold;
                    para.Foreground = new SolidColorBrush(Ink);
                    break;
            }
            return para;
        }

        private static Paragraph CreateParagraph(string line, Ctx ctx)
        {
            var para = new Paragraph { Margin = new Thickness(0, 4, 0, 4) };
            ParseInlines(line, para.Inlines, ctx);
            return para;
        }

        private static Section ParseBlockquote(string line, Ctx ctx)
        {
            var text = Regex.Replace(line.TrimStart(), @"^>\s?", "");
            var para = new Paragraph { Margin = new Thickness(0), Foreground = new SolidColorBrush(Mute) };
            ParseInlines(text, para.Inlines, ctx);

            return new Section(para)
            {
                BorderBrush = new SolidColorBrush(Frost),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(12, 2, 4, 2),
                Margin = new Thickness(0, 6, 0, 6)
            };
        }

        private static Paragraph ParseCodeBlock(string[] lines, ref int i)
        {
            i++; // пропускаем открывающий ```
            var sb = new StringBuilder();
            while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
            {
                sb.AppendLine(lines[i]);
                i++;
            }
            if (i < lines.Length) i++; // закрывающий ```

            return new Paragraph(new Run(sb.ToString().TrimEnd('\n')))
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Ink),
                Background = new SolidColorBrush(Slate),
                BorderBrush = new SolidColorBrush(Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 6, 0, 6)
            };
        }

        /// <summary>
        /// Строка начинается с маркера списка.
        ///
        /// Пробел после маркера НЕ обязателен: люди часто пишут «1.текст» или
        /// «-текст» без него. Раньше такие строки списком не считались и
        /// рисовались обычным текстом — отсюда «номера стоят криво» и
        /// «список обрывается на втором пункте».
        ///
        /// Требуем лишь, чтобы после маркера шёл хоть какой-то текст, иначе
        /// одиночная точка или дефис превращались бы в пустой пункт.
        /// </summary>
        private static readonly Regex BulletMarker =
            new Regex(@"^\s*[-*+]\s*(?=\S)", RegexOptions.Compiled);

        private static readonly Regex NumberMarker =
            new Regex(@"^\s*\d+[.)]\s*(?=\S)", RegexOptions.Compiled);

        /// <summary>Снимает маркер в начале строки.</summary>
        private static readonly Regex MarkerStrip =
            new Regex(@"^\s*(?:[-*+]|\d+[.)])\s*", RegexOptions.Compiled);

        private static bool IsBullet(string line) => BulletMarker.IsMatch(line);
        private static bool IsNumbered(string line) => NumberMarker.IsMatch(line);

        private static List ParseList(string[] lines, ref int i, Ctx ctx)
        {
            var firstLine = lines[i];
            bool ordered = IsNumbered(firstLine);
            bool taskList = Regex.IsMatch(firstLine, @"^\s*[-*+]\s*\[[ xX]\]");

            var list = new List
            {
                MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                // Отступы как в Word: список сдвинут вправо, но не «плывёт»
                Margin = new Thickness(24, 6, 0, 6),
                Padding = new Thickness(0),
                MarkerOffset = 12
            };

            while (i < lines.Length)
            {
                var line = lines[i];
                bool bullet = IsBullet(line);
                bool numbered = IsNumbered(line);

                if (!bullet && !numbered) break;

                var text = MarkerStrip.Replace(line, "");

                // Строка только из маркера — это не пункт списка, а разделитель
                if (string.IsNullOrWhiteSpace(text)) break;

                if (taskList && bullet)
                {
                    bool done = Regex.IsMatch(text, @"^\[[xX]\]");
                    text = Regex.Replace(text, @"^\[[ xX]\]\s?", "");
                    text = (done ? TaskChecked : TaskUnchecked) + " " + text;
                    list.MarkerStyle = TextMarkerStyle.None;
                }

                var item = new ListItem();

                // Отступов у абзаца внутри пункта быть не должно: они смещают
                // текст относительно маркера, и номера встают неровно
                var para = new Paragraph { Margin = new Thickness(0) };
                ParseInlines(text, para.Inlines, ctx);
                i++;

                // ── Продолжение пункта ──
                //
                // Строки с отступом, не начинающиеся новым маркером, относятся
                // к текущему пункту: так длинный текст не разрывает список.
                // Проверяем это ЗДЕСЬ, внутри обработки пункта: раньше проверка
                // стояла после основного цикла, и первая же такая строка
                // обрывала список, а следующий пункт терялся.
                while (i < lines.Length && lines[i].StartsWith("  ") &&
                       !IsBullet(lines[i]) && !IsNumbered(lines[i]))
                {
                    var continuation = lines[i].Trim();
                    if (continuation.Length == 0) break;

                    para.Inlines.Add(new LineBreak());
                    ParseInlines(continuation, para.Inlines, ctx);
                    i++;
                }

                item.Blocks.Add(para);
                list.ListItems.Add(item);
            }

            return list;
        }

        private static Table ParseTable(string[] lines, ref int i, Ctx ctx)
        {
            var header = SplitTableRow(lines[i]);
            var bodyRows = new List<List<string>>();
            i += 2; // строка заголовка + разделитель

            while (i < lines.Length && lines[i].Contains("|") && !string.IsNullOrWhiteSpace(lines[i]))
            {
                bodyRows.Add(SplitTableRow(lines[i]));
                i++;
            }

            var table = CreateStyledTable(1 + bodyRows.Count, header.Count);

            var group = table.RowGroups.FirstOrDefault();
            if (group == null || group.Rows.Count == 0) return table;

            // Заголовок
            FillRow(group.Rows[0], header, ctx, true);

            // Данные
            for (int r = 0; r < bodyRows.Count && r + 1 < group.Rows.Count; r++)
                FillRow(group.Rows[r + 1], bodyRows[r], ctx, false);

            return table;
        }

        private static void FillRow(TableRow row, List<string> values, Ctx ctx, bool isHeader)
        {
            for (int c = 0; c < row.Cells.Count; c++)
            {
                var text = c < values.Count ? values[c] : "";
                var cell = row.Cells[c];
                var paragraph = cell.Blocks.FirstBlock as Paragraph;
                if (paragraph == null) continue;

                paragraph.Inlines.Clear();
                if (isHeader)
                {
                    paragraph.FontWeight = FontWeights.Bold;
                    paragraph.Inlines.Add(new Run(text));
                }
                else
                {
                    ParseInlines(text, paragraph.Inlines, ctx);
                }
            }
        }

        /// <summary>
        /// Создаёт таблицу в оформлении заметок: рамка, фон заголовка, отступы,
        /// равные колонки. Используется и парсером, и редактором.
        /// </summary>
        public static Table CreateStyledTable(int rows, int cols)
        {
            if (rows < 1) rows = 1;
            if (cols < 1) cols = 1;

            var table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 10, 0, 10),
                BorderBrush = new SolidColorBrush(Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0)
            };

            for (int c = 0; c < cols; c++)
                table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

            var group = new TableRowGroup();
            for (int r = 0; r < rows; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < cols; c++)
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 20 };
                    if (r == 0) paragraph.FontWeight = FontWeights.Bold;

                    row.Cells.Add(new TableCell(paragraph)
                    {
                        Padding = new Thickness(10, 6, 10, 6),
                        BorderBrush = new SolidColorBrush(Line),
                        BorderThickness = new Thickness(0, 0, 1, 0),
                        Background = new SolidColorBrush(r == 0 ? Slate : Color.FromRgb(0x15, 0x1B, 0x2E))
                    });
                }
                group.Rows.Add(row);
            }
            table.RowGroups.Add(group);

            return table;
        }

        private static List<string> SplitTableRow(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            return t.Split('|').Select(p => p.Trim()).ToList();
        }

        #endregion

        #region === Инлайн-разметка ===

        private static readonly Regex InlinePattern = new Regex(
            @"(`[^`\n]+`)"
            + @"|(!\[[^\]]*\]\([^\)]*\))"
            + @"|(\[[^\]\n]+\]\([a-zA-Z][a-zA-Z0-9+.\-]*:[^\)\s]*\))"
            + @"|(\[\[[^\]\n]+\]\])"
            + @"|(\*\*\*.+?\*\*\*)"
            + @"|(\*\*.+?\*\*)"
            + @"|(__.+?__)"
            + @"|(\*[^*\n]+?\*)"
            + @"|(_[^_\n]+?_)"
            + @"|(~~.+?~~)"
            + @"|(==.+?==)"
            + @"|(<u>.+?</u>)"
            + @"|(\$[^\$\n]+\$)",
            RegexOptions.Compiled);

        /// <summary>Ссылка вида [текст](адрес): текст и адрес отдельно.</summary>
        private static readonly Regex MarkdownLink = new Regex(
            @"^\[(?<text>[^\]\n]+)\]\((?<url>[a-zA-Z][a-zA-Z0-9+.\-]*:[^\)\s]*)\)$",
            RegexOptions.Compiled);

        /// <summary>Адрес ссылки в тексте заметки (отдельно от wiki-ссылок).</summary>
        public static readonly DependencyProperty LinkUrlProperty =
            DependencyProperty.RegisterAttached("LinkUrl", typeof(string), typeof(MarkdownParser),
                new PropertyMetadata(null));

        public static void SetLinkUrl(DependencyObject target, string value) => target?.SetValue(LinkUrlProperty, value);
        public static string GetLinkUrl(DependencyObject target) => target?.GetValue(LinkUrlProperty) as string;

        private static void ParseInlines(string text, InlineCollection inlines, Ctx ctx)
        {
            if (string.IsNullOrEmpty(text)) return;

            int pos = 0;
            foreach (Match m in InlinePattern.Matches(text))
            {
                if (m.Index > pos)
                    inlines.Add(new Run(Unescape(text.Substring(pos, m.Index - pos))));

                AddInlineToken(m.Value, inlines, ctx);
                pos = m.Index + m.Length;
            }

            if (pos < text.Length)
                inlines.Add(new Run(Unescape(text.Substring(pos))));
        }

        private static void AddInlineToken(string token, InlineCollection inlines, Ctx ctx)
        {
            // Код
            if (token.StartsWith("`") && token.EndsWith("`") && token.Length > 2)
            {
                inlines.Add(new Run(token.Substring(1, token.Length - 2))
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = new SolidColorBrush(Slate),
                    Foreground = new SolidColorBrush(Frost)
                });
                return;
            }

            // Картинка
            if (token.StartsWith("!["))
            {
                var img = CreateImage(token, ctx);
                if (img != null) inlines.Add(img);
                return;
            }

            // Обычная ссылка [текст](адрес) — как в Obsidian и Word: фиолетовая
            // подчёркнутая ссылка, по клику открывается в браузере.
            var mdLink = MarkdownLink.Match(token);
            if (mdLink.Success)
            {
                string label = mdLink.Groups["text"].Value;
                string url = mdLink.Groups["url"].Value;

                var webLink = new Hyperlink(new Run(label))
                {
                    Foreground = new SolidColorBrush(Arcane),
                    TextDecorations = TextDecorations.Underline,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = url
                };

                try { webLink.NavigateUri = new Uri(url); } catch { }
                SetLinkUrl(webLink, url);
                inlines.Add(webLink);
                return;
            }

            // Wiki-ссылка
            if (token.StartsWith("[[") && token.EndsWith("]]"))
            {
                var inner = token.Substring(2, token.Length - 4);
                string target = inner, alias = inner;
                int bar = inner.IndexOf('|');
                if (bar >= 0)
                {
                    target = inner.Substring(0, bar).Trim();
                    alias = inner.Substring(bar + 1).Trim();
                }

                var link = new Hyperlink(new Run(alias))
                {
                    Foreground = new SolidColorBrush(Arcane),
                    TextDecorations = TextDecorations.Underline,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Перейти к заметке «" + target + "»"
                };
                SetWikiTarget(link, target);
                inlines.Add(link);
                return;            }

            // Жирный курсив
            if (token.StartsWith("***") && token.EndsWith("***"))
            {
                var italic = new Italic(new Run(token.Substring(3, token.Length - 6)));
                inlines.Add(new Bold(italic));
                return;
            }

            // Жирный
            if ((token.StartsWith("**") && token.EndsWith("**")) ||
                (token.StartsWith("__") && token.EndsWith("__")))
            {
                inlines.Add(new Bold(new Run(token.Substring(2, token.Length - 4))));
                return;
            }

            // Курсив
            if ((token.StartsWith("*") && token.EndsWith("*")) ||
                (token.StartsWith("_") && token.EndsWith("_")))
            {
                inlines.Add(new Italic(new Run(token.Substring(1, token.Length - 2))));
                return;
            }

            // Зачёркнутый
            if (token.StartsWith("~~") && token.EndsWith("~~"))
            {
                var span = new Span(new Run(token.Substring(2, token.Length - 4)));
                span.TextDecorations = TextDecorations.Strikethrough;
                inlines.Add(span);
                return;
            }

            // Выделение
            if (token.StartsWith("==") && token.EndsWith("=="))
            {
                inlines.Add(new Span(new Run(token.Substring(2, token.Length - 4)))
                {
                    Background = new SolidColorBrush(Accent()),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x09, 0x0C, 0x16))
                });
                return;
            }

            // Подчёркнутый
            if (token.StartsWith("<u>") && token.EndsWith("</u>"))
            {
                inlines.Add(new Span(new Run(token.Substring(3, token.Length - 7)))
                {
                    TextDecorations = TextDecorations.Underline
                });
                return;
            }

            // Формула
            if (token.StartsWith("$") && token.EndsWith("$") && token.Length > 2)
            {
                inlines.Add(new Run(token.Substring(1, token.Length - 2))
                {
                    FontFamily = new FontFamily("Cambria Math, Consolas"),
                    Foreground = new SolidColorBrush(Accent())
                });
                return;
            }

            inlines.Add(new Run(Unescape(token)));
        }

        private static Color Accent() => PaletteBrush.GetColor("C.Accent", Color.FromRgb(0x5F, 0xD4, 0xDD));

        private static string Unescape(string text)
        {
            return text.Replace("\\*", "*").Replace("\\_", "_").Replace("\\`", "`");
        }

        #endregion

        #region === Картинки ===

        private static InlineUIContainer CreateImage(string token, Ctx ctx)
        {
            try
            {
                var m = Regex.Match(token, @"^!\[(?<alt>[^\]]*)\]\((?<src>[^\)]*)\)$");
                if (!m.Success) return null;

                var alt = m.Groups["alt"].Value;
                var src = m.Groups["src"].Value.Trim().Trim('"', '\'');

                int width = 0;
                int bar = alt.IndexOf('|');
                if (bar >= 0 && int.TryParse(alt.Substring(bar + 1).Trim(), out var w))
                {
                    width = w;
                    alt = alt.Substring(0, bar);
                }
                else if (src.Contains("|"))
                {
                    var parts = src.Split('|');
                    src = parts[0].Trim();
                    if (int.TryParse(parts[1].Trim(), out var w2)) width = w2;
                }

                if (string.IsNullOrWhiteSpace(src)) return null;

                var resolved = ResolvePath(src, ctx?.BaseFolder);
                var image = new Image
                {
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(2, 4, 2, 4),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Клик — изменить размер"
                };

                var source = LoadImage(resolved, width);
                if (source == null)
                {
                    image.Width = 220;
                    image.Height = 100;
                    image.ToolTip = "Файл не найден: " + src;
                }
                else if (source is BitmapSource bitmap)
                {
                    image.Source = bitmap;
                    image.Width = width > 0 ? width : Math.Min(bitmap.PixelWidth, 700);
                }

                SetImageSource(image, src);
                SetImageWidth(image, width == 0 ? -1 : width);

                var container = new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
                SetImageSource(container, src);
                SetImageContainer(image, container);
                return container;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownParser] Картинка не разобрана: {ex.Message}");
                return null;
            }
        }

        /// <summary>Загружает картинку (GIF — через GifBitmapDecoder).</summary>
        public static ImageSource LoadImage(string fullPath, int width)
        {
            if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath))
                return null;

            try
            {
                var ext = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
                if (ext == ".gif")
                {
                    var decoder = new GifBitmapDecoder(
                        new Uri(fullPath, UriKind.Absolute),
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                        return decoder.Frames[0];
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(fullPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownParser] Ошибка загрузки картинки: {ex.Message}");
                return null;
            }
        }

        /// <summary>Превращает относительный путь (NoteImages/...) в абсолютный.</summary>
        public static string ResolvePath(string path, string baseFolder)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (System.IO.Path.IsPathRooted(path)) return path;

            var root = string.IsNullOrEmpty(baseFolder) ? DatabaseHelper.GetPhotoBasePath() : baseFolder;

            var normalized = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (normalized.StartsWith("CharacterPhoto" + System.IO.Path.DirectorySeparatorChar))
                normalized = normalized.Substring("CharacterPhoto".Length + 1);

            return System.IO.Path.Combine(root, normalized);
        }

        #endregion

        #region === FlowDocument → Markdown ===

        public static string ToMarkdown(FlowDocument doc)
        {
            if (doc == null) return "";
            var sb = new StringBuilder();
            foreach (Block block in doc.Blocks)
                SerializeBlock(block, sb, 0);
            return CollapseBlankLines(sb.ToString()).Trim();
        }

        private static string CollapseBlankLines(string text)
        {
            return Regex.Replace(text, @"(\r?\n){3,}", "\n\n");
        }

        private static void SerializeBlock(Block block, StringBuilder sb, int indent)
        {
            var pad = new string(' ', indent);

            if (block is Paragraph para)
            {
                // Горизонтальная линия
                if (para.FontSize <= 1 && para.BorderThickness.Bottom > 0)
                {
                    sb.AppendLine("---").AppendLine();
                    return;
                }

                // Блок кода
                if (para.FontFamily?.Source != null && para.FontFamily.Source.Contains("Consolas") &&
                    para.Background is SolidColorBrush)
                {
                    sb.AppendLine("```");
                    sb.AppendLine(GetText(para.Inlines));
                    sb.AppendLine("```").AppendLine();
                    return;
                }

                if (para.FontSize >= 24) { sb.AppendLine(pad + "# " + GetText(para.Inlines)); sb.AppendLine(); return; }
                if (para.FontSize >= 19) { sb.AppendLine(pad + "## " + GetText(para.Inlines)); sb.AppendLine(); return; }
                if (para.FontSize >= 16 && para.FontWeight != FontWeights.Normal)
                { sb.AppendLine(pad + "### " + GetText(para.Inlines)); sb.AppendLine(); return; }

                // Абзац только из картинок
                if (para.Inlines.Count > 0 && para.Inlines.All(IsImageInline))
                {
                    foreach (var inline in para.Inlines.ToList())
                        sb.AppendLine(pad + SerializeImageInline(inline));
                    sb.AppendLine();
                    return;
                }

                sb.AppendLine(pad + SerializeInlines(para.Inlines));
                sb.AppendLine();
                return;
            }

            if (block is Section section)
            {
                foreach (var child in section.Blocks.OfType<Paragraph>())
                    sb.AppendLine(pad + "> " + SerializeInlines(child.Inlines));
                sb.AppendLine();
                return;
            }

            if (block is List list)
            {
                bool ordered = list.MarkerStyle == TextMarkerStyle.Decimal;
                bool task = list.MarkerStyle == TextMarkerStyle.None;
                int index = 1;

                foreach (ListItem item in list.ListItems)
                {
                    var text = GetText(item.Blocks);
                    string prefix;

                    if (task)
                    {
                        bool done = text.StartsWith(TaskChecked);
                        var body = text.Length > 0 ? text.Substring(1).TrimStart() : "";
                        prefix = "- [" + (done ? "x" : " ") + "] " + body;
                    }
                    else
                    {
                        prefix = ordered ? index + ". " + text : "- " + text;
                    }

                    sb.AppendLine(pad + prefix);
                    index++;
                }

                sb.AppendLine();
                return;
            }

            if (block is Table table)
            {
                var rows = new List<List<string>>();
                foreach (TableRowGroup group in table.RowGroups)
                    foreach (TableRow row in group.Rows)
                        rows.Add(row.Cells.Select(c => GetHeaderText(c.Blocks).Replace("\n", " ").Trim()).ToList());

                if (rows.Count > 0)
                {
                    sb.AppendLine(pad + "| " + string.Join(" | ", rows[0]) + " |");
                    sb.AppendLine(pad + "|" + string.Join("|", rows[0].Select(_ => " --- ")) + "|");
                    for (int r = 1; r < rows.Count; r++)
                        sb.AppendLine(pad + "| " + string.Join(" | ", rows[r]) + " |");
                    sb.AppendLine();
                }
                return;
            }
        }

        private static bool IsImageInline(Inline inline)
        {
            if (inline is InlineUIContainer c && c.Child is Image) return true;
            if (inline is Run r) return Regex.IsMatch(r.Text ?? "", @"^\s*!\[[^\]]*\]\([^\)]*\)\s*$");
            return false;
        }

        private static string SerializeImageInline(Inline inline)
        {
            if (inline is InlineUIContainer container && container.Child is Image image)
            {
                var src = GetImageSource(container) ?? GetImageSource(image) ?? "";

                int width = GetImageWidth(container);
                if (width == 0 && GetImageWidth(image) != 0) width = GetImageWidth(image);
                if (width == 0 && !double.IsNaN(image.Width) && image.Width > 0)
                    width = (int)image.Width;

                var alt = width > 0 ? "image|" + width : "image";
                return $"![{alt}]({src})";
            }

            if (inline is Run run)
                return run.Text?.Trim() ?? "";

            return "";
        }

        private static string SerializeInlines(InlineCollection inlines)
        {
            var sb = new StringBuilder();
            foreach (var inline in inlines)
                sb.Append(SerializeInline(inline));
            return sb.ToString();
        }

        private static string SerializeInline(Inline inline)
        {
            if (inline is Run run)
                return run.Text ?? "";

            if (inline is InlineUIContainer container && container.Child is Image)
                return SerializeImageInline(container);

            if (inline is Hyperlink link)
            {
                // Обычная ссылка [текст](адрес): храним её как есть, чтобы файл
                // заметки оставался обычным Markdown
                var url = GetLinkUrl(link);
                if (!string.IsNullOrEmpty(url))
                {
                    var label = GetText(link.Inlines);
                    // Wiki-ссылки дополнительно помечаются целью: у них цели нет
                    if (string.IsNullOrEmpty(GetWikiTarget(link)))
                        return $"[{label}]({url})";
                }

                var target = GetWikiTarget(link);
                var text = GetText(link.Inlines);
                if (string.IsNullOrEmpty(target))
                    return text;
                return string.Equals(target, text, StringComparison.Ordinal)
                    ? $"[[{target}]]"
                    : $"[[{target}|{text}]]";
            }

            if (inline is Bold bold)
            {
                if (bold.Inlines.Count == 1 && bold.Inlines.FirstInline is Italic)
                    return "***" + GetText(bold.Inlines) + "***";
                return "**" + SerializeInlines(bold.Inlines) + "**";
            }

            if (inline is Italic italic)
                return "*" + SerializeInlines(italic.Inlines) + "*";

            if (inline is Span span)
            {
                var text = SerializeInlines(span.Inlines);
                if (span.TextDecorations == TextDecorations.Strikethrough)
                    return "~~" + text + "~~";
                if (span.TextDecorations == TextDecorations.Underline)
                    return "<u>" + text + "</u>";
                if (span.Background is SolidColorBrush bg && bg.Color == Accent())
                    return "==" + text + "==";
                if (span.FontFamily?.Source != null && span.FontFamily.Source.Contains("Consolas"))
                    return "`" + text + "`";
                return text;
            }

            return "";
        }

        private static string GetText(InlineCollection inlines)
        {
            var sb = new StringBuilder();
            foreach (var inline in inlines)
                sb.Append(SerializeInline(inline));
            return sb.ToString();
        }

        private static string GetText(IEnumerable<Block> blocks)
        {
            var sb = new StringBuilder();
            foreach (var block in blocks)
            {
                if (block is Paragraph para) sb.Append(GetText(para.Inlines));
                else if (block is List list)
                {
                    bool first = true;
                    foreach (ListItem item in list.ListItems)
                    {
                        if (!first) sb.Append(' ');
                        sb.Append(GetText(item.Blocks));
                        first = false;
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>Текст без разметки — для ячеек таблицы (жирный заголовок не нужен).</summary>
        private static string GetHeaderText(IEnumerable<Block> blocks)
        {
            var sb = new StringBuilder();
            foreach (var block in blocks)
            {
                if (block is Paragraph para)
                {
                    foreach (var inline in para.Inlines)
                        sb.Append(ExtractRawText(inline));
                }
            }
            return sb.ToString();
        }

        private static string ExtractRawText(Inline inline)
        {
            if (inline is Run run) return run.Text ?? "";
            if (inline is InlineUIContainer container && container.Child is Image) return SerializeImageInline(container);
            if (inline is Hyperlink link)
            {
                var url = GetLinkUrl(link);
                var label = GetText(link.Inlines);
                if (!string.IsNullOrEmpty(url) && string.IsNullOrEmpty(GetWikiTarget(link)))
                    return $"[{label}]({url})";
                return label;
            }
            if (inline is Bold bold) return GetHeaderInlineText(bold.Inlines);
            if (inline is Italic italic) return GetHeaderInlineText(italic.Inlines);
            if (inline is Span span) return GetHeaderInlineText(span.Inlines);
            return "";
        }

        private static string GetHeaderInlineText(InlineCollection inlines)
        {
            var sb = new StringBuilder();
            foreach (var inline in inlines)
                sb.Append(ExtractRawText(inline));
            return sb.ToString();
        }

        #endregion

        #region === Прочее ===

        /// <summary>Текст документа без разметки — для поиска.</summary>
        public static string ExtractPlainText(FlowDocument doc)
        {
            if (doc == null) return "";
            var range = new TextRange(doc.ContentStart, doc.ContentEnd);
            return range.Text ?? "";
        }

        #endregion
    }
}
