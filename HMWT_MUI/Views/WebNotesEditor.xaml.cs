using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Редактор заметок на WebView2.
    ///
    /// Зачем переехали: прежний редактор собирал документ из элементов WPF и
    /// перезапускал гифки в потоке интерфейса — на заметке с несколькими крупными
    /// гифками окно подвешивалось. Внутри WebView гифки анимирует сам браузерный
    /// движок, а текст правится как обычный HTML.
    ///
    /// Формат хранения не меняется: заметка по-прежнему текст с разметкой,
    /// перевод в HTML и обратно делает <see cref="NotesHtmlBridge"/>.
    /// Если WebView2 не поднялся, вкладка заметок остаётся на прежнем редакторе.
    ///
    /// Важное про скорость: движок и страница поднимаются один раз. При переходе
    /// между заметками страница НЕ перезагружается — в неё только подставляется
    /// новый текст, поэтому открытие заметки не ждёт запуска браузера.
    /// </summary>
    public partial class WebNotesEditor : UserControl
    {
        /// <summary>Кликнули по вики-ссылке: вкладка открывает другую заметку.</summary>
        public event Action<string> WikiLinkClicked;

        /// <summary>Просят вставить вики-ссылку (Ctrl+K и кнопка на панели).</summary>
        public event EventHandler RequestWikiLink;

        /// <summary>Короткая подсказка в строке состояния вкладки.</summary>
        public event Action<string> Hint;

        /// <summary>Текст изменился (для автосохранения вкладки).</summary>
        public event EventHandler TextChanged;

        /// <summary>
        /// Просят открыть обычную ссылку внутри приложения (отдельным окном).
        /// Если никто не подписан, ссылка откроется в системном браузере.
        /// </summary>
        public event Action<string> InternalLinkRequested;

        /// <summary>Сохранение по Ctrl+S из самого редактора.</summary>
        public event EventHandler SaveRequested;

        /// <summary>Вставка картинки из буфера обмена (Ctrl+V внутри текста).</summary>
        public event EventHandler PasteImageRequested;

        /// <summary>Открыта ли заметка (как у прежнего редактора).</summary>
        public bool IsNoteOpen { get; private set; }

        /// <summary>Есть ли несохранённые правки.</summary>
        public bool IsDirty { get; private set; }

        /// <summary>Есть ли в заметке хоть что-то, кроме пустых абзацев.</summary>
        public bool HasContent { get; private set; }

        /// <summary>Слов в заметке (считает страница — без повторного разбора разметки).</summary>
        public int WordCount { get; private set; }

        /// <summary>Символов в заметке.</summary>
        public int CharCount { get; private set; }

        /// <summary>Корень картинок персонажа (для виртуального адреса страницы).</summary>
        public string PhotoRoot { get; set; } = "";

        /// <summary>Папка картинок заметок.</summary>
        public string ImagesFolder { get; set; } = "";

        /// <summary>Папка вложений заметок.</summary>
        public string AttachmentsFolder { get; set; } = "";

        /// <summary>Названия заметок для вики-ссылок (подсказка автодополнения).</summary>
        public List<string> WikiTitles { get; set; } = new List<string>();

        /// <summary>Готов ли движок: иначе вкладка оставит прежний редактор.</summary>
        public bool IsReady { get; private set; }

        private bool _inAppLinks;

        /// <summary>
        /// Открывать ссылки внутри приложения (true) или в системном браузере (false).
        /// Обычный клик по ссылке следует этому выбору, Ctrl+клик всегда открывает браузер.
        /// </summary>
        public bool InAppLinks
        {
            get => _inAppLinks;
            set
            {
                if (_inAppLinks == value) return;
                _inAppLinks = value;
                Run("setLinkMode", value ? "internal" : "browser");
            }
        }

        private int _characterId;
        private string _lastHtml = "";
        private bool _readOnly;
        private bool _initStarted;
        private bool _pageLoaded;
        private bool _pong;

        private CoreWebView2 _core;

        public WebNotesEditor()
        {
            InitializeComponent();
            Loaded += Editor_Loaded;
        }

        private void Editor_Loaded(object sender, RoutedEventArgs e)
        {
            // Повторный показ вкладки: движок уже поднят — страницу не перезагружаем
            if (_core != null)
            {
                Reattach();
                return;
            }

            // Прошлая попытка не удалась — пробуем снова, но не запускаем две сразу
            if (_initStarted) return;
            _initStarted = true;

            StartAsync();
        }

        /// <summary>Поднимает движок и загружает страницу редактора.</summary>
        private async void StartAsync()
        {
            try
            {
                // Общий движок на всё приложение: второй экземпляр (просмотр ссылок)
                // берёт тот же профиль, и запуск второго окна не ждёт старта браузера.
                var environment = await WebView2Host.GetEnvironmentAsync();
                await Web.EnsureCoreWebView2Async(environment);

                if (Web.CoreWebView2 == null)
                    throw new InvalidOperationException("движок не создан");

                var core = Web.CoreWebView2;
                _core = core;

                // Картинки заметок отдаём странице по виртуальному адресу:
                // локальные файлы иначе браузер не покажет
                string root = string.IsNullOrWhiteSpace(PhotoRoot) ? DatabaseHelper.GetPhotoBasePath() : PhotoRoot;
                try
                {
                    core.SetVirtualHostNameToFolderMapping(
                        NotesBridgeConstants.VirtualHostName, root, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Заметки] виртуальный адрес картинок: {ex.Message}");
                }

                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;

                // Фон страницы задаём цветом темы: прозрачный фон у окна движка
                // ведёт себя по-разному на разных видеокартах
                Web.DefaultBackgroundColor = PageColor();

                core.WebMessageReceived += OnWebMessage;
                core.NavigationCompleted += (s, e) => _pageLoaded = true;

                // Окно движка показываем до загрузки страницы: если оно скрыто,
                // оно не получает размеров и не принимает щелчки
                Web.Visibility = Visibility.Visible;
                FallbackNotice.Visibility = Visibility.Collapsed;

                Navigate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Заметки] WebView2 не поднялся: " + ex);
                IsReady = false;
                _initStarted = false;
                FallbackNotice.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Показывает страницу заново (после сбоя или первого запуска).</summary>
        private void Navigate()
        {
            if (_core == null) return;
            _pageLoaded = false;
            IsReady = false;
            _core.NavigateToString(BuildPage());
        }

        /// <summary>
        /// Вкладку показали снова. Движок жив, но окно страницы могло
        /// пересоздаться — проверяем страницу и при необходимости поднимаем её.
        /// </summary>
        private void Reattach()
        {
            Web.Visibility = Visibility.Visible;
            ShowContent();

            if (!IsReady || !_pageLoaded) return;

            _pong = false;
            Run("ping");

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (!_pong) Navigate();
            };
            timer.Start();
        }

        // ══════════════════════ СООБЩЕНИЯ СО СТРАНИЦЫ ══════════════════════

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.WebMessageAsJson; }
            catch { return; }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var t) ? t.GetString() : "";

                switch (type)
                {
                    case "ready":
                        IsReady = true;
                        _pageLoaded = true;
                        ShowContent();
                        break;

                    case "pong":
                        _pong = true;
                        break;

                    case "changed":
                        _lastHtml = root.TryGetProperty("html", out var h) ? (h.GetString() ?? "") : "";
                        HasContent = !(root.TryGetProperty("empty", out var em) && em.GetBoolean());
                        IsDirty = true;
                        TextChanged?.Invoke(this, EventArgs.Empty);
                        break;

                    case "counts":
                        WordCount = (int)Number(root, "words");
                        CharCount = (int)Number(root, "chars");
                        break;

                    case "html":
                        _lastHtml = root.TryGetProperty("html", out var hh) ? (hh.GetString() ?? "") : "";
                        break;

                    case "wiki":
                        WikiLinkClicked?.Invoke(root.TryGetProperty("target", out var w) ? (w.GetString() ?? "") : "");
                        break;

                    case "requestWiki":
                        RequestWikiLink?.Invoke(this, EventArgs.Empty);
                        break;

                    case "openLink":
                        OpenLink(root.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "",
                                 !(root.TryGetProperty("external", out var ex) && ex.GetBoolean()));
                        break;

                    case "editLink":
                        EditLink(root.TryGetProperty("text", out var lt) ? (lt.GetString() ?? "") : "",
                                 root.TryGetProperty("url", out var lu) ? (lu.GetString() ?? "") : "");
                        break;

                    case "save":
                        SaveRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case "pasteImage":
                        PasteImageRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case "hint":
                        Hint?.Invoke(root.TryGetProperty("text", out var ht) ? (ht.GetString() ?? "") : "");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Заметки] разбор сообщения: {ex.Message}");
            }
        }

        private static double Number(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value)) return 0;
            try { return value.GetDouble(); } catch { return 0; }
        }

        /// <summary>Открывает ссылку: внутри приложения (окном) или в браузере.</summary>
        private void OpenLink(string url, bool insideApp)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            if (insideApp && InternalLinkRequested != null)
            {
                InternalLinkRequested(url);
                return;
            }

            if (!NoteLinks.Open(url))
                Hint?.Invoke("ссылка не открылась: " + url);
        }

        /// <summary>Спрашивает новый адрес ссылки и меняет его на странице.</summary>
        private void EditLink(string text, string url)
        {
            string newUrl = Windows.NotePromptWindow.AskText(Window.GetWindow(this),
                "Адрес ссылки", "Куда ведёт ссылка «" + text + "»?", url, "Сохранить");
            if (string.IsNullOrWhiteSpace(newUrl)) return;

            Run("editCurrentLink", newUrl);
        }

        // ══════════════════════ ПУБЛИЧНОЕ API (как у прежнего редактора) ══════════════════════

        /// <summary>Загружает текст заметки.</summary>
        public void LoadMarkdown(string markdown, int characterId, bool readOnly = false)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            _characterId = characterId;
            _readOnly = readOnly;
            IsNoteOpen = true;
            IsDirty = false;

            _lastHtml = NotesHtmlBridge.ToHtml(markdown);
            HasContent = !string.IsNullOrWhiteSpace(markdown);
            WordCount = 0;
            CharCount = 0;

            ShowContent();
        }

        /// <summary>Показывает загруженное содержимое на странице.</summary>
        private void ShowContent()
        {
            if (!IsReady || _core == null) return;
            Web.Visibility = Visibility.Visible;
            FallbackNotice.Visibility = Visibility.Collapsed;

            Run("setHtml", _lastHtml ?? "", _readOnly ? "true" : "false");
            Run("setLinkMode", _inAppLinks ? "internal" : "browser");
        }

        /// <summary>Текст заметки для сохранения.</summary>
        public string GetMarkdown()
        {
            // Текст берём из копии, которую страница присылает на каждое изменение.
            // Раньше здесь ждали ответ, «прокачивая» цикл сообщений вложенно —
            // из-за этого программа замирала на несколько секунд и падала.
            return NotesHtmlBridge.ToMarkdown(_lastHtml);
        }

        /// <summary>Просит у страницы текущий HTML без ожидания (ответ придёт сообщением).</summary>
        public void RequestHtml() => Run("emitHtml", "0");

        /// <summary>Помечает документ сохранённым.</summary>
        public void MarkClean() => IsDirty = false;

        /// <summary>Помечает документ изменённым.</summary>
        public void MarkDirty() => IsDirty = true;

        /// <summary>Очищает заметку.</summary>
        public void Clear()
        {
            _lastHtml = "";
            HasContent = false;
            WordCount = 0;
            CharCount = 0;
            IsDirty = true;
            if (!IsReady) return;
            Run("setHtml", "<p><br></p>", "true");
        }

        /// <summary>Ставит фокус в текст (курсор — в конец заметки).</summary>
        public void FocusEditor() => Run("focusDoc");

        // ── команды панели инструментов ──
        public void ApplyBold() => Run("bold");
        public void ApplyItalic() => Run("italic");
        public void ApplyUnderline() => Run("underline");
        public void ApplyStrikethrough() => Run("strike");
        public void ApplyInlineCode() => Run("code");
        public void ApplyHighlight() => Run("highlight");

        /// <summary>Цвет текста: выделению или всей заметке (hex #RRGGBB).</summary>
        public void ApplyTextColor(string hex, bool wholeText = false)
            => Run("color", string.IsNullOrWhiteSpace(hex) ? "#ECE4D0" : hex, wholeText ? "true" : "false");
        public void ApplyHeading(int level) => Run("heading", level.ToString());
        public void ApplyList(bool numbered, bool checklist = false)
            => Run("list", numbered ? "ol" : "ul", checklist ? "check" : "");
        public void ApplyQuote() => Run("quote");
        public void InsertHorizontalRule() => Run("hr");
        public void InsertTable(int rows = 3, int cols = 3) => Run("table", rows.ToString(), cols.ToString());

        /// <summary>Вставляет вики-ссылку.</summary>
        public void InsertWikiLink(string title, string alias = null)
            => Run("insertWiki", title ?? "", alias ?? title ?? "");

        /// <summary>
        /// Вставляет картинку. Путь для показа — относительный (страница берёт файл
        /// по виртуальному адресу); полный путь принимаем для совместимости.
        /// </summary>
        public void InsertImage(string fullPath, string relative = null)
            => Run("insertImage", string.IsNullOrWhiteSpace(relative) ? (fullPath ?? "") : relative);

        /// <summary>Вставляет ссылку на вложение.</summary>
        public void InsertAttachmentLink(string fullPath, string relative, string title)
            => Run("insertAttachment",
                   string.IsNullOrWhiteSpace(relative) ? (fullPath ?? "") : relative,
                   title ?? "");

        /// <summary>Анимации в WebView делает движок: эти вызовы больше не нужны.</summary>
        public void StopAllAnimations() { }

        /// <summary>Анимации в WebView делает движок: эти вызовы больше не нужны.</summary>
        public void RestartAnimations() { }

        /// <summary>
        /// Копирует картинку в папку заметок. Возвращает полный путь (или null при
        /// ошибке) и относительный путь для разметки — так же, как прежний редактор.
        /// </summary>
        public string SaveImageFile(string sourcePath, out string relative)
        {
            relative = "";
            try
            {
                if (string.IsNullOrWhiteSpace(ImagesFolder) || !File.Exists(sourcePath)) return null;
                Directory.CreateDirectory(ImagesFolder);
                string name = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath);
                string full = Path.Combine(ImagesFolder, name);
                File.Copy(sourcePath, full, true);
                relative = "NoteImages/" + _characterId + "/" + name;
                return full;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Заметки] картинка не сохранена: {ex.Message}");
                return null;
            }
        }

        /// <summary>Копирует вложение в папку заметок: полный путь и путь для разметки.</summary>
        public string SaveAttachmentFile(string sourcePath, out string relative)
        {
            relative = "";
            try
            {
                if (string.IsNullOrWhiteSpace(AttachmentsFolder) || !File.Exists(sourcePath)) return null;
                Directory.CreateDirectory(AttachmentsFolder);
                string name = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath);
                string full = Path.Combine(AttachmentsFolder, name);
                File.Copy(sourcePath, full, true);
                relative = "NoteAttachments/" + _characterId + "/" + name;
                return full;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Заметки] вложение не сохранено: {ex.Message}");
                return null;
            }
        }

        // ══════════════════════ ВЫЗОВ КОМАНД НА СТРАНИЦЕ ══════════════════════

        /// <summary>Выполняет команду на странице (аргументы подставляются как JSON).</summary>
        private void Run(string command, params string[] args)
        {
            if (_core == null) return;

            var parts = new List<string>();
            foreach (string arg in args)
                parts.Add(JsonSerializer.Serialize(arg ?? ""));

            string script = "notesApi." + command + "(" + string.Join(",", parts) + ")";
            try { _core.ExecuteScriptAsync(script); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Заметки] команда {command}: {ex.Message}"); }
        }

        // ══════════════════════ СТРАНИЦА РЕДАКТОРА ══════════════════════

        /// <summary>Цвет фона страницы — из палитры темы.</summary>
        private static System.Drawing.Color PageColor()
        {
            var brush = Application.Current?.TryFindResource("B.Slate") as SolidColorBrush;
            if (brush != null)
                return System.Drawing.Color.FromArgb(brush.Color.R, brush.Color.G, brush.Color.B);

            return System.Drawing.Color.FromArgb(0x15, 0x15, 0x1F);
        }

        /// <summary>
        /// Собирает HTML страницы: оформление берём из палитры приложения,
        /// чтобы светлая и тёмная темы работали и в заметках.
        /// </summary>
        private string BuildPage()
        {
            string panel = BrushHex("B.PanelHi", "#241F33");
            string ink = BrushHex("B.Ink", "#ECE4D0");
            string mute = BrushHex("B.Mute", "#8B859C");
            string accent = BrushHex("B.Accent", "#9A7BD6");
            string line = BrushHex("B.Line", "#322E44");
            string background = BrushHex("B.Slate", "#15151F");
            string display = FontName("F.Display", "Segoe UI");
            string body = FontName("F.Body", "Segoe UI");

            string page = PageTemplate
                .Replace("{{PANEL}}", panel)
                .Replace("{{INK}}", ink)
                .Replace("{{MUTE}}", mute)
                .Replace("{{ACCENT}}", accent)
                .Replace("{{LINE}}", line)
                .Replace("{{BG}}", background)
                .Replace("{{FONT_DISPLAY}}", display)
                .Replace("{{FONT_BODY}}", body);

            return page;
        }

        private static string BrushHex(string key, string fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
                    return "#" + brush.Color.R.ToString("X2") + brush.Color.G.ToString("X2") + brush.Color.B.ToString("X2");
            }
            catch { }
            return fallback;
        }

        private static string FontName(string key, string fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is FontFamily font)
                    return font.Source;
            }
            catch { }
            return fallback;
        }

        /// <summary>Разметка страницы редактора (вставляется в WebView целиком).</summary>
        private const string PageTemplate = @"<!DOCTYPE html>
