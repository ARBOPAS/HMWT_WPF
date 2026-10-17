using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Мост между нашим форматом заметок и HTML для редактора в WebView.
    ///
    /// Заметки хранятся как текст с разметкой (заголовки «#», списки «-»,
    /// ссылки «[текст](адрес)», картинки «![](путь)», вики-ссылки «[[заметка]]»).
    /// WebView работает с HTML, поэтому нужны два перевода: текст → HTML при
    /// открытии и HTML → текст при сохранении.
    ///
    /// Главное требование — ОБРАТИМОСТЬ: открыл и сохранил, ничего не меняя, —
    /// текст должен остаться прежним. Поэтому всё, что добавляет редактор
    /// (ширина картинки, номера пунктов, вложенность списков), записывается
    /// в разметку так, чтобы старые заметки читались как раньше.
    /// </summary>
    public static class NotesHtmlBridge
    {
        /// <summary>Предел вложенности списков: глубже уже нечитаемо.</summary>
        private const int MaxListDepth = 6;

        /// <summary>Сколько пробелов в начале строки считается одним уровнем списка.</summary>
        private const int IndentPerLevel = 2;

        // ══════════════════════ ТЕКСТ → HTML ══════════════════════

        /// <summary>Переводит текст заметки в HTML для редактора.</summary>
        public static string ToHtml(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "<p><br></p>";

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var html = new StringBuilder();

            // Открытые списки: индекс = уровень вложенности, значение = вид списка
            // ("ul" — маркированный, "ol" — нумерованный, "ulc" — чек-лист).
            var listStack = new List<string>();
            bool inQuote = false;
            bool inTable = false;

            foreach (string raw in lines)
            {
                string line = raw ?? "";
                string trimmed = line.Trim();

                // Отступ считаем, но саму строку не переписываем: табуляции внутри
                // текста — часть заметки, и при сохранении они должны остаться
                int indent = 0;
                foreach (char symbol in line)
                {
                    if (symbol == ' ') indent++;
                    else if (symbol == '\t') indent += IndentPerLevel;
                    else break;
                }

                bool isQuote = trimmed.StartsWith(">", StringComparison.Ordinal);
                bool isPipeRow = trimmed.StartsWith("|", StringComparison.Ordinal);
                bool isTask = Regex.IsMatch(trimmed, @"^[-*]\s+\[[ xX]\]\s*");
                bool isBullet = !isTask && Regex.IsMatch(trimmed, @"^[-*]\s+\S");
                bool isNumber = Regex.IsMatch(trimmed, @"^\d+[.)]\s+\S");
                bool isList = isTask || isBullet || isNumber;

                if (inTable && !isPipeRow)
                {
                    html.AppendLine("</table>");
                    inTable = false;
                }

                if (!isList) CloseAllLists(html, listStack);

                if (inQuote && !isQuote)
                {
                    html.AppendLine("</blockquote>");
                    inQuote = false;
                }

                if (isQuote)
                {
                    if (!inQuote) { html.AppendLine("<blockquote>"); inQuote = true; }
                    html.AppendLine("<p>" + InlineToHtml(trimmed.Substring(1).TrimStart()) + "</p>");
                    continue;
                }

                var heading = Regex.Match(trimmed, @"^(#{1,3})\s+(.*)$");
                if (heading.Success)
                {
                    int headingLevel = heading.Groups[1].Value.Length;
                    html.AppendLine("<h" + headingLevel + ">" + InlineToHtml(heading.Groups[2].Value)
                                    + "</h" + headingLevel + ">");
                    continue;
                }

                // Горизонтальная линия: «---», «***» или «___» отдельной строкой
                if (Regex.IsMatch(trimmed, @"^(-{3,}|\*{3,}|_{3,})$"))
                {
                    html.AppendLine("<hr>");
                    continue;
                }

                if (isPipeRow)
                {
                    if (!inTable)
                    {
                        html.AppendLine("<table>");
                        inTable = true;
                    }

                    // Строка-разделитель «|---|---|» нужна только разметке, в таблице её нет
                    if (!Regex.IsMatch(trimmed, @"^\|[\s:\-|]+\|$"))
                    {
                        html.Append("<tr>");
                        foreach (string cell in SplitPipeRow(trimmed))
                            html.Append("<td>" + InlineToHtml(cell) + "</td>");
                        html.AppendLine("</tr>");
                    }
                    continue;
                }

                if (isList)
                {
                    int level = Math.Min(indent / IndentPerLevel, MaxListDepth);
                    string kind = isTask ? "ulc" : (isBullet ? "ul" : "ol");

                    // Уровень нельзя перепрыгнуть: подпункт без родителя станет обычным пунктом
                    if (level > listStack.Count) level = listStack.Count;

                    AppendListOpen(html, listStack, level, kind);
                    AppendListItem(html, trimmed, isTask);
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    html.AppendLine("<p><br></p>");
                    continue;
                }

                html.AppendLine("<p>" + InlineToHtml(trimmed) + "</p>");
            }

            if (inTable) html.AppendLine("</table>");
            if (inQuote) html.AppendLine("</blockquote>");
            CloseAllLists(html, listStack);

            return html.ToString();
        }

        /// <summary>
        /// Приводит открытые списки к нужному уровню и открывает новый список.
        ///
        /// Пункт списка НЕ закрывается сразу: следующий пункт более глубокого
        /// уровня должен попасть внутрь него — иначе пропадёт вложенность.
        /// </summary>
        private static void AppendListOpen(StringBuilder html, List<string> stack, int level, string kind)
        {
            // Закрываем всё, что глубже нужного уровня
            while (stack.Count > level + 1) CloseOneList(html, stack);

            if (stack.Count == level + 1)
            {
                if (stack[level] == kind)
                {
                    html.Append("</li>");
                }
                else
                {
                    // На этом уровне был список другого вида — его закрываем
                    CloseOneList(html, stack);
                }
            }

            if (stack.Count == level)
            {
                html.Append(kind == "ulc" ? "<ul class=\"check\">" : "<" + kind + ">");
                stack.Add(kind);
            }
        }

        /// <summary>Пишет один пункт списка (без закрывающего тега).</summary>
        private static void AppendListItem(StringBuilder html, string trimmed, bool isTask)
        {
            string attrs = "";
            string text;

            if (isTask)
            {
                bool done = Regex.IsMatch(trimmed, @"^[-*]\s+\[[xX]\]");
                attrs = " class=\"task\" data-check=\"" + (done ? "1" : "0") + "\"";
                text = Regex.Replace(trimmed, @"^[-*]\s+\[[ xX]\]\s*", "");
            }
            else if (Regex.IsMatch(trimmed, @"^\d+[.)]\s+"))
            {
                var number = Regex.Match(trimmed, @"^(\d+)[.)]");
                attrs = " data-n=\"" + number.Groups[1].Value + "\" value=\"" + number.Groups[1].Value + "\"";
                text = Regex.Replace(trimmed, @"^\d+[.)]\s+", "");
            }
            else
            {
                text = Regex.Replace(trimmed, @"^[-*]\s+", "");
            }

            html.Append("<li" + attrs + ">" + InlineToHtml(text));
        }

        private static void CloseOneList(StringBuilder html, List<string> stack)
        {
            if (stack.Count == 0) return;
            string kind = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            html.Append("</li></");
            html.Append(kind == "ol" ? "ol" : "ul");
            html.AppendLine(">");
        }

        private static void CloseAllLists(StringBuilder html, List<string> stack)
        {
            while (stack.Count > 0) CloseOneList(html, stack);
        }

        /// <summary>Делит строку таблицы «| a | b |» на ячейки.</summary>
        private static List<string> SplitPipeRow(string row)
        {
            var cells = new List<string>();
            string body = row.Trim();
            if (body.StartsWith("|", StringComparison.Ordinal)) body = body.Substring(1);
            if (body.EndsWith("|", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);

            foreach (string cell in body.Split('|'))
                cells.Add(cell.Trim());

            return cells;
        }

        /// <summary>Разметка внутри строки: жирный, курсив, зачёркнутый, код, ссылки, картинки.</summary>
        private static string InlineToHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string s = WebUtility.HtmlEncode(text);

            // Вики-ссылка: [[заметка]] или [[заметка|подпись]]. Цель и подпись
            // пишем отдельно — так ссылка с подписью не «ломается» на полпути
            // и ссылка без неё остаётся прежней.
            s = Regex.Replace(s, @"\[\[([^\]]+)\]\]", m =>
            {
                string target = m.Groups[1].Value;
                string label = target;

                int bar = target.IndexOf('|');
                if (bar >= 0)
                {
                    label = target.Substring(bar + 1).Trim();
                    target = target.Substring(0, bar).Trim();
                    if (label.Length == 0) label = target;
                }

                return "<a class=\"wiki\" data-wiki=\"" + target + "\" data-label=\"" + label + "\">"
                       + label + "</a>";
            });

            // Картинка с необязательной шириной: ![](путь) или ![подпись|400](путь).
            // Ширину пишем отдельным атрибутом — так её видит страница редактора,
            // а старые заметки без размера читаются как раньше.
            s = Regex.Replace(s, @"!\[([^\]]*)\]\(([^)]+)\)", m =>
            {
                string alt = m.Groups[1].Value;
                string path = m.Groups[2].Value.Trim();
                int width = 0;

                int bar = alt.IndexOf('|');
                if (bar >= 0 && int.TryParse(alt.Substring(bar + 1).Trim(), out int parsed))
                {
                    width = parsed;
                    alt = alt.Substring(0, bar).Trim();
                }
                else
                {
                    int pathBar = path.LastIndexOf('|');
                    if (pathBar > 0 && int.TryParse(path.Substring(pathBar + 1).Trim(), out int parsedPath))
                    {
                        width = parsedPath;
                        path = path.Substring(0, pathBar).Trim();
                    }
                }

                string extra = width > 0
                    ? " data-width=\"" + width + "\" width=\"" + width + "\" style=\"width:" + width + "px\""
                    : "";

                // loading=lazy: картинки вне экрана не грузятся до прокрутки —
                // большая заметка с гифками открывается быстрее
                return "<img src=\"" + ImageUrl(path) + "\" data-path=\"" + path + "\" alt=\"" + alt +
                       "\" loading=\"lazy\"" + extra + "/>";
            });

            s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)",
                m => "<a class=\"web\" href=\"" + m.Groups[2].Value + "\" data-url=\"" + m.Groups[2].Value + "\">" + m.Groups[1].Value + "</a>");

            s = Regex.Replace(s, @"`([^`]+)`", "<code>$1</code>");

            s = Regex.Replace(s, @"~~([^~]+)~~", "<s>$1</s>");
            s = Regex.Replace(s, @"\*\*([^*]+)\*\*", "<b>$1</b>");
            s = Regex.Replace(s, @"(?<!\*)\*([^*]+)\*(?!\*)", "<i>$1</i>");

            return s;
        }

        /// <summary>Адрес картинки для страницы: локальный файл отдаётся через виртуальный хост.</summary>
        public static string ImageUrl(string relativePath)
        {
            string clean = (relativePath ?? "").Trim().Replace('\\', '/');
            return NotesBridgeConstants.VirtualHostUrl + "/" + clean.Replace(" ", "%20");
        }

        /// <summary>Текст заметки без разметки — для счётчика слов.</summary>
        public static string PlainText(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";
            string s = markdown;
            s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", " ");
            s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
            s = Regex.Replace(s, @"\[\[([^\]]*)\]\]", "$1");
            s = Regex.Replace(s, @"[#*>`~|]", "");
            return s;
        }

        // ══════════════════════ HTML → ТЕКСТ ══════════════════════

        /// <summary>Переводит HTML из редактора обратно в текст заметки.</summary>
        public static string ToMarkdown(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";

            // Списки разбираются отдельно: у них своя вложенность.
            var lists = new List<string>();
            string work = ExtractLists(html, lists);

            var blocks = Regex.Matches(work,
                @"<(?<tag>h[1-3]|p|li|blockquote|div|table)\b[^>]*>(?<inner>.*?)</\k<tag>>" +
                @"|<br\s*/?>|<hr\b[^>]*/?>|\u0001(?<list>\d+)\u0002",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            var result = new List<string>();

            foreach (Match block in blocks)
            {
                if (block.Groups["list"].Success)
                {
                    int index = int.Parse(block.Groups["list"].Value);
                    if (index >= 0 && index < lists.Count)
                        SerializeList(lists[index], 0, result);
                    continue;
                }

                if (block.Value.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add("");
                    continue;
                }

                if (block.Value.StartsWith("<hr", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add("---");
                    continue;
                }

                string tag = block.Groups["tag"].Value.ToLowerInvariant();
                string text = InlineToMarkdown(block.Groups["inner"].Value).Trim();

                switch (tag)
                {
                    case "h1": result.Add("# " + text); break;
                    case "h2": result.Add("## " + text); break;
                    case "h3": result.Add("### " + text); break;
                    case "blockquote": result.Add("> " + text); break;

                    case "table":
                        SerializeTable(block.Groups["inner"].Value, result);
                        break;

                    case "li":
                        // Пункт вне списка — так бывает только в испорченном HTML
                        result.Add("- " + text);
                        break;

                    default:
                        if (text.Length > 0) result.Add(text);
                        else result.Add("");
                        break;
                }
            }

            // Пустые абзацы схлопываем: <p><br></p> иначе растут при каждом сохранении
            var clean = new List<string>();
            foreach (string line in result)
            {
                if (line.Length == 0 && clean.Count > 0 && clean[clean.Count - 1].Length == 0) continue;
                clean.Add(line);
            }

            while (clean.Count > 0 && clean[clean.Count - 1].Length == 0) clean.RemoveAt(clean.Count - 1);
            while (clean.Count > 0 && clean[0].Length == 0) clean.RemoveAt(0);

            return string.Join("\n", clean);
        }

        /// <summary>
        /// Вынимает списки из HTML и ставит на их место метки.
        ///
        /// Так список разбирается отдельно от абзацев, и вложенность не теряется:
        /// метка \u0001N\u0002 заменяет целый список на своём месте.
        /// </summary>
        private static string ExtractLists(string html, List<string> lists)
        {
            var result = new StringBuilder();
            int position = 0;

            while (position < html.Length)
            {
                var match = Regex.Match(html.Substring(position), @"<(ul|ol)\b", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    result.Append(html.Substring(position));
                    break;
                }

                int start = position + match.Index;
                result.Append(html, position, start - position);

                int end = FindListEnd(html, start);
                if (end < 0)
                {
                    result.Append(html.Substring(start));
                    break;
                }

                lists.Add(html.Substring(start, end - start));
                result.Append('\u0001').Append(lists.Count - 1).Append('\u0002');
                position = end;
            }

            return result.ToString();
        }

        /// <summary>Индекс сразу после закрывающего тега списка (с учётом вложенности).</summary>
        private static int FindListEnd(string html, int start)
        {
            var tagRegex = new Regex(@"<(/?)(ul|ol)\b[^>]*>", RegexOptions.IgnoreCase);
            int depth = 0;
            int position = start;

            while (position < html.Length)
            {
                var match = tagRegex.Match(html, position);
                if (!match.Success) return -1;

                if (match.Groups[1].Value == "/")
                {
                    depth--;
                    if (depth <= 0) return match.Index + match.Length;
                }
                else
                {
                    depth++;
                }

                position = match.Index + match.Length;
            }

            return -1;
        }

        /// <summary>Индекс начала закрывающего тега </li> для пункта, открытого в начале (или с from).</summary>
        private static int FindListItemEnd(string html, int itemStart)
        {
            var tagRegex = new Regex(@"<(/?)(li)\b[^>]*>", RegexOptions.IgnoreCase);
            int depth = 0;
            int position = itemStart;

            while (position < html.Length)
            {
                var match = tagRegex.Match(html, position);
                if (!match.Success) return -1;

                if (match.Groups[1].Value == "/")
                {
                    depth--;
                    if (depth <= 0) return match.Index;
                }
                else
                {
                    depth++;
                }

                position = match.Index + match.Length;
            }

            return -1;
        }

        /// <summary>Разбирает список с любой вложенностью и дописывает строки разметки.</summary>
        private static void SerializeList(string listHtml, int depth, List<string> into)
        {
            if (string.IsNullOrEmpty(listHtml) || depth > MaxListDepth) return;

            var open = Regex.Match(listHtml, @"^<(ul|ol)\b([^>]*)>", RegexOptions.IgnoreCase);
            if (!open.Success) return;

            string kind = open.Groups[1].Value.ToLowerInvariant();
            string attrs = open.Groups[2].Value;
            bool isCheckList = kind == "ul" && Regex.IsMatch(attrs, @"class\s*=\s*""[^""]*\bcheck\b", RegexOptions.IgnoreCase);

            string body = listHtml.Substring(open.Length);
            body = Regex.Replace(body, @"</(ul|ol)\s*>\s*$", "", RegexOptions.IgnoreCase);

            int position = 0;
            int counter = 0;

            while (position < body.Length)
            {
                var item = Regex.Match(body.Substring(position), @"<li\b([^>]*)>", RegexOptions.IgnoreCase);
                if (!item.Success) break;

                int itemStart = position + item.Index;
                int contentStart = itemStart + item.Length;
                int itemEnd = FindListItemEnd(body, itemStart);
                if (itemEnd < 0) itemEnd = body.Length;

                string itemAttrs = item.Groups[1].Value;
                string content = body.Substring(contentStart, Math.Max(0, itemEnd - contentStart));

                // Вложенные списки вынимаем заранее: в текст пункта они не входят
                var nested = new List<string>();
                string flat = ExtractLists(content, nested);
                flat = Regex.Replace(flat, "\u0001\\d+\u0002", "");
                string text = InlineToMarkdown(flat).Trim();

                string indent = new string(' ', Math.Min(depth, MaxListDepth) * IndentPerLevel);
                string prefix;

                if (Regex.IsMatch(itemAttrs, @"data-check\s*="))
                {
                    bool done = Regex.IsMatch(itemAttrs, @"data-check\s*=\s*""1""");
                    prefix = "- [" + (done ? "x" : " ") + "] ";
                }
                else if (kind == "ol")
                {
                    counter++;
                    var number = Regex.Match(itemAttrs, @"data-n\s*=\s*""(\d+)""");
                    if (!number.Success) number = Regex.Match(itemAttrs, @"value\s*=\s*""(\d+)""");

                    int shown = number.Success ? int.Parse(number.Groups[1].Value) : counter;
                    counter = shown;
                    prefix = shown + ". ";
                }
                else
                {
                    prefix = isCheckList ? "- [ ] " : "- ";
                }

                if (text.Length > 0) into.Add(indent + prefix + text);

                foreach (string nestedList in nested)
                    SerializeList(nestedList, depth + 1, into);

                position = itemEnd + 5;
                if (position > body.Length) break;
            }
        }

        /// <summary>Переводит HTML-таблицу в строки разметки «| a | b |».</summary>
        private static void SerializeTable(string tableHtml, List<string> into)
        {
            var rows = Regex.Matches(tableHtml, @"<tr\b[^>]*>(?<inner>.*?)</tr>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            int columns = 0;
            var lines = new List<string>();

            foreach (Match row in rows)
            {
                var cells = Regex.Matches(row.Groups["inner"].Value, @"<t[dh]\b[^>]*>(?<inner>.*?)</t[dh]>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                var values = new List<string>();
                foreach (Match cell in cells)
                    values.Add(InlineToMarkdown(cell.Groups["inner"].Value).Trim().Replace("|", "/"));

                if (values.Count == 0) continue;

                columns = Math.Max(columns, values.Count);
                lines.Add("| " + string.Join(" | ", values) + " |");
            }

            if (lines.Count == 0) return;

            // Разделитель после первой строки — так таблица снова читается как таблица
            var separator = new StringBuilder("|");
            for (int i = 0; i < Math.Max(columns, 1); i++) separator.Append("---|");

            into.Add(lines[0]);
            into.Add(separator.ToString());
            for (int i = 1; i < lines.Count; i++) into.Add(lines[i]);
        }

        /// <summary>Достаёт значение атрибута из тега.</summary>
        private static string Attribute(string tag, string name)
        {
            var match = Regex.Match(tag, @"\b" + name + @"\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>Разметка внутри строки: обратный перевод того, что делает InlineToHtml.</summary>
        private static string InlineToMarkdown(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            string s = html;

            // Обратно: [[заметка]] или [[заметка|подпись]]
            s = Regex.Replace(s, @"<a\b[^>]*data-wiki=""([^""]*)""[^>]*>(?<label>.*?)</a>", m =>
            {
                string target = WebUtility.HtmlDecode(m.Groups[1].Value);
                string label = Regex.Replace(m.Groups["label"].Value, "<[^>]+>", "");
                label = WebUtility.HtmlDecode(label).Trim();

                return string.Equals(label, target, StringComparison.Ordinal)
                    ? "[[" + target + "]]"
                    : "[[" + target + "|" + label + "]]";
            }, RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // Картинка: путь и ширина, если она задана перетаскиванием за уголок
            s = Regex.Replace(s, @"<img\b[^>]*>", m =>
            {
                string tag = m.Value;
                string path = Attribute(tag, "data-path");
                if (string.IsNullOrEmpty(path)) return "";

                string alt = WebUtility.HtmlDecode(Attribute(tag, "alt") ?? "");
                string widthText = Attribute(tag, "data-width") ?? Attribute(tag, "width");

                int width = 0;
                if (!string.IsNullOrEmpty(widthText)) int.TryParse(widthText.Trim(), out width);

                return "![" + alt + (width > 0 ? "|" + width : "") + "](" + path + ")";
            }, RegexOptions.Singleline | RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<a[^>]*data-url=""([^""]*)""[^>]*>(.*?)</a>", "[$2]($1)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<a[^>]*href=""([^""]*)""[^>]*>(.*?)</a>", "[$2]($1)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<s\b[^>]*>(.*?)</s>", "~~$1~~", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<b\b[^>]*>(.*?)</b>", "**$1**", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<strong\b[^>]*>(.*?)</strong>", "**$1**", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<i\b[^>]*>(.*?)</i>", "*$1*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<em\b[^>]*>(.*?)</em>", "*$1*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<code\b[^>]*>(.*?)</code>", "`$1`", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", "");

            s = WebUtility.HtmlDecode(s);
            s = s.Replace('\u00A0', ' ');

            return s;
        }
    }

    /// <summary>Постоянные имена для редактора заметок в WebView.</summary>
    public static class NotesBridgeConstants
    {
        /// <summary>Виртуальный адрес, по которому странице доступны картинки заметок.</summary>
        public const string VirtualHostName = "notes.local";

        /// <summary>Полный адрес виртуального хоста.</summary>
        public const string VirtualHostUrl = "https://" + VirtualHostName;
    }
}
