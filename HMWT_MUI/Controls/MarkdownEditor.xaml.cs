using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HMWT_MUI.Services;

namespace HMWT_MUI.Controls
{
    /// <summary>
    /// Нативный редактор заметок: RichTextBox + Markdown-разметка.
    /// Картинки и GIF-анимации вставляются прямо в текст, wiki-ссылки [[...]]
    /// кликабельны, содержимое хранится и экспортируется как Markdown.
    /// </summary>
    public partial class MarkdownEditor : UserControl
    {
        #region === ЦВЕТА ===

        private static readonly Color Frost = (Color)ColorConverter.ConvertFromString("#4FB6AC");
        private static readonly Color Arcane = (Color)ColorConverter.ConvertFromString("#9A7BD6");
        private static readonly Color Ink = (Color)ColorConverter.ConvertFromString("#ECE4D0");
        private static readonly Color Slate = (Color)ColorConverter.ConvertFromString("#15151F");
        private static readonly Color LineColor = (Color)ColorConverter.ConvertFromString("#322E44");
        private static readonly Color GoldColor = (Color)ColorConverter.ConvertFromString("#5FD4DD");

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff" };

        #endregion

        #region === СОБЫТИЯ ===

        public event RoutedEventHandler TextChanged;
        public event Action<string> WikiLinkClicked;
        public event EventHandler RequestWikiLink;

        /// <summary>Короткая подсказка для строки состояния хоста.</summary>
        public event Action<string> Hint;

        #endregion

        #region === СОСТОЯНИЕ ===

        private bool _suppressNotification;
        private bool _autoFormatting;
        private Image _selectedImage;
        private int _currentCharId;
        private readonly Dictionary<Image, GifAnimator> _gifAnimators = new Dictionary<Image, GifAnimator>();

        /// <summary>Пауза анимаций на время набора: кадры не мешают вводу.</summary>
        private readonly DispatcherTimer _animationResumeTimer;

        /// <summary>Анимации остановлены из-за набора и ждут возврата.</summary>
        private bool _animationsPaused;

        /// <summary>Идёт попытка вернуть анимации после загрузки документа.</summary>
        private bool _pendingGifRestart;

        /// <summary>
        /// Есть ли в документе анимированные GIF. Пересчитывается лениво:
        /// проверка обходит весь документ, а вызывается она на каждое нажатие
        /// клавиши, поэтому результат запоминается до изменения документа.
        /// </summary>
        private bool? _hasAnimatedImages;

        /// <summary>Сколько держать паузу после последнего нажатия клавиши.</summary>
        private const int TypingPauseMs = 250;

        /// <summary>Предельная ширина картинки в тексте заметки.</summary>
        private const int MaxNoteImageWidth = 700;

        /// <summary>Корень, относительно которого разрешаются пути картинок.</summary>
        public string PhotoRoot { get; set; }

        /// <summary>Папка для сохранения новых картинок (абсолютная).</summary>
        public string ImagesFolder { get; set; }

        /// <summary>Папка для сохранения вложений (абсолютная).</summary>
        public string AttachmentsFolder { get; set; }

        /// <summary>Заголовки всех заметок — для подсказок wiki-ссылок.</summary>
        public List<string> WikiTitles { get; set; } = new List<string>();

        /// <summary>Есть несохранённые изменения.</summary>
        public bool IsDirty { get; private set; }

        /// <summary>Открыта ли заметка.</summary>
        public bool IsNoteOpen { get; private set; }

        #endregion

        public MarkdownEditor()
        {
            InitializeComponent();
            PhotoRoot = DatabaseHelper.GetPhotoBasePath();
            Rtb.IsReadOnly = true;

            // Кадры GIF отрисовываются в интерфейсном потоке. Когда их меняют
            // прямо во время набора, каждый символ конкурирует с отрисовкой
            // кадра, и ввод идёт рывками. Поэтому на время набора анимации
            // останавливаются, а через четверть секунды после последней
            // клавиши запускаются снова.
            _animationResumeTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(TypingPauseMs)
            };
            _animationResumeTimer.Tick += (s, e) =>
            {
                _animationResumeTimer.Stop();
                _animationsPaused = false;
                RestartAnimations();
            };

            Unloaded += Editor_Unloaded;
        }

        private void Editor_Unloaded(object sender, RoutedEventArgs e)
        {
            _animationResumeTimer.Stop();
            _animationsPaused = false;
            StopAllAnimations();
        }

        #region === ЗАГРУЗКА / СОХРАНЕНИЕ ===

        /// <summary>Загружает Markdown в редактор.</summary>
        public void LoadMarkdown(string markdown, int characterId, bool readOnly = false)
        {
            _currentCharId = characterId;
            StopAllAnimations();
            InvalidateDocumentCache();
            IsNoteOpen = true;
            IsDirty = false;
            _selectedImage = null;
            ImagePanel.Visibility = Visibility.Collapsed;

            _suppressNotification = true;
            try
            {
                Rtb.IsUndoEnabled = false;
                MarkdownParser.LoadInto(Rtb.Document, markdown ?? "", PhotoRoot);
                Rtb.Document.PagePadding = new Thickness(0);
                Rtb.Document.ColumnWidth = double.PositiveInfinity;
                Rtb.IsUndoEnabled = true;
                Rtb.IsReadOnly = readOnly;
                Rtb.CaretPosition = Rtb.Document.ContentStart;
            }
            finally
            {
                _suppressNotification = false;
            }

            // Анимации запускаем после раскладки документа
            RestartAnimationsDeferred();
        }