<html lang=""ru""><head><meta charset=""utf-8"">
<style>
  :root { --panel: {{PANEL}}; --ink: {{INK}}; --mute: {{MUTE}}; --accent: {{ACCENT}};
          --line: {{LINE}}; --bg: {{BG}}; }
  * { box-sizing: border-box; }
  html, body { margin: 0; height: 100%; background: var(--bg); }
  body { font-family: '{{FONT_BODY}}', 'Segoe UI', sans-serif; color: var(--ink);
         overflow-y: auto; overflow-x: hidden; }

  /* Поле правки занимает всю страницу: щёлкнуть можно в любом месте */
  #doc { outline: none; padding: 18px 22px; min-height: 100%; font-size: 14px; line-height: 1.65;
         caret-color: var(--accent); cursor: text; word-wrap: break-word; }
  #doc[contenteditable=""false""] { cursor: default; }

  #doc h1, #doc h2, #doc h3 { font-family: '{{FONT_DISPLAY}}', serif; color: var(--ink); margin: 18px 0 8px; }
  #doc h1 { font-size: 24px; } #doc h2 { font-size: 20px; } #doc h3 { font-size: 17px; }
  #doc p { margin: 0 0 10px; }
  #doc a { color: var(--accent); text-decoration: underline; cursor: pointer; }
  #doc a.wiki { border-bottom: 1px dotted var(--accent); text-decoration: none; }
  #doc blockquote { margin: 10px 0; padding: 6px 12px; border-left: 3px solid var(--accent); color: var(--mute); }
  #doc code { background: rgba(127,127,127,.18); padding: 1px 5px; border-radius: 4px; font-family: Consolas, monospace; }
  #doc mark { background: rgba(224,174,78,.35); color: inherit; }
  #doc ul, #doc ol { margin: 0 0 10px 22px; padding: 0; }
  #doc li { margin: 2px 0; }
  #doc ul ul, #doc ul ol, #doc ol ul, #doc ol ol { margin-bottom: 2px; }
  #doc hr { border: none; border-top: 1px solid var(--line); margin: 16px 0; }
  #doc table { border-collapse: collapse; margin: 10px 0; }
  #doc td, #doc th { border: 1px solid var(--line); padding: 4px 8px; min-width: 60px; }
  #doc img { max-width: 100%; height: auto; border-radius: 6px; cursor: pointer; }
  #doc img.sel { outline: 2px solid var(--accent); outline-offset: 1px; }

  /* Чек-лист: квадратик рисуем стилем, в тексте заметки его нет */
  #doc ul.check { list-style: none; margin-left: 6px; }
  #doc li.task { position: relative; padding-left: 24px; cursor: pointer; }
  #doc li.task::before { content: '☐'; position: absolute; left: 2px; top: 0; color: var(--mute); }
  #doc li.task[data-check=""1""]::before { content: '☑'; color: var(--accent); }
  #doc li.task[data-check=""1""] { color: var(--mute); text-decoration: line-through; }

  ::selection { background: rgba(154,123,214,.35); }

  /* Ручки-уголки у выбранной картинки: лежат ВНЕ поля правки, в текст не попадают */
  #handles { position: fixed; inset: 0; pointer-events: none; z-index: 10; }
  #handles[hidden] { display: none; }
  .handle { position: absolute; width: 11px; height: 11px; margin: -6px 0 0 -6px;
            background: var(--bg); border: 2px solid var(--accent); border-radius: 50%;
            pointer-events: auto; }
  .handle[data-corner=""nw""], .handle[data-corner=""se""] { cursor: nwse-resize; }
  .handle[data-corner=""ne""], .handle[data-corner=""sw""] { cursor: nesw-resize; }

  #imgbar { position: fixed; z-index: 11; display: none; gap: 6px; padding: 5px 7px;
            background: var(--panel); border: 1px solid var(--line); border-radius: 7px;
            box-shadow: 0 6px 18px rgba(0,0,0,.35); }
  #imgbar button { font: inherit; font-size: 12px; color: var(--ink); background: transparent;
                   border: 1px solid var(--line); border-radius: 5px; padding: 3px 8px; cursor: pointer; }
  #imgbar button:hover { border-color: var(--accent); }
  #sizehint { position: fixed; z-index: 12; display: none; font-size: 11px; color: var(--ink);
              background: var(--panel); border: 1px solid var(--line); border-radius: 5px; padding: 2px 6px; }
</style></head>
<body>
<div id=""doc"" contenteditable=""true"" spellcheck=""false""><p><br></p></div>

<div id=""handles"" hidden>
  <div class=""handle"" data-corner=""nw"" id=""h-nw""></div>
  <div class=""handle"" data-corner=""ne"" id=""h-ne""></div>
  <div class=""handle"" data-corner=""sw"" id=""h-sw""></div>
  <div class=""handle"" data-corner=""se"" id=""h-se""></div>
</div>

<div id=""imgbar"">
  <button id=""btnResetSize"" type=""button"">Размер 100%</button>
  <button id=""btnDeleteImage"" type=""button"">Удалить картинку</button>
</div>
<div id=""sizehint""></div>

<script>
const doc = document.getElementById('doc');
const handles = document.getElementById('handles');
const imgbar = document.getElementById('imgbar');
const sizehint = document.getElementById('sizehint');

const post = (obj) => { try { window.chrome.webview.postMessage(obj); } catch (e) {} };
const esc = (s) => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

let readOnly = false;
let linkMode = 'browser';
let selectedImg = null;

// ── состояние и отправка изменений ──

function isEmpty() {
  return doc.innerText.replace(/\u00a0/g, '').trim().length === 0 && doc.querySelectorAll('img').length === 0;
}