        /// <summary>
        /// Запускает GIF-анимации после того, как документ разложен.
        ///
        /// Одного вызова мало: при первом открытии вкладки «Заметки» редактор
        /// в этот момент ещё не показан, и попытка включить анимацию пропадала —
        /// гифки оживали только после переключения между заметками (там вид уже
        /// отрисован). Поэтому ждём фактической раскладки и, если анимации так и
        /// не появились, пробуем ещё раз.
        /// </summary>
        private void RestartAnimationsDeferred()
        {
            _pendingGifRestart = true;
            ScheduleGifRestart(20);
        }

        private void ScheduleGifRestart(int attemptsLeft)
        {
            if (!_pendingGifRestart || attemptsLeft <= 0) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_pendingGifRestart) return;

                try
                {
                    // IsLoaded гарантирует, что элемент в дереве и раскладка есть
                    if (!IsLoaded || Rtb.ActualWidth <= 0)
                    {
                        ScheduleGifRestart(attemptsLeft - 1);
                        return;
                    }

                    RestartAnimations();

                    // Анимаций всё ещё нет, хотя в документе есть гифки —
                    // значит раскладка не закончилась, пробуем на следующем кадре
                    if (_gifAnimators.Count == 0 && HasAnimatedImages())
                    {
                        WaitForLayoutThenRetry(attemptsLeft - 1);
                        return;
                    }

                    _pendingGifRestart = false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] GIF restart: {ex.Message}");
                    _pendingGifRestart = false;
                }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>Ждёт ближайшую раскладку и пробует запустить анимации снова.</summary>
        private void WaitForLayoutThenRetry(int attemptsLeft)
        {
            if (attemptsLeft <= 0 || !_pendingGifRestart) return;

            EventHandler onLayout = null;
            onLayout = (s, e) =>
            {
                LayoutUpdated -= onLayout;
                ScheduleGifRestart(attemptsLeft);
            };
            LayoutUpdated += onLayout;
        }

        /// <summary>Очищает редактор (заметка не выбрана).</summary>
        public void Clear()
        {
            StopAllAnimations();
            InvalidateDocumentCache();
            IsNoteOpen = false;
            IsDirty = false;
            _selectedImage = null;
            ImagePanel.Visibility = Visibility.Collapsed;

            _suppressNotification = true;
            try
            {
                Rtb.IsUndoEnabled = false;
                Rtb.Document.Blocks.Clear();
                Rtb.IsUndoEnabled = true;
                Rtb.IsReadOnly = true;
            }
            finally
            {
                _suppressNotification = false;
            }
        }

        /// <summary>Текущее содержимое в Markdown.</summary>
        public string GetMarkdown()
        {
            return MarkdownParser.ToMarkdown(Rtb.Document);
        }

        /// <summary>Сбрасывает флаг изменений (после сохранения).</summary>
        public void MarkClean()
        {
            IsDirty = false;
        }

        /// <summary>Помечает содержимое изменённым (например, после импорта).</summary>
        public void MarkDirty()
        {
            IsDirty = true;
        }

        /// <summary>Документ редактора (для подсчёта слов и поиска).</summary>
        public FlowDocument Document => Rtb.Document;

        /// <summary>
        /// Документ пуст: ни текста, ни картинок — и курсор в него не поставлен.
        ///
        /// Обычный «пустой» RichTextBox содержит один пустой абзац, а после
        /// Ctrl+A и Delete остаётся точно такой же документ. Разница только в
        /// том, что во втором случае человек работал в редакторе, и признаком
        /// этого служит фокус: сохранение по таймеру, при переключении вкладки
        /// или закрытии окна происходит, когда клавиатура уже не в редакторе.
        /// </summary>
        public bool HasNoVisibleContent()
        {
            if (!IsDocumentEmpty()) return false;
            return Rtb == null || !Rtb.IsKeyboardFocusWithin;
        }

        /// <summary>Документ изменился: пересчитать признаки при следующей надобности.</summary>
        private void InvalidateDocumentCache()
        {
            _hasAnimatedImages = null;
        }

        /// <summary>
        /// Документ пуст: ни текста, ни картинок. Обычный «пустой» RichTextBox
        /// содержит один пустой абзац, поэтому только проверки блоков мало.
        /// </summary>
        public bool IsDocumentEmpty()
        {
            var doc = Rtb?.Document;
            if (doc == null) return true;

            foreach (var block in doc.Blocks)
            {
                if (block is Paragraph para)
                {
                    if (para.Inlines.Count > 0) return false;
                    continue;
                }

                // Списки, таблицы, цитаты и всё остальное считаем содержимым
                return false;
            }

            return true;
        }

        public void FocusEditor()
        {
            Rtb.Focus();
        }

        #endregion

        #region === ФАЙЛЫ ЗАМЕТКИ ===

        /// <summary>Копирует выбранный файл картинки в хранилище заметок.</summary>
        public string SaveImageFile(string sourcePath, out string relativePath)
        {
            relativePath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;

                var folder = EnsureFolder(ImagesFolder);
                if (string.IsNullOrEmpty(folder)) return null;

                var ext = Path.GetExtension(sourcePath);
                if (string.IsNullOrEmpty(ext)) ext = ".png";

                var name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                           Sanitize(Path.GetFileNameWithoutExtension(sourcePath)) + ext;
                var target = Path.Combine(folder, name);
                File.Copy(sourcePath, target, true);

                relativePath = "NoteImages/" + _currentCharId + "/" + name;
                return target;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] Ошибка сохранения картинки: {ex.Message}");
                return null;
            }
        }

        /// <summary>Копирует вложение (любой файл) в хранилище заметок.</summary>
        public string SaveAttachmentFile(string sourcePath, out string relativePath)
        {
            relativePath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;

                var folder = EnsureFolder(AttachmentsFolder);
                if (string.IsNullOrEmpty(folder)) return null;

                var name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Sanitize(Path.GetFileName(sourcePath));
                var target = Path.Combine(folder, name);
                File.Copy(sourcePath, target, true);

                relativePath = "NoteAttachments/" + _currentCharId + "/" + name;
                return target;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] Ошибка сохранения вложения: {ex.Message}");
                return null;
            }
        }

        private static string EnsureFolder(string folder)
        {
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            return folder;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "file";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }

        #endregion

        #region === КАРТИНКИ / ВЛОЖЕНИЯ ===

        /// <summary>Вставляет картинку (GIF — с анимацией) по абсолютному пути.</summary>
        public void InsertImage(string fullPath, string relativePath)
        {
            if (!IsNoteOpen) return;

            var image = CreateImageElement(fullPath, relativePath);
            var para = new Paragraph(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom })
            {
                Margin = new Thickness(0, 4, 0, 4)
            };

            var doc = Rtb.Document;
            var anchor = GetTopLevelParagraph() ?? doc.Blocks.LastBlock;
            if (anchor != null && ReferenceEquals(anchor.Parent, doc))
                doc.Blocks.InsertAfter(anchor, para);
            else
                doc.Blocks.Add(para);

            var empty = new Paragraph();
            doc.Blocks.InsertAfter(para, empty);
            Rtb.CaretPosition = empty.ContentStart;
            Rtb.Focus();
            InvalidateDocumentCache();
            OnTextChangedInternal();
        }

        /// <summary>Создаёт элемент Image с настроенным источником и анимацией.</summary>
        public Image CreateImageElement(string fullPath, string relativePath)
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(2, 4, 2, 4),
                Cursor = Cursors.Hand,
                ToolTip = "Клик — изменить размер"
            };

            bool isGif = !string.IsNullOrEmpty(fullPath) &&
                         string.Equals(Path.GetExtension(fullPath), ".gif", StringComparison.OrdinalIgnoreCase);

            // Размер гифки узнаём по заголовкам кадров: полноразмерное чтение
            // ради одной цифры занимает секунду и сотни мегабайт.
            int width = 0, height = 0;
            bool known = isGif && GifFrameCache.TryGetPixelSize(fullPath, out width, out height);
            int displayWidth = known ? Math.Min(width, MaxNoteImageWidth) : 0;

            if (isGif)
            {
                // Анимированные гифки рисуются кадрами, и первый кадр приходит
                // из того же набора: отдельно читать картинку не нужно
                var gif = GifFrameCache.Get(fullPath, displayWidth);
                if (gif != null && gif.Frames.Length > 0)
                {
                    image.Source = gif.Frames[0];
                    image.Width = known ? displayWidth : Math.Min(gif.Frames[0].PixelWidth, MaxNoteImageWidth);
                }
            }

            if (image.Source == null)
            {
                var source = MarkdownParser.LoadImage(fullPath, displayWidth);
                if (source is BitmapSource bitmap)
                {
                    displayWidth = (int)Math.Min(bitmap.PixelWidth, MaxNoteImageWidth);
                    image.Source = bitmap;
                    image.Width = displayWidth;
                }
                else
                {
                    image.Width = 220;
                    image.Height = 100;
                    image.ToolTip = "Файл не найден: " + relativePath;
                }
            }

            if (!double.IsNaN(image.Width) && image.Width > 0)
                MarkdownParser.SetImageWidth(image, (int)Math.Round(image.Width));

            MarkdownParser.SetImageSource(image, relativePath);

            if (isGif)
            {
                if (displayWidth <= 0) displayWidth = GetAnimationDisplayWidth(image);
                StartGifAnimation(image, fullPath, displayWidth);
            }

            return image;
        }

        /// <summary>
        /// Ширина, в которой картинка реально видна: по ней разбираются кадры.
        ///
        /// Кадр стоит ширина × высота × 4 байта, и он рисуется в интерфейсном
        /// потоке. Гифка 800×450, показанная в 700 пикселей, при разборе в
        /// исходном размере стоит на четверть больше памяти и в разы больше
        /// процессора на каждую смену кадра — замер на гифке 9,6 МБ: 156 мс
        /// против 16 мс процессорного времени на три секунды анимации.
        /// </summary>
        private int GetAnimationDisplayWidth(Image image)
        {
            double width = 0;

            if (image != null && !double.IsNaN(image.Width) && image.Width > 0)
                width = image.Width;
            else if (Rtb != null && Rtb.ActualWidth > 0)
                width = Rtb.ActualWidth;

            if (width <= 0) return 0;

            return (int)Math.Min(width, MaxNoteImageWidth);
        }

        /// <summary>Запускает GIF-анимацию кадрами в размере показа.</summary>
        public void StartGifAnimation(Image image, string fullPath)
        {
            StartGifAnimation(image, fullPath, GetAnimationDisplayWidth(image));
        }

        public void StartGifAnimation(Image image, string fullPath, int displayWidth)
        {
            StopGifAnimation(image);

            try
            {
                // Кадры разбираются в размере показа: тогда при смене кадра
                // картинку не нужно масштабировать на каждом тике.
                var gif = GifFrameCache.Get(fullPath, displayWidth);
                if (gif == null || gif.Frames.Length == 0) return;

                image.Source = gif.Frames[0];
                if (!gif.IsAnimated) return;

                var animator = new GifAnimator(gif.Frames, gif.Delays, frame =>
                {
                    try { image.Source = frame; } catch { }
                });
                animator.Start();
                _gifAnimators[image] = animator;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] GIF-анимация не запущена: {ex.Message}");
            }
        }

        /// <summary>Останавливает анимацию конкретной картинки.</summary>
        public void StopGifAnimation(Image image)
        {
            if (image != null && _gifAnimators.TryGetValue(image, out var animator))
            {
                try { animator.Stop(); } catch { }
                _gifAnimators.Remove(image);
            }
        }

        /// <summary>Перезапускает все GIF-анимации документа.</summary>
        public void RestartAnimations()
        {
            if (!IsNoteOpen || Rtb.Document == null) return;

            try
            {
                foreach (var image in FindImages().ToList())
                {
                    var rel = MarkdownParser.GetImageSource(image);
                    if (string.IsNullOrEmpty(rel)) continue;
                    if (!rel.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) continue;

                    var full = MarkdownParser.ResolvePath(rel, PhotoRoot);
                    if (full == null || !File.Exists(full)) continue;

                    // Ставим статичный кадр, затем запускаем анимацию.
                    // Размер берём у самой картинки: кадры разбираются ровно
                    // в нём, поэтому повторный запуск не тянет полный размер.
                    int displayWidth = GetAnimationDisplayWidth(image);
                    image.Source = MarkdownParser.LoadImage(full, displayWidth);
                    StartGifAnimation(image, full, displayWidth);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] Ошибка перезапуска GIF: {ex.Message}");
            }
        }

        /// <summary>Останавливает все анимации (при выгрузке вкладки).</summary>
        public void StopAllAnimations()
        {
            _pendingGifRestart = false;
            foreach (var animator in _gifAnimators.Values)
            {
                try { animator.Stop(); } catch { }
            }
            _gifAnimators.Clear();
        }

        /// <summary>Все картинки документа (для сбора используемых файлов).</summary>
        public List<string> GetImagePaths()
        {
            var result = new List<string>();
            foreach (var image in FindImages())
            {
                var rel = MarkdownParser.GetImageSource(image);
                if (!string.IsNullOrEmpty(rel) && !result.Contains(rel)) result.Add(rel);
            }
            return result;
        }

        private IEnumerable<Image> FindImages()
        {
            var found = new List<Image>();
            CollectImages(Rtb.Document.Blocks, found);
            return found;
        }

        private static void CollectImages(BlockCollection blocks, List<Image> sink)
        {
            foreach (var block in blocks)
                CollectImagesFromBlock(block, sink);
        }

        private static void CollectImagesFromBlock(Block block, List<Image> sink)
        {
            if (block is Paragraph para)
            {
                CollectImagesFromInlines(para.Inlines, sink);
            }
            else if (block is Section section)
            {
                CollectImages(section.Blocks, sink);
            }
            else if (block is List list)
            {
                foreach (ListItem item in list.ListItems)
                    CollectImages(item.Blocks, sink);
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup group in table.RowGroups)
                    foreach (TableRow row in group.Rows)
                        foreach (TableCell cell in row.Cells)
                            CollectImages(cell.Blocks, sink);
            }
        }

        private static void CollectImagesFromInlines(InlineCollection inlines, List<Image> sink)
        {
            foreach (var inline in inlines)
            {
                if (inline is InlineUIContainer c && c.Child is Image img) sink.Add(img);
                else if (inline is Span span) CollectImagesFromInlines(span.Inlines, sink);
            }
        }

        #endregion

        #region === ФОРМАТИРОВАНИЕ ===

        public void ApplyBold() { ToggleInlineFormatting(TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal); }
        public void ApplyItalic() { ToggleInlineFormatting(TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal); }
        public void ApplyUnderline() { ToggleInlineFormatting(Inline.TextDecorationsProperty, TextDecorations.Underline, null); }
        public void ApplyStrikethrough() { ToggleInlineFormatting(Inline.TextDecorationsProperty, TextDecorations.Strikethrough, null); }
        public void ApplyHighlight() { ToggleInlineFormatting(TextElement.BackgroundProperty, new SolidColorBrush(GoldColor), null); }

        public void ApplyInlineCode()
        {
            var selection = RequireSelection();
            if (selection == null) return;

            var run = new Run(selection.Text)
            {
                FontFamily = new FontFamily("Consolas"),
                Background = new SolidColorBrush(Slate),
                Foreground = new SolidColorBrush(Frost)
            };
            var paragraph = selection.Start.Paragraph ?? CurrentParagraph;
            selection.Text = "";

            if (paragraph != null)
            {
                paragraph.Inlines.Add(run);
                AfterFormat(run.ContentEnd);
            }
            else
            {
                InsertInlineIntoDocument(run);
            }
        }

        /// <summary>Вставляет инлайн в текущий абзац (создаёт абзац при необходимости).</summary>
        private void InsertInlineIntoDocument(Inline inline)
        {
            var paragraph = CurrentParagraph;
            if (paragraph == null)
            {
                paragraph = new Paragraph();
                Rtb.Document.Blocks.Add(paragraph);
            }

            paragraph.Inlines.Add(inline);
            paragraph.Inlines.Add(new Run(" "));
            Rtb.CaretPosition = inline.ElementEnd;
            OnTextChangedInternal();
            Rtb.Focus();
        }

        private void ToggleInlineFormatting(DependencyProperty property, object on, object off)
        {
            var selection = RequireSelection();
            if (selection == null) return;

            var current = selection.GetPropertyValue(property);
            bool isOn;
            if (on is TextDecorationCollection target)
                isOn = current is TextDecorationCollection tc && tc.Count > 0 && tc.Contains(target[0]);
            else
                isOn = Equals(current, on);

            if (isOn)
            {
                if (off == null) selection.ApplyPropertyValue(property, new TextDecorationCollection());
                else selection.ApplyPropertyValue(property, off);
            }
            else
            {
                selection.ApplyPropertyValue(property, on);
            }

            AfterFormat(selection.End);
        }

        #region === БЛОЧНЫЕ ОПЕРАЦИИ ===

        /// <summary>
        /// Возвращает абзац, которым можно управлять на уровне документа.
        /// Если каретка внутри списка, цитаты или ячейки таблицы, блок сначала
        /// поднимается на верхний уровень, иначе WPF запрещает InsertAfter/Remove.
        /// </summary>
        private Paragraph GetTopLevelParagraph()
        {
            var paragraph = CurrentParagraph;
            if (paragraph == null) return null;

            var doc = Rtb.Document;

            // 1. Абзац напрямую в документе — самый простой случай
            if (ReferenceEquals(paragraph.Parent, doc)) return paragraph;

            // 2. Внутри ячейки таблицы — блочное форматирование не поддерживаем
            if (paragraph.Parent is TableCell)
            {
                Hint?.Invoke("Внутри таблицы блочное форматирование недоступно");
                return null;
            }

            // 3. Внутри цитаты
            if (paragraph.Parent is Section section)
            {
                var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
                var replacement = new Paragraph(new Run(text)) { Margin = new Thickness(0, 4, 0, 4) };
                doc.Blocks.InsertAfter(section, replacement);
                doc.Blocks.Remove(section);
                Rtb.CaretPosition = replacement.ContentEnd;
                return replacement;
            }

            // 4. Внутри списка — выносим текст наружу
            if (paragraph.Parent is ListItem listItem && listItem.Parent is List list)
            {
                var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
                var replacement = new Paragraph(new Run(text)) { Margin = new Thickness(0, 4, 0, 4) };
                doc.Blocks.InsertAfter(list, replacement);
                list.ListItems.Remove(listItem);
                if (list.ListItems.Count == 0) doc.Blocks.Remove(list);
                Rtb.CaretPosition = replacement.ContentEnd;
                return replacement;
            }

            return null;
        }

        /// <summary>Вставляет блок сразу после указанного абзаца.</summary>
        private static bool InsertAfterParagraph(FlowDocument doc, Paragraph anchor, Block block)
        {
            doc.Blocks.InsertAfter(anchor, block);
            return true;
        }

        /// <summary>Гарантирует, что в документе есть хотя бы один абзац.</summary>
        private void EnsureDocumentHasBlock()
        {
            if (Rtb.Document.Blocks.Count == 0)
                Rtb.Document.Blocks.Add(new Paragraph());
        }

        #endregion

        public void ApplyHeading(int level)
        {
            var para = GetTopLevelParagraph();
            if (para == null) return;

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
                    para.ClearValue(TextElement.FontSizeProperty);
                    para.ClearValue(TextElement.FontWeightProperty);
                    para.Foreground = new SolidColorBrush(Ink);
                    break;
            }

            OnTextChangedInternal();
            Rtb.Focus();
        }

        public void ApplyQuote()
        {
            var para = GetTopLevelParagraph();
            if (para == null) return;

            var doc = Rtb.Document;
            if (!ReferenceEquals(para.Parent, doc)) return;

            // Порядок важен: сначала забираем абзац из документа, затем вставляем
            // Section. Если удалить абзац после вставки, WPF уносит весь Section.
            var index = doc.Blocks.ToList().IndexOf(para);
            doc.Blocks.Remove(para);

            var section = new Section(para)
            {
                BorderBrush = new SolidColorBrush(Frost),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(12, 2, 4, 2),
                Margin = new Thickness(0, 6, 0, 6)
            };

            InsertBlockAtTopLevel(section, index >= 0 ? index : (int?)null);
            EnsureDocumentHasBlock();

            // Каретку ставим в абзац, который уже лежит внутри Section
            Rtb.CaretPosition = para.ContentEnd;
            OnTextChangedInternal();
            Rtb.Focus();
        }

        public void ApplyList(bool ordered, bool task = false)
        {
            var para = GetTopLevelParagraph();
            if (para == null) return;

            var doc = Rtb.Document;
            if (!ReferenceEquals(para.Parent, doc)) return;

            var index = doc.Blocks.ToList().IndexOf(para);
            var text = new TextRange(para.ContentStart, para.ContentEnd).Text.Trim();
            doc.Blocks.Remove(para);

            var itemParagraph = new Paragraph { Margin = new Thickness(0) };
            if (task) itemParagraph.Inlines.Add(new Run("☐  "));
            itemParagraph.Inlines.Add(new Run(text));

            var list = new List
            {
                MarkerStyle = task ? TextMarkerStyle.None : (ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc),
                Margin = new Thickness(0, 6, 0, 6),
                Padding = new Thickness(0)
            };
            list.ListItems.Add(new ListItem(itemParagraph));

            InsertBlockAtTopLevel(list, index >= 0 ? index : (int?)null);
            EnsureDocumentHasBlock();

            Rtb.CaretPosition = itemParagraph.ContentEnd;
            OnTextChangedInternal();
            Rtb.Focus();
        }

        /// <summary>
        /// Вставляет блок на верхний уровень документа: по сохранённой позиции,
        /// иначе — сразу после абзаца с кареткой, иначе — в конец.
        /// </summary>
        private void InsertBlockAtTopLevel(Block block, int? preferredIndex = null)
        {
            var doc = Rtb.Document;

            if (preferredIndex.HasValue)
            {
                var index = Math.Max(0, Math.Min(preferredIndex.Value, doc.Blocks.Count));
                if (index >= doc.Blocks.Count)
                {
                    doc.Blocks.Add(block);
                }
                else
                {
                    var reference = doc.Blocks.ElementAt(index);
                    doc.Blocks.InsertBefore(reference, block);
                }
                return;
            }

            var anchor = CurrentParagraph;
            if (anchor != null && ReferenceEquals(anchor.Parent, doc))
            {
                doc.Blocks.InsertAfter(anchor, block);
                return;
            }

            doc.Blocks.Add(block);
        }

        public void InsertHorizontalRule()
        {
            var doc = Rtb.Document;
            var para = new Paragraph
            {
                BorderBrush = new SolidColorBrush(LineColor),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 10, 0, 10),
                FontSize = 1,
                LineHeight = 1
            };

            var anchor = GetTopLevelParagraph();
            if (anchor != null && ReferenceEquals(anchor.Parent, doc))
            {
                doc.Blocks.InsertAfter(anchor, para);
                doc.Blocks.InsertAfter(para, new Paragraph());
            }
            else
            {
                doc.Blocks.Add(para);
                doc.Blocks.Add(new Paragraph());
            }

            Rtb.CaretPosition = doc.Blocks.LastBlock is Paragraph last ? last.ContentStart : Rtb.CaretPosition;
            OnTextChangedInternal();
            Rtb.Focus();
        }

        public void InsertTable(int rows, int cols)
        {
            if (rows < 1) rows = 1;
            if (cols < 1) cols = 1;

            var table = MarkdownParser.CreateStyledTable(rows, cols);

            // Заполняем подсказками, чтобы таблицу можно было сразу набирать
            var group = table.RowGroups.FirstOrDefault();
            if (group != null)
            {
                for (int r = 0; r < group.Rows.Count; r++)
                {
                    var row = group.Rows[r];
                    for (int c = 0; c < row.Cells.Count; c++)
                    {
                        var paragraph = row.Cells[c].Blocks.FirstBlock as Paragraph;
                        if (paragraph == null) continue;
                        paragraph.Inlines.Clear();
                        if (r == 0) paragraph.Inlines.Add(new Run("Столбец " + (c + 1)));
                    }
                }
            }

            var doc = Rtb.Document;
            var anchor = GetTopLevelParagraph();
            var empty = new Paragraph();

            if (anchor != null && ReferenceEquals(anchor.Parent, doc))
            {
                doc.Blocks.InsertAfter(anchor, table);
                doc.Blocks.InsertAfter(table, empty);
            }
            else
            {
                doc.Blocks.Add(table);
                doc.Blocks.Add(empty);
            }

            Rtb.CaretPosition = empty.ContentStart;
            OnTextChangedInternal();
            Rtb.Focus();
        }

        /// <summary>Вставляет wiki-ссылку [[Заголовок]].</summary>
        public void InsertWikiLink(string title, string alias = null)
        {
            if (string.IsNullOrWhiteSpace(title)) return;

            var link = new Hyperlink(new Run(string.IsNullOrEmpty(alias) ? title : alias))
            {
                Foreground = new SolidColorBrush(Arcane),
                TextDecorations = TextDecorations.Underline,
                Cursor = Cursors.Hand,
                ToolTip = "Перейти к заметке «" + title + "»"
            };
            MarkdownParser.SetWikiTarget(link, title);

            var selection = Rtb.Selection;
            if (!selection.IsEmpty) selection.Text = "";

            InsertInlineIntoDocument(link);
        }

        private TextSelection RequireSelection()
        {
            var selection = Rtb.Selection;
            if (selection == null) return null;

            if (selection.IsEmpty)
            {
                var caret = selection.Start;
                var start = FindWordBoundary(caret, LogicalDirection.Backward);
                var end = FindWordBoundary(caret, LogicalDirection.Forward);
                if (start != null && end != null && start.CompareTo(end) < 0)
                    selection.Select(start, end);
            }

            return selection.IsEmpty ? null : selection;
        }

        private TextPointer FindWordBoundary(TextPointer position, LogicalDirection direction)
        {
            var current = position;
            int guard = 0;
            while (guard++ < 200)
            {
                var next = current.GetPositionAtOffset(direction == LogicalDirection.Forward ? 1 : -1);
                if (next == null) return current;

                var probe = direction == LogicalDirection.Forward ? current : next;
                var ch = GetCharAt(probe);
                if (ch == '\0' || ch == '\r' || ch == '\n' || ch == ' ' || ch == '\t')
                    return direction == LogicalDirection.Forward ? current : next;

                current = next;
            }
            return current;
        }

        private static char GetCharAt(TextPointer position)
        {
            if (position == null) return '\0';
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = position.GetTextInRun(LogicalDirection.Forward);
                return string.IsNullOrEmpty(text) ? '\0' : text[0];
            }
            return '\0';
        }

        private void AfterFormat(TextPointer caret)
        {
            OnTextChangedInternal();
            Rtb.Focus();
            if (caret != null) Rtb.CaretPosition = caret;
        }

        private Paragraph CurrentParagraph
        {
            get
            {
                var caret = Rtb.CaretPosition ?? Rtb.Document.ContentStart;
                return caret.Paragraph;
            }
        }

        #endregion

        #region === СОБЫТИЯ РЕДАКТОРА ===

        private void Rtb_TextChanged(object sender, TextChangedEventArgs e)
        {
            OnTextChangedInternal();
        }

        private void OnTextChangedInternal()
        {
            if (_suppressNotification) return;
            IsDirty = true;
            PauseAnimationsWhileTyping();
            TextChanged?.Invoke(this, new RoutedEventArgs());
        }

        /// <summary>
        /// Останавливает GIF-анимации на время набора и планирует их возврат.
        ///
        /// Анимация держит DispatcherTimer с приоритетом Render: смена кадра
        /// конкурирует с обработкой клавиш, и при крупных гифках ввод заметно
        /// «заикается». Пока человек печатает, кадры не нужны — важнее отклик.
        /// </summary>
        private void PauseAnimationsWhileTyping()
        {
            // Считаем анимации по документу, а не по словарю: словарь только что
            // очищен StopAllAnimations, и по нему нельзя понять, надо ли
            // возвращать кадры после паузы.
            if (!HasAnimatedImages()) return;

            _animationsPaused = true;
            StopAllAnimations();
            _animationResumeTimer.Stop();
            _animationResumeTimer.Start();
        }

        /// <summary>Есть ли в документе анимированные GIF.</summary>
        private bool HasAnimatedImages()
        {
            if (_hasAnimatedImages.HasValue) return _hasAnimatedImages.Value;

            bool found = false;
            if (IsNoteOpen && Rtb.Document != null)
            {
                try
                {
                    foreach (var image in FindImages())
                    {
                        var rel = MarkdownParser.GetImageSource(image);
                        if (!string.IsNullOrEmpty(rel) &&
                            rel.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] поиск анимаций: {ex.Message}");
                }
            }

            _hasAnimatedImages = found;
            return found;
        }

        private void Rtb_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (e.Key == Key.Enter && !ctrl && !_autoFormatting && TryAutoFormatBlock())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V && ctrl)
            {
                try
                {
                    if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
                    {
                        e.Handled = true;
                        TryPasteImage();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] Буфер обмена недоступен: {ex.Message}");
                }
            }

            if ((e.Key == Key.Delete || e.Key == Key.Back) && _selectedImage != null)
            {
                e.Handled = true;
                RemoveSelectedImage();
                return;
            }

            if (!ctrl) return;

            switch (e.Key)
            {
                case Key.B:
                    e.Handled = true;
                    ApplyBold();
                    break;
                case Key.I:
                    e.Handled = true;
                    ApplyItalic();
                    break;
                case Key.U:
                    e.Handled = true;
                    if (shift) ApplyStrikethrough();
                    else ApplyUnderline();
                    break;
                case Key.K:
                    e.Handled = true;
                    RequestWikiLink?.Invoke(this, EventArgs.Empty);
                    break;
                case Key.D1:
                    if (shift) { e.Handled = true; ApplyHeading(1); }
                    break;
                case Key.D2:
                    if (shift) { e.Handled = true; ApplyHeading(2); }
                    break;
                case Key.D3:
                    if (shift) { e.Handled = true; ApplyHeading(3); }
                    break;
                case Key.D0:
                    if (shift) { e.Handled = true; ApplyHeading(0); }
                    break;
                case Key.E:
                    if (shift) { e.Handled = true; ApplyInlineCode(); }
                    break;
            }
        }

        /// <summary>
        /// Превращает markdown-префикс строки в готовый блок при нажатии Enter:
        /// "- ", "1. ", "[] ", "# ", "## ", "> ", "--- ".
        /// </summary>
        private bool TryAutoFormatBlock()
        {
            var para = CurrentParagraph;
            if (para == null) return false;

            var text = new TextRange(para.ContentStart, para.ContentEnd).Text;
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return false;

            _autoFormatting = true;
            try
            {
                if (Regex.IsMatch(trimmed, @"^[-*+]\s*$"))
                {
                    ApplyList(false);
                    return true;
                }
                if (Regex.IsMatch(trimmed, @"^\d+[.)]\s*$"))
                {
                    ApplyList(true);
                    return true;
                }
                if (Regex.IsMatch(trimmed, @"^\[[ xX]?\]\s*$"))
                {
                    ApplyList(false, true);
                    return true;
                }
                if (trimmed == "#")
                {
                    ApplyHeading(1);
                    return true;
                }
                if (trimmed == "##")
                {
                    ApplyHeading(2);
                    return true;
                }
                if (trimmed == "###")
                {
                    ApplyHeading(3);
                    return true;
                }
                if (trimmed == ">")
                {
                    ApplyQuote();
                    return true;
                }
            }
            finally
            {
                _autoFormatting = false;
            }

            return false;
        }

        private void Rtb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(Rtb);
            var hit = Rtb.InputHitTest(pos) as DependencyObject;

            var link = FindParent<Hyperlink>(hit);
            if (link != null)
            {
                var wiki = MarkdownParser.GetWikiTarget(link);
                if (!string.IsNullOrEmpty(wiki))
                {
                    e.Handled = true;
                    WikiLinkClicked?.Invoke(wiki);
                    return;
                }

                // Обычная ссылка [текст](адрес): открываем в браузере.
                // LocalPath здесь не годится — у http-адреса он даёт «/items/3».
                var url = MarkdownParser.GetLinkUrl(link);
                if (!string.IsNullOrEmpty(url))
                {
                    e.Handled = true;
                    OpenExternalLink(url);
                    return;
                }

                if (link.NavigateUri != null)
                {
                    e.Handled = true;
                    try { System.Diagnostics.Process.Start(link.NavigateUri.LocalPath); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] Вложение не открыто: {ex.Message}"); }
                    return;
                }
            }

            var image = FindParent<Image>(hit);
            if (image != null)
            {
                _selectedImage = image;
                ImagePanel.Visibility = Visibility.Visible;
            }
            else
            {
                _selectedImage = null;
                ImagePanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Открывает внешнюю ссылку из заметки: http/https — браузером,
        /// mailto — почтовой программой, file — по ассоциации файлов.
        /// </summary>
        private void OpenExternalLink(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] ссылка не открыта ({url}): {ex.Message}");
                Hint?.Invoke("Не удалось открыть ссылку: " + ex.Message);
            }
        }

        private static T FindParent<T>(DependencyObject node) where T : DependencyObject
        {
            while (node != null)
            {
                if (node is T typed) return typed;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        private void ImageSize_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedImage == null) return;
            if (!(sender is Button button) || !int.TryParse(button.Tag?.ToString(), out var width)) return;

            double newWidth;
            if (width == 0)
            {
                var rel = MarkdownParser.GetImageSource(_selectedImage);
                var full = MarkdownParser.ResolvePath(rel, PhotoRoot);
                var src = MarkdownParser.LoadImage(full, 0) as BitmapSource;
                newWidth = src != null ? Math.Min(src.PixelWidth, 1400) : 700;
            }
            else
            {
                newWidth = width;
            }

            _selectedImage.Width = newWidth;
            _selectedImage.Height = double.NaN;
            MarkdownParser.SetImageWidth(_selectedImage, width == 0 ? -1 : (int)newWidth);

            var container = MarkdownParser.GetImageContainer(_selectedImage);
            if (container != null)
                MarkdownParser.SetImageWidth(container, width == 0 ? -1 : (int)newWidth);

            ImagePanel.Visibility = Visibility.Collapsed;
            OnTextChangedInternal();
        }

        private void ImageReload_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedImage == null) return;

            var rel = MarkdownParser.GetImageSource(_selectedImage);
            var full = MarkdownParser.ResolvePath(rel, PhotoRoot);
            if (full != null && File.Exists(full))
            {
                _selectedImage.Source = MarkdownParser.LoadImage(full, 0);
                if (rel != null && rel.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    StartGifAnimation(_selectedImage, full);
            }

            ImagePanel.Visibility = Visibility.Collapsed;
            OnTextChangedInternal();
        }

        private void ImageDelete_Click(object sender, RoutedEventArgs e)
        {
            RemoveSelectedImage();
        }

        /// <summary>Удаляет выбранную картинку (кнопкой или клавишей Delete).</summary>
        private void RemoveSelectedImage()
        {
            var image = _selectedImage;
            _selectedImage = null;
            ImagePanel.Visibility = Visibility.Collapsed;

            if (image == null) return;

            StopGifAnimation(image);

            try
            {
                var container = MarkdownParser.GetImageContainer(image) ?? image.Parent as InlineUIContainer;
                var paragraph = container?.Parent as Paragraph;

                paragraph?.Inlines.Remove(container);

                if (paragraph != null && paragraph.Inlines.Count == 0 &&
                    paragraph.Parent is FlowDocument doc)
                {
                    doc.Blocks.Remove(paragraph);
                }

                // WPF требует, чтобы документ не оставался пустым
                EnsureDocumentHasBlock();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] Ошибка удаления картинки: {ex.Message}");
                EnsureDocumentHasBlock();
            }

            InvalidateDocumentCache();
            OnTextChangedInternal();
        }

        private void Rtb_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                double size = Rtb.FontSize + (e.Delta > 0 ? 1 : -1);
                if (size < 10) size = 10;
                if (size > 32) size = 32;
                Rtb.FontSize = size;
            }
        }

        #endregion

        #region === DRAG & DROP, ВСТАВКА ===

        private void Rtb_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Rtb_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;

            var point = e.GetPosition(Rtb);
            var position = Rtb.GetPositionFromPoint(point, true) ?? Rtb.CaretPosition;
            Rtb.CaretPosition = position;

            foreach (var file in files)
                InsertDroppedFile(file);

            Rtb.Focus();
        }

        /// <summary>Вставляет файл: картинку — как изображение, остальное — как вложение.</summary>
        public void InsertDroppedFile(string file)
        {
            if (!File.Exists(file)) return;

            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ImageExtensions.Contains(ext))
            {
                var full = SaveImageFile(file, out var relative);
                if (full != null) InsertImage(full, relative);
            }
            else
            {
                var full = SaveAttachmentFile(file, out var relative);
                if (full != null)
                    InsertAttachmentLink(full, relative, Path.GetFileName(file));
            }
        }

        /// <summary>Вставляет ссылку-вложение в текст.</summary>
        public void InsertAttachmentLink(string fullPath, string relativePath, string displayName)
        {
            var link = new Hyperlink(new Run(displayName))
            {
                Foreground = new SolidColorBrush(Frost),
                Cursor = Cursors.Hand,
                ToolTip = "Вложение: " + relativePath
            };
            if (!string.IsNullOrEmpty(fullPath))
            {
                try { link.NavigateUri = new Uri(fullPath); }
                catch { }
            }

            var selection = Rtb.Selection;
            if (!selection.IsEmpty) selection.Text = "";

            InsertInlineIntoDocument(link);
        }

        /// <summary>Вставка картинки из буфера обмена (сохраняется файлом).</summary>
        public bool TryPasteImage()
        {
            try
            {
                if (!Clipboard.ContainsImage() || Clipboard.ContainsText()) return false;

                var bitmap = Clipboard.GetImage();
                if (bitmap == null) return false;

                var folder = EnsureFolder(ImagesFolder);
                if (string.IsNullOrEmpty(folder)) return false;

                var name = "paste_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
                var target = Path.Combine(folder, name);

                using (var stream = new FileStream(target, FileMode.Create))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(stream);
                }

                InsertImage(target, "NoteImages/" + _currentCharId + "/" + name);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownEditor] Ошибка вставки картинки: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