function emitChanged() {
  if (selectedImg) showHandles();
  post({ type: 'changed', html: doc.innerHTML, empty: isEmpty() });
  scheduleCounts();
}

let countsTimer = null;
function scheduleCounts() {
  if (countsTimer) return;
  countsTimer = setTimeout(() => {
    countsTimer = null;
    const text = doc.innerText.replace(/\u00a0/g, ' ');
    const words = text.split(/[\s\u00a0]+/).filter(w => w.length > 0).length;
    post({ type: 'counts', words: words, chars: text.replace(/\n/g, '').length });
  }, 300);
}

function emitHtml(tag) { post({ type: 'html', tag: String(tag ?? ''), html: doc.innerHTML }); }
function ping() { post({ type: 'pong' }); }
function setLinkMode(mode) { linkMode = mode === 'internal' ? 'internal' : 'browser'; }

function clearSelectionUi() {
  if (selectedImg) selectedImg.classList.remove('sel');
  selectedImg = null;
  handles.hidden = true;
  imgbar.style.display = 'none';
  sizehint.style.display = 'none';
}

function setHtml(html, ro) {
  clearSelectionUi();
  doc.innerHTML = html && String(html).trim().length ? html : '<p><br></p>';

  // Сравниваем со строкой явно: значения сюда приходят строками, и «false»
  // как булево оказалось бы истиной — редактор остался бы недоступным для правки
  readOnly = String(ro) === 'true';
  doc.setAttribute('contenteditable', readOnly ? 'false' : 'true');
  window.scrollTo(0, 0);
}

function focusDoc() {
  if (!doc.isContentEditable) return;
  doc.focus();
  const sel = window.getSelection();
  const range = document.createRange();
  range.selectNodeContents(doc);
  range.collapse(false);
  sel.removeAllRanges();
  sel.addRange(range);
  try { doc.scrollIntoView({ block: 'nearest' }); } catch (e) {}
}

function exec(cmd, value) {
  if (!doc.isContentEditable) return;
  doc.focus();
  document.execCommand(cmd, false, value === undefined ? null : value);
  emitChanged();
}

// Цвет текста: выделению или всему документу. styleWithCSS даёт span
// со style=color:..., а не устаревший font color.
function applyColor(color, whole) {
  if (!doc.isContentEditable) return;
  doc.focus();
  document.execCommand('styleWithCSS', false, true);

  const sel = window.getSelection();
  const all = String(whole) === 'true';
  if (all) {
    document.execCommand('selectAll', false, null);
    document.execCommand('foreColor', false, color);
    sel.removeAllRanges();
    const range = document.createRange();
    range.selectNodeContents(doc);
    range.collapse(false);
    sel.addRange(range);
  } else {
    document.execCommand('foreColor', false, color);
  }
  emitChanged();
}

// ── выбранная картинка и её уголки ──

function selectImage(img) {
  clearSelectionUi();
  if (!img || readOnly) return;
  selectedImg = img;
  img.classList.add('sel');
  showHandles();
  showImageBar();
}

function showHandles() {
  if (!selectedImg) { handles.hidden = true; return; }
  const r = selectedImg.getBoundingClientRect();
  handles.hidden = false;
  const at = { nw: [r.left, r.top], ne: [r.right, r.top], sw: [r.left, r.bottom], se: [r.right, r.bottom] };
  for (const corner in at) {
    const node = document.getElementById('h-' + corner);
    node.style.left = Math.round(at[corner][0]) + 'px';
    node.style.top = Math.round(at[corner][1]) + 'px';
  }
  showImageBar();
}

function showImageBar() {
  if (!selectedImg) { imgbar.style.display = 'none'; return; }
  const r = selectedImg.getBoundingClientRect();
  imgbar.style.display = 'flex';
  const height = imgbar.offsetHeight || 34;
  let top = r.bottom + 8;
  if (top + height > window.innerHeight - 8) top = Math.max(8, r.top - height - 8);
  let left = Math.min(r.left, window.innerWidth - (imgbar.offsetWidth || 220) - 12);
  imgbar.style.top = Math.round(top) + 'px';
  imgbar.style.left = Math.round(Math.max(8, left)) + 'px';
}

let drag = null;

function beginDrag(event) {
  if (!selectedImg) return;
  event.preventDefault();
  event.stopPropagation();

  const corner = event.currentTarget.getAttribute('data-corner');
  const rect = selectedImg.getBoundingClientRect();
  drag = {
    startX: event.clientX,
    startWidth: rect.width,
    direction: (corner === 'ne' || corner === 'se') ? 1 : -1
  };
  window.addEventListener('mousemove', moveDrag);
  window.addEventListener('mouseup', endDrag);
}

function moveDrag(event) {
  if (!drag || !selectedImg) return;

  const limit = Math.max(80, doc.clientWidth - 44);
  let width = drag.startWidth + (event.clientX - drag.startX) * drag.direction;
  width = Math.round(Math.max(60, Math.min(width, limit)));

  selectedImg.style.width = width + 'px';
  selectedImg.style.height = 'auto';
  selectedImg.setAttribute('width', String(width));
  selectedImg.setAttribute('data-width', String(width));

  showHandles();
  sizehint.style.display = 'block';
  sizehint.textContent = width + ' px';
  sizehint.style.left = Math.round(event.clientX + 14) + 'px';
  sizehint.style.top = Math.round(event.clientY + 10) + 'px';
}

function endDrag() {
  if (!drag) return;
  drag = null;
  sizehint.style.display = 'none';
  window.removeEventListener('mousemove', moveDrag);
  window.removeEventListener('mouseup', endDrag);
  if (selectedImg) showHandles();
  emitChanged();
}

document.getElementById('h-nw').addEventListener('mousedown', beginDrag);
document.getElementById('h-ne').addEventListener('mousedown', beginDrag);
document.getElementById('h-sw').addEventListener('mousedown', beginDrag);
document.getElementById('h-se').addEventListener('mousedown', beginDrag);

document.getElementById('btnResetSize').addEventListener('click', () => {
  if (!selectedImg) return;
  selectedImg.removeAttribute('data-width');
  selectedImg.removeAttribute('width');
  selectedImg.style.width = '';
  selectedImg.style.height = '';
  emitChanged();
  showHandles();
});

document.getElementById('btnDeleteImage').addEventListener('click', () => {
  if (!selectedImg) return;
  const node = selectedImg;
  clearSelectionUi();
  if (node.parentNode) node.parentNode.removeChild(node);
  emitChanged();
});

// ── редакторские команды ──

function heading(level) { exec('formatBlock', level === '0' ? '<p>' : '<h' + level + '>'); }

function list(kind, mode) {
  if (!doc.isContentEditable) return;
  doc.focus();
  if (mode === 'check') {
    const html = '<ul class=""check""><li class=""task"" data-check=""0"">' + esc(window.getSelection().toString()) + '</li></ul><p><br></p>';
    document.execCommand('insertHTML', false, html);
  } else if (kind === 'ol') {
    document.execCommand('insertOrderedList', false, null);
  } else {
    document.execCommand('insertUnorderedList', false, null);
  }
  emitChanged();
}

function quote() { exec('formatBlock', '<blockquote>'); }
function hr() { exec('insertHorizontalRule'); }

function table(rowsArg, colsArg) {
  const rows = Math.max(1, parseInt(rowsArg, 10) || 3);
  const cols = Math.max(1, parseInt(colsArg, 10) || 3);
  let html = '<table>';
  for (let r = 0; r < rows; r++) {
    html += '<tr>';
    for (let c = 0; c < cols; c++) html += '<td><br></td>';
    html += '</tr>';
  }
  html += '</table><p><br></p>';
  exec('insertHTML', html);
}

function highlight() {
  const sel = window.getSelection();
  if (!sel || sel.rangeCount === 0) return;
  const text = sel.toString();
  if (!text) return;
  exec('insertHTML', '<mark>' + esc(text) + '</mark>');
}

function inlineCode() {
  const sel = window.getSelection();
  if (!sel || sel.rangeCount === 0 || !sel.toString()) { post({ type: 'hint', text: 'выделите текст, чтобы сделать его кодом' }); return; }
  exec('insertHTML', '<code>' + esc(sel.toString()) + '</code>');
}

function imageUrl(path) {
  return 'https://notes.local/' + String(path).split('\\').join('/').split(' ').join('%20');
}

function insertImage(path) {
  if (!path) return;
  exec('insertHTML', '<img src=""' + imageUrl(path) + '"" data-path=""' + esc(path) + '""/>');
}

function insertWiki(target, alias) {
  exec('insertHTML', '<a class=""wiki"" data-wiki=""' + esc(target) + '"">' + esc(alias || target) + '</a>&nbsp;');
}

function insertAttachment(path, title) {
  if (!path) return;
  exec('insertHTML', '<a class=""web"" data-url=""' + esc(path) + '"" data-attach=""1"">' + esc(title || path) + '</a>&nbsp;');
}

function editCurrentLink(newUrl) {
  const sel = window.getSelection();
  let node = sel && sel.anchorNode;
  while (node && node.nodeType !== 1) node = node.parentNode;
  while (node && node.tagName !== 'A') node = node.parentNode;
  if (!node) return;
  node.setAttribute('href', newUrl);
  node.setAttribute('data-url', newUrl);
  emitChanged();
}

// ── события страницы ──

doc.addEventListener('input', emitChanged);

doc.addEventListener('mousedown', (e) => {
  if (e.target && e.target.tagName === 'IMG') {
    selectImage(e.target);
    return;
  }
  clearSelectionUi();
});

doc.addEventListener('click', (e) => {
  const target = e.target;

  // Чек-лист: щелчок по квадратику слева отмечает пункт
  const task = target && target.closest ? target.closest('li.task') : null;
  if (task) {
    const box = task.getBoundingClientRect();
    if (e.clientX - box.left < 22) {
      e.preventDefault();
      task.setAttribute('data-check', task.getAttribute('data-check') === '1' ? '0' : '1');
      emitChanged();
      return;
    }
  }

  const link = target && target.closest ? target.closest('a') : null;
  if (!link) return;

  const wiki = link.getAttribute('data-wiki');
  if (wiki) {
    e.preventDefault();
    post({ type: 'wiki', target: wiki });
    return;
  }

  const url = link.getAttribute('data-url') || link.getAttribute('href') || '';
  if (!url) return;

  // Ctrl+клик всегда открывает системный браузер; обычный клик — как выбрано переключателем
  e.preventDefault();
  post({ type: 'openLink', url: url, external: (e.ctrlKey || e.metaKey) || linkMode !== 'internal' });
});

doc.addEventListener('contextmenu', (e) => {
  const link = e.target && e.target.closest ? e.target.closest('a') : null;
  if (!link || link.getAttribute('data-wiki')) return;
  e.preventDefault();
  post({ type: 'editLink', text: (link.textContent || '').trim(),
         url: link.getAttribute('data-url') || link.getAttribute('href') || '' });
});

doc.addEventListener('keydown', (e) => {
  const ctrl = e.ctrlKey || e.metaKey;
  const key = (e.key || '').toLowerCase();

  // Картинку удаляют кнопкой на панели, а не клавишей: случайное нажатие
  // стирало картинку вместе с файлом
  if ((e.key === 'Delete' || e.key === 'Backspace') && selectedImg) { e.preventDefault(); return; }

  if (ctrl && key === 's') { e.preventDefault(); post({ type: 'save' }); return; }
  if (ctrl && !e.shiftKey && key === 'b') { e.preventDefault(); exec('bold'); return; }
  if (ctrl && !e.shiftKey && key === 'i') { e.preventDefault(); exec('italic'); return; }
  if (ctrl && !e.shiftKey && key === 'u') { e.preventDefault(); exec('underline'); return; }
  if (ctrl && e.shiftKey && key === 'u') { e.preventDefault(); exec('strikeThrough'); return; }
  if (ctrl && e.shiftKey && key === 'e') { e.preventDefault(); inlineCode(); return; }
  if (ctrl && key === 'k') { e.preventDefault(); post({ type: 'requestWiki' }); return; }
  if (ctrl && e.shiftKey && key >= '0' && key <= '3') {
    e.preventDefault();
    heading(key);
    return;
  }
});

doc.addEventListener('paste', (e) => {
  const items = e.clipboardData ? e.clipboardData.items : null;
  if (!items) return;
  for (const item of items) {
    if (item.kind === 'file' && item.type.indexOf('image/') === 0) {
      e.preventDefault();
      post({ type: 'pasteImage' });
      return;
    }
  }

  // Текст из браузера приходит с color:black — вставляем как обычный,
  // иначе на тёмном фоне его не видно.
  const text = e.clipboardData.getData('text/plain');
  if (text) {
    e.preventDefault();
    document.execCommand('insertText', false, text);
    emitChanged();
  }
});

// Масштаб текста: Ctrl+колесо, как в прежнем редакторе
let zoom = 14;
window.addEventListener('wheel', (e) => {
  if (!e.ctrlKey) return;
  e.preventDefault();
  zoom = Math.max(10, Math.min(26, zoom + (e.deltaY < 0 ? 1 : -1)));
  doc.style.fontSize = zoom + 'px';
  showHandles();
}, { passive: false });

window.addEventListener('scroll', () => { if (selectedImg) showHandles(); }, true);
window.addEventListener('resize', () => { if (selectedImg) showHandles(); });
window.addEventListener('blur', () => { clearSelectionUi(); });

// Перетаскивать файлы в WebView нельзя (страница не получает путь к файлу):
// честно предупреждаем вместо молчаливого игнорирования
doc.addEventListener('dragover', (e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'none'; });
doc.addEventListener('drop', (e) => {
  e.preventDefault();
  post({ type: 'hint', text: 'перетаскивание не поддерживается — вставьте картинку кнопкой на панели' });
});

window.addEventListener('DOMContentLoaded', () => {
  post({ type: 'ready', ms: Math.round(performance.now()) });
});

window.notesApi = {
  setHtml, emitHtml, focusDoc, exec, heading, list, quote, hr, table,
  highlight, insertWiki, insertImage, insertAttachment, editCurrentLink,
  setLinkMode, ping, imageUrl,
  bold: () => exec('bold'), italic: () => exec('italic'), underline: () => exec('underline'),
  strike: () => exec('strikeThrough'), code: inlineCode, color: applyColor
};
</script>
</body></html>";
    }
}
