using HMWT_MUI.Models;
using HMWT_MUI.Services;
using HMWT_MUI.Windows;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using HMWT_MUI.Helpers;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Вкладка заметок: дерево групп (с подгруппами) и заметок, нативный
    /// Markdown-редактор с картинками, GIF и wiki-ссылками.
    /// Все диалоги — отдельные окна, поэтому ничего не перекрывает ввод.
    /// </summary>
    public partial class NotesView : UserControl
    {
        /// <summary>Ключ настройки: открывать ссылки внутри приложения или в браузере.</summary>
        private const string LinkModeSettingKey = "Notes_LinkInApp";

        private readonly int _characterId;
        private readonly NotesService _notesService;

        private long? _currentNoteId;
        private long? _selectedGroupId;
        private string _savedMarkdown = "";

        private DispatcherTimer _debounceTimer;
        private int _searchVersion;
        private HashSet<long> _matchedNotes;
        private HashSet<long> _matchedGroups;

        private Point _dragStart;
        private NoteTreeItem _dragItem;
        private DateTime _lastGroupClick;

        private Button _linkModeButton;
        private NoteLinkWindow _linkWindow;

        public NotesView(int characterId)
        {
            InitializeComponent();

            _characterId = characterId;
            _notesService = new NotesService(characterId);

            Editor.ImagesFolder = _notesService.GetImagesPath();
            Editor.AttachmentsFolder = _notesService.GetAttachmentsPath();
            Editor.WikiLinkClicked += Editor_WikiLinkClicked;
            Editor.RequestWikiLink += (s, e) => InsertWikiLink();
            Editor.Hint += text => SetStatus(text, "B.Accent");
            Editor.TextChanged += Editor_TextChanged;
            Editor.InternalLinkRequested += ShowInternalLink;
            Editor.SaveRequested += (s, e) => SaveCurrentNote(force: true);
            Editor.PasteImageRequested += (s, e) => InsertImageFromClipboard();

            Editor.InAppLinks = LoadLinkModeSetting();

            BuildToolbar();

            Loaded += NotesView_Loaded;
            Unloaded += NotesView_Unloaded;
        }

        #region === ЖИЗНЕННЫЙ ЦИКЛ ===

        private void NotesView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SetupDebounceTimer();
                SetupEscHandler();
                Editor.PhotoRoot = DatabaseHelper.GetPhotoBasePath();
                LoadTree();
                RestoreOpenNote();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка загрузки: {ex.Message}");
                SetStatus("ошибка загрузки", "B.Danger");
            }
        }

        /// <summary>
        /// Возвращает на экран заметку, которая была открыта, когда вкладку закрыли.
        ///
        /// Вид вкладки кэшируется, поэтому при возврате в «Заметки» вызывается
        /// только Loaded — редактор к этому моменту пуст, хотя _currentNoteId
        /// указывает на заметку. Раньше здесь безусловно показывалось пустое
        /// состояние: заметка выглядела стёртой, а клик по ней уже ничего не
        /// делал (OpenNote выходит сразу, если id совпадает).
        /// </summary>
        private void RestoreOpenNote()
        {
            if (!_currentNoteId.HasValue)
            {
                ShowEmptyState();
                return;
            }

            var item = FindById(NotesTree.Items.OfType<NoteTreeItem>(), _currentNoteId.Value, false);
            if (item == null)
            {
                // Заметку удалили, пока вкладка была закрыта
                _currentNoteId = null;
                _savedMarkdown = "";
                ShowEmptyState();
                return;
            }

            if (Editor.IsNoteOpen && Editor.HasContent)
            {
                // Редактор не размонтировался — текст на месте, ничего не трогаем
                SetStatus("заметка открыта", "B.Mute");
                UpdateWordCount();
                return;
            }

            NoteTitleBox.Text = item.Title ?? "";
            Editor.LoadMarkdown(_savedMarkdown, _characterId);
            Editor.MarkClean();
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
            SetStatus("заметка открыта", "B.Mute");
            UpdateWordCount();
        }

        private void NotesView_Unloaded(object sender, RoutedEventArgs e)
        {
            _debounceTimer?.Stop();
            RemoveEscHandler();
            SaveCurrentNote(force: true);
            Editor.StopAllAnimations();
        }

        private void SetupEscHandler()
        {
            var window = Window.GetWindow(this);
            if (window != null)
                window.PreviewKeyDown += Window_PreviewKeyDown;
        }

        private void RemoveEscHandler()
        {
            var window = Window.GetWindow(this);
            if (window != null)
                window.PreviewKeyDown -= Window_PreviewKeyDown;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                SaveCurrentNote(force: true);
            }
        }

        #endregion

        #region === ТУЛБАР ===

        private void BuildToolbar()
        {
            AddToolbarButton("B", "Жирный (Ctrl+B)", () => Editor.ApplyBold(), FontWeights.Bold);
            AddToolbarButton("I", "Курсив (Ctrl+I)", () => Editor.ApplyItalic(), null, FontStyles.Italic);
            AddToolbarButton("U", "Подчёркнутый (Ctrl+U)", () => Editor.ApplyUnderline(), null, null, TextDecorations.Underline);
            AddToolbarButton("S̶", "Зачёркнутый (Ctrl+Shift+U)", () => Editor.ApplyStrikethrough(), null, null, TextDecorations.Strikethrough);
            AddToolbarButton("〈〉", "Код (Ctrl+Shift+E)", () => Editor.ApplyInlineCode());
            AddToolbarButton("highlight", "Выделение цветом", () => Editor.ApplyHighlight());

            AddToolbarSeparator();

            AddToolbarButton("H1", "Заголовок 1 (Ctrl+Shift+1)", () => Editor.ApplyHeading(1));
            AddToolbarButton("H2", "Заголовок 2 (Ctrl+Shift+2)", () => Editor.ApplyHeading(2));
            AddToolbarButton("H3", "Заголовок 3 (Ctrl+Shift+3)", () => Editor.ApplyHeading(3));
            AddToolbarButton("¶", "Обычный текст (Ctrl+Shift+0)", () => Editor.ApplyHeading(0));

            AddToolbarSeparator();

            AddToolbarButton("•", "Маркированный список", () => Editor.ApplyList(false));
            AddToolbarButton("1.", "Нумерованный список", () => Editor.ApplyList(true));
            AddToolbarButton("check", "Чек-лист", () => Editor.ApplyList(false, true));
            AddToolbarButton("quote", "Цитата", () => Editor.ApplyQuote());
            AddToolbarButton("―", "Разделитель", () => Editor.InsertHorizontalRule());

            AddToolbarSeparator();

            AddToolbarButton("image", "Вставить картинку (Ctrl+V — из буфера)", InsertImage);
            AddToolbarButton("film", "Вставить GIF-анимацию", InsertImage);
            AddToolbarButton("paperclip", "Прикрепить файл", InsertAttachment);
            AddToolbarButton("link", "Wiki-ссылка (Ctrl+K)", InsertWikiLink);
            AddToolbarButton("table", "Таблица", InsertTable);
            AddLinkModeButton();

            AddToolbarSeparator();

            AddToolbarButton("arrowUp", "Экспорт в Markdown", ExportCurrentNote);
            AddToolbarButton("arrowDown", "Импорт Markdown в заметку", ImportMarkdownIntoNote);
            AddToolbarButton("?", "Справка", ShowHelp);
        }

        private void AddToolbarButton(string content, string tooltip, Action action,
            FontWeight? fontWeight = null, FontStyle? fontStyle = null, TextDecorationCollection decorations = null)
        {
            var text = new TextBlock { Text = IconLibrary.Has(content) ? "" : content, TextAlignment = TextAlignment.Center };
            if (fontWeight.HasValue) text.FontWeight = fontWeight.Value;
            if (fontStyle.HasValue) text.FontStyle = fontStyle.Value;
            if (decorations != null) text.TextDecorations = decorations;

            object face = IconLibrary.Has(content)
                ? new IconView { Kind = content, IconSize = 16, IconBrush = DmUiKit.B("Ink") }
                : (object)text;

            var button = new Button
            {
                Content = face,
                ToolTip = tooltip,
                Style = FindResource("ToolbarBtn") as Style,
                Margin = new Thickness(1, 0, 1, 0)
            };
            button.Click += (s, e) => action();
            EditorToolbar.Children.Add(button);
        }

        private void AddToolbarSeparator()
        {
            EditorToolbar.Children.Add(new Border
            {
                Width = 1,
                Margin = new Thickness(6, 4, 6, 4),
                Background = FindResource("B.Line") as System.Windows.Media.Brush
            });
        }

        /// <summary>
        /// Переключатель «где открывать ссылки»: внутри приложения или в браузере.
        ///
        /// Обычный клик по ссылке следует этому выбору; Ctrl+клик всегда
        /// открывает системный браузер. Выбор запоминается в настройках,
        /// как тема оформления, — при следующем запуске он тот же.
        /// </summary>
        private void AddLinkModeButton()
        {
            var icon = new IconView
            {
                Kind = "link",
                IconSize = 14,
                IconBrush = DmUiKit.B("Mute"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.5
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(label);

            _linkModeButton = new Button
            {
                Content = content,
                Style = FindResource("ToolbarBtn") as Style,
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(8, 0, 8, 0)
            };
            _linkModeButton.Click += (s, e) => SetLinkMode(!Editor.InAppLinks, save: true);

            EditorToolbar.Children.Add(_linkModeButton);
            UpdateLinkModeButton(label);
        }

        private TextBlock LinkModeLabel =>
            (_linkModeButton?.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();

        private void UpdateLinkModeButton(TextBlock label = null)
        {
            label = label ?? LinkModeLabel;
            if (label == null) return;

            label.Text = Editor.InAppLinks ? "Ссылки: внутри" : "Ссылки: в браузере";
            _linkModeButton.ToolTip = Editor.InAppLinks
                ? "Ссылки открываются окном внутри программы. Нажмите, чтобы открывать их в браузере. Ctrl+клик всегда открывает браузер."
                : "Ссылки открываются в системном браузере. Нажмите, чтобы открывать их внутри программы. Ctrl+клик всегда открывает браузер.";
        }

        private void SetLinkMode(bool inApp, bool save)
        {
            Editor.InAppLinks = inApp;
            UpdateLinkModeButton();

            if (!save) return;

            try
            {
                DatabaseHelper.SaveSetting(LinkModeSettingKey, inApp ? "internal" : "browser");
                SetStatus(inApp ? "ссылки откроются внутри программы" : "ссылки откроются в браузере", "B.Mute");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] режим ссылок не сохранён: {ex.Message}");
            }
        }

        private bool LoadLinkModeSetting()
        {
            try
            {
                return string.Equals(DatabaseHelper.GetSetting(LinkModeSettingKey, "browser"),
                    "internal", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Показывает ссылку окном внутри программы (режим «внутри»).</summary>
        private void ShowInternalLink(string url)
        {
            try
            {
                CloseLinkWindow();

                _linkWindow = new NoteLinkWindow(url, Window.GetWindow(this));
                _linkWindow.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_linkWindow, s)) _linkWindow = null;
                };
                _linkWindow.Show();

                SetStatus("ссылка открыта внутри программы", "B.Mute");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] просмотр ссылки: {ex.Message}");
                SetStatus("не удалось открыть ссылку внутри программы", "B.Danger");
                NoteLinks.Open(url);
            }
        }

        /// <summary>
        /// Закрывает окно просмотра ссылки.
        ///
        /// Обязательно при выборе другой заметки: иначе поверх новой заметки
        /// остаётся страница из прошлой, и непонятно, к чему она относится.
        /// </summary>
        private void CloseLinkWindow()
        {
            if (_linkWindow == null) return;

            var window = _linkWindow;
            _linkWindow = null;
            try { window.Close(); }
            catch (Exception ex) { Debug.WriteLine($"[NotesView] окно ссылки не закрылось: {ex.Message}"); }
        }

        #endregion

        #region === ДЕРЕВО ===

        private void LoadTree(long? idToSelect = null)
        {
            try
            {
                var expanded = CollectExpandedIds();
                var tree = _notesService.GetTree();

                // Проставляем уровни: по ним рисуются вертикальные направляющие,
                // чтобы вложенность читалась как в Obsidian
                foreach (var root in tree) root.ApplyDepth(0);

                NotesTree.ItemsSource = tree;

                RestoreExpanded(tree, expanded);

                if (idToSelect.HasValue)
                    SelectItemById(idToSelect.Value);

                Editor.WikiTitles = _notesService.GetAllNoteTitles();
                UpdateTreeInfo(tree);

                // Активный поиск применяем к пересозданному дереву
                var query = SearchBox.Text.Trim();
                if (query.Length > 0)
                    _ = RunSearchAsync(query);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка загрузки дерева: {ex}");
                SetStatus("ошибка загрузки дерева: " + ex.GetType().Name + " " + ex.Message, "B.Danger");
            }
        }

        private HashSet<long> CollectExpandedIds()
        {
            var ids = new HashSet<long>();
            foreach (var item in Flatten())
            {
                if (item.IsGroup && item.IsExpanded) ids.Add(item.Id);
            }
            return ids;
        }

        /// <summary>При первом открытии разворачиваем корневые группы.</summary>
        private void RestoreExpanded(IEnumerable<NoteTreeItem> items, HashSet<long> expanded)
        {
            foreach (var item in Flatten(items))
            {
                if (!item.IsGroup) continue;
                item.IsExpanded = expanded.Count == 0 || expanded.Contains(item.Id);
            }
        }

        private static IEnumerable<NoteTreeItem> Flatten(IEnumerable<NoteTreeItem> items = null)
        {
            foreach (var item in items)
            {
                yield return item;
                foreach (var child in Flatten(item.Children))
                    yield return child;
            }
        }

        private IEnumerable<NoteTreeItem> Flatten()
        {
            var roots = NotesTree.Items.OfType<NoteTreeItem>().ToList();
            return Flatten(roots);
        }

        private void UpdateTreeInfo(IEnumerable<NoteTreeItem> tree)
        {
            var all = Flatten(tree).ToList();
            int groups = all.Count(i => i.IsGroup);
            int notes = all.Count(i => !i.IsGroup);

            TreeInfoText.Text = notes == 0
                ? "Пока пусто. Создайте группу или заметку."
                : $"Групп: {groups} • Заметок: {notes}";
        }

        private void SelectItemById(long id)
        {
            foreach (var item in Flatten())
            {
                if (item.Id != id) continue;

                item.IsSelected = true;
                ExpandParentsOf(item);
                return;
            }
        }

        private void ExpandParentsOf(NoteTreeItem target)
        {
            foreach (var item in Flatten().Where(i => i.IsGroup))
            {
                if (ContainsRecursive(item, target)) item.IsExpanded = true;
            }
        }

        private static bool ContainsRecursive(NoteTreeItem parent, NoteTreeItem target)
        {
            foreach (var child in parent.Children)
            {
                if (ReferenceEquals(child, target)) return true;
                if (ContainsRecursive(child, target)) return true;
            }
            return false;
        }

        private static NoteTreeItem FindById(IEnumerable<NoteTreeItem> items, long id)
        {
            foreach (var item in items)
            {
                if (item.Id == id) return item;
                var found = FindById(item.Children, id);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Поиск узла по идентификатору с указанием типа.
        ///
        /// Это важно: группы и заметки лежат в разных таблицах, и у каждой свой
        /// AUTOINCREMENT. Поэтому одно и то же число принадлежит и папке, и заметке
        /// одновременно, и поиск «по одному числу» возвращал первую попавшуюся
        /// запись при обходе дерева — почти всегда папку. Из-за этого папка
        /// «превращалась» в заметку: её заголовок попадал в поле названия,
        /// а при переименовании заметки переименовывалась папка.
        /// </summary>
        private static NoteTreeItem FindById(IEnumerable<NoteTreeItem> items, long id, bool isGroup)
        {
            foreach (var item in items)
            {
                if (item.Id == id && item.IsGroup == isGroup) return item;
                var found = FindById(item.Children, id, isGroup);
                if (found != null) return found;
            }
            return null;
        }

        private NoteTreeItem GetContextItem(object sender)
        {
            if (sender is ContextMenu menu && menu.PlacementTarget is FrameworkElement target)
                return target.DataContext as NoteTreeItem;
            if (sender is MenuItem item)
            {
                var ownerMenu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu;
                if (ownerMenu?.PlacementTarget is FrameworkElement fe)
                    return fe.DataContext as NoteTreeItem;
            }
            return null;
        }

        private void NotesTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var container = FindAncestor<TreeViewItem>(source);
            if (container == null)
            {
                e.Handled = true;
                return;
            }

            var item = container.DataContext as NoteTreeItem;
            if (item == null)
            {
                e.Handled = true;
                return;
            }

            var menu = FindResource("TreeMenu") as ContextMenu;
            if (menu == null) return;

            menu.PlacementTarget = container;
            menu.DataContext = item;

            var open = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == "Открыть");
            if (open != null) open.Visibility = item.IsGroup ? Visibility.Collapsed : Visibility.Visible;

            var export = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == "Экспорт в Markdown…");
            if (export != null) export.Visibility = item.IsGroup ? Visibility.Collapsed : Visibility.Visible;

            var attach = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == "Прикрепить файл…");
            if (attach != null) attach.Visibility = item.IsGroup ? Visibility.Collapsed : Visibility.Visible;

            var newSub = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == "Новая подгруппа…");
            if (newSub != null) newSub.Visibility = item.IsGroup ? Visibility.Visible : Visibility.Collapsed;

            var newNote = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == "Новая заметка здесь…");
            if (newNote != null) newNote.Visibility = item.IsGroup ? Visibility.Visible : Visibility.Collapsed;

            e.Handled = false;
            menu.IsOpen = true;
        }

        private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            while (node != null)
            {
                if (node is T typed) return typed;
                node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        private void NotesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var item = e.NewValue as NoteTreeItem;
            if (item == null) return;

            _selectedGroupId = item.IsGroup ? item.Id : null;

            if (item.IsGroup)
            {
                // Папка — это не заметка, поэтому редактор закрываем подсказкой.
                //
                // ВАЖНО: сначала сохраняем открытую заметку, а сам редактор НЕ
                // очищаем. Раньше здесь вызывалась полная очистка, и получалось
                // так: игрок заполнил заметку, щёлкнул по папке — и текст с
                // заголовком исчезали с экрана, будто всё стёрлось. Данные при
                // этом оставались в базе, но понять это было невозможно.
                SaveCurrentNote(force: true);

                ShowEmptyState("Это группа заметок",
                    "Выберите заметку внутри неё или создайте новую кнопкой «+ Заметка».\n" +
                    "Стрелка слева от названия раскрывает и сворачивает группу.");
                SetStatus($"группа «{item.Title}»", "B.Mute");
                return;
            }

            OpenNote(item);
        }

        /// <summary>
        /// Клик по стрелке слева от названия раскрывает/сворачивает папку, а двойной
        /// клик по строке делает то же самое.
        ///
        /// Раньше раскрытие работало только по двум кликам с интервалом менее 400 мс:
        /// одиночный клик выделял папку, а второй клик с паузой уже ничего не делал —
        /// отсюда жалоба, что «папка не раскрывается». Теперь есть явная зона стрелки.
        /// </summary>
        private void NotesTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragItem = null;

            var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (container != null)
                _dragItem = container.DataContext as NoteTreeItem;

            if (container == null || _dragItem == null) return;

            // Зона стрелки: левый край строки. Клик по ней раскрывает группу сразу.
            var point = e.GetPosition(container);
            if (point.X <= 22)
            {
                if (_dragItem.IsGroup)
                {
                    _dragItem.IsExpanded = !_dragItem.IsExpanded;
                    e.Handled = true;
                }
                return;
            }

            // Двойной клик по строке тоже раскрывает/сворачивает группу.
            if (e.ClickCount == 2 && _dragItem.IsGroup)
            {
                _dragItem.IsExpanded = !_dragItem.IsExpanded;
                e.Handled = true;
            }
        }

        private void NotesTree_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;

            var current = e.GetPosition(null);
            if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            try
            {
                DragDrop.DoDragDrop(NotesTree, _dragItem, DragDropEffects.Move);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка перетаскивания: {ex.Message}");
            }
            finally
            {
                _dragItem = null;
            }
        }

        private void NotesTree_DragOver(object sender, DragEventArgs e)
        {
            var target = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            var targetItem = target?.DataContext as NoteTreeItem;

            e.Effects = CanDrop(_dragItem, targetItem) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void NotesTree_Drop(object sender, DragEventArgs e)
        {
            var target = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            var targetItem = target?.DataContext as NoteTreeItem;

            if (!CanDrop(_dragItem, targetItem)) return;
            e.Handled = true;

            try
            {
                if (_dragItem.IsGroup)
                {
                    long? parent = targetItem == null ? null : (targetItem.IsGroup ? targetItem.Id : targetItem.ParentId);
                    if (!_notesService.MoveGroup(_dragItem.Id, parent))
                    {
                        SetStatus("нельзя вложить группу в саму себя", "B.Danger");
                        return;
                    }
                }
                else
                {
                    long? group = targetItem == null ? null : (targetItem.IsGroup ? targetItem.Id : targetItem.ParentId);
                    _notesService.MoveNote(_dragItem.Id, group);
                }

                var movedId = _dragItem.Id;
                LoadTree(movedId);
                SetStatus("перемещено", "B.Ok");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка перемещения: {ex.Message}");
                SetStatus("ошибка перемещения", "B.Danger");
            }
        }

        private static bool CanDrop(NoteTreeItem dragged, NoteTreeItem target)
        {
            if (dragged == null) return false;
            if (target == null) return true;                 // в корень
            if (ReferenceEquals(dragged, target)) return false;
            if (!dragged.IsGroup) return true;

            // Группу нельзя вложить в саму себя или в своего потомка
            return !ContainsRecursive(dragged, target);
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            var all = Flatten().Where(i => i.IsGroup).ToList();
            if (all.Count == 0) return;

            bool expand = all.Any(i => !i.IsExpanded);
            foreach (var group in all)
                group.IsExpanded = expand;
        }

        #endregion

        #region === ПОИСК ===

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = SearchBox.Text.Trim();

            if (string.IsNullOrEmpty(query))
            {
                _matchedNotes = null;
                _matchedGroups = null;
                LoadTree();
                return;
            }

            _ = RunSearchAsync(query);
        }

        /// <summary>Полнотекстовый поиск по заголовкам и содержимому заметок.</summary>
        private async System.Threading.Tasks.Task RunSearchAsync(string query)
        {
            int version = ++_searchVersion;

            try
            {
                var result = await System.Threading.Tasks.Task.Run(() =>
                {
                    var notes = _notesService.SearchNotes(query, out var groups);
                    return (notes, groups);
                });

                if (version != _searchVersion) return;

                _matchedNotes = result.notes;
                _matchedGroups = result.groups;

                var needle = query.ToLowerInvariant();
                foreach (var item in Flatten())
                {
                    if (item.IsGroup)
                    {
                        item.IsVisible = _matchedGroups.Contains(item.Id) || ContainsVisibleNote(item);
                        if (item.IsVisible) item.IsExpanded = true;
                    }
                    else
                    {
                        item.IsVisible = _matchedNotes.Contains(item.Id) ||
                                         item.Title.ToLowerInvariant().Contains(needle);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка поиска: {ex.Message}");
            }
        }

        private static bool ContainsVisibleNote(NoteTreeItem group)
        {
            foreach (var child in group.Children)
            {
                if (!child.IsGroup && child.IsVisible) return true;
                if (child.IsGroup && child.IsVisible && ContainsVisibleNote(child)) return true;
            }
            return false;
        }

        #endregion

        #region === СОЗДАНИЕ / ПЕРЕИМЕНОВАНИЕ / УДАЛЕНИЕ ===

        private void CreateGroup_Click(object sender, RoutedEventArgs e) => PromptCreateGroup(_selectedGroupId);

        private void CreateNote_Click(object sender, RoutedEventArgs e) => PromptCreateNote(_selectedGroupId);

        private void MenuNewSub_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextItem(sender);
            PromptCreateGroup(item?.Id);
        }

        private void MenuNewNote_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextItem(sender);
            PromptCreateNote(item?.Id);
        }

        private void PromptCreateGroup(long? parentGroupId)
        {
            var name = NotePromptWindow.AskText(Window.GetWindow(this),
                "Новая группа", parentGroupId.HasValue ? "Название подгруппы" : "Название группы",
                "Новая группа");

            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                var id = _notesService.CreateGroup(name.Trim(), parentGroupId);
                LoadTree(id);
                SetStatus("группа создана", "B.Ok");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка создания группы: {ex.Message}");
                SetStatus("не удалось создать группу", "B.Danger");
            }
        }

        private void PromptCreateNote(long? groupId)
        {
            var name = NotePromptWindow.AskText(Window.GetWindow(this),
                "Новая заметка", "Название заметки", "Новая заметка");

            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                var id = _notesService.CreateNote(name.Trim(), groupId);
                LoadTree(id);
                OpenNote(FindById(NotesTree.Items.OfType<NoteTreeItem>(), id, false));
                SetStatus("заметка создана", "B.Ok");
                Editor.FocusEditor();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка создания заметки: {ex.Message}");
                SetStatus("не удалось создать заметку", "B.Danger");
            }
        }

        private void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextItem(sender);
            if (item == null) return;

            var name = NotePromptWindow.AskText(Window.GetWindow(this),
                "Переименование", item.IsGroup ? "Новое имя группы" : "Новое имя заметки",
                item.Title, "Сохранить");

            if (string.IsNullOrWhiteSpace(name) || name.Trim() == item.Title) return;

            try
            {
                if (item.IsGroup)
                    _notesService.RenameGroup(item.Id, name.Trim());
                else
                    _notesService.UpdateNoteTitle(item.Id, name.Trim());

                if (!item.IsGroup && _currentNoteId == item.Id)
                    NoteTitleBox.Text = name.Trim();

                LoadTree(item.Id);
                SetStatus("переименовано", "B.Ok");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка переименования: {ex.Message}");
                SetStatus("ошибка переименования", "B.Danger");
            }
        }

        private void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is NoteTreeItem item)
                ConfirmDelete(item);
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextItem(sender);
            if (item != null) ConfirmDelete(item);
        }

        private void ConfirmDelete(NoteTreeItem item)
        {
            string message;
            if (item.IsGroup)
            {
                int notes = CountNotes(item);
                int groups = CountGroups(item);
                message = $"Будет удалено: {groups} групп и {notes} заметок вместе с их картинками и вложениями.";
            }
            else
            {
                message = "Заметка будет удалена вместе с картинками и вложениями.";
            }

            var confirmed = NotePromptWindow.Confirm(Window.GetWindow(this),
                $"Удалить {(item.IsGroup ? "группу" : "заметку")} «{item.Title}»?", message);

            if (!confirmed) return;

            try
            {
                if (item.IsGroup)
                    _notesService.DeleteGroup(item.Id);
                else
                    _notesService.DeleteNote(item.Id);

                if (_currentNoteId == item.Id)
                {
                    _currentNoteId = null;
                    _savedMarkdown = "";
                    NoteTitleBox.Text = "";
                    Editor.Clear();
                    ShowEmptyState();
                }

                LoadTree();
                SetStatus("удалено", "B.Ok");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка удаления: {ex.Message}");
                SetStatus("ошибка удаления", "B.Danger");
            }
        }

        private static int CountNotes(NoteTreeItem group)
        {
            int count = 0;
            foreach (var child in group.Children)
                count += child.IsGroup ? CountNotes(child) : 1;
            return count;
        }

        private static int CountGroups(NoteTreeItem group)
        {
            int count = 0;
            foreach (var child in group.Children)
            {
                if (child.IsGroup)
                {
                    count += 1 + CountGroups(child);
                }
            }
            return count;
        }

        private void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextItem(sender);
            if (item != null && !item.IsGroup) OpenNote(item);
        }

        private void MenuExportNote_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextItem(sender);
            if (item == null) return;

            if (item.IsGroup) return;
            if (_currentNoteId != item.Id) OpenNote(item);
            ExportCurrentNote();
        }

        private void MenuAttach_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextItem(sender);
            if (item == null) return;

            if (item.IsGroup) return;
            if (_currentNoteId != item.Id) OpenNote(item);
            InsertAttachment();
        }

        #endregion

        #region === РЕДАКТОР ===

        /// <summary>
        /// Открывает заметку. Принимает элемент дерева, а не идентификатор:
        /// идентификаторы групп и заметок берутся из разных таблиц со своими
        /// AUTOINCREMENT и пересекаются, поэтому по одному числу отличить
        /// папку от заметки невозможно. Проверка типа внутри метода —
        /// обязательная защита от «папка открылась как заметка».
        /// </summary>
        private void OpenNote(NoteTreeItem note)
        {
            if (note == null || note.IsGroup) return;

            if (_currentNoteId == note.Id)
            {
                Editor.FocusEditor();
                return;
            }

            // Вместе с прошлой заметкой закрываем и открытую из неё ссылку
            CloseLinkWindow();

            SaveCurrentNote(force: true);

            try
            {
                _currentNoteId = note.Id;
                _savedMarkdown = _notesService.GetNoteContent(note.Id);

                // Заголовок берём у той же заметки, которую открываем, — поиск по
                // одному числу мог вернуть папку с тем же номером
                NoteTitleBox.Text = note.Title ?? "";
                UpdateTitlePlaceholder();

                Editor.LoadMarkdown(_savedMarkdown, _characterId);
                Editor.MarkClean();

                EmptyStatePanel.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;

                SetStatus("заметка открыта", "B.Mute");
                UpdateWordCount();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка открытия заметки: {ex.Message}");
                SetStatus("ошибка открытия заметки", "B.Danger");
            }
        }

        /// <summary>
        /// Показывает подсказку вместо редактора.
        ///
        /// Редактор намеренно НЕ очищается: если игрок только что заполнил заметку
        /// и щёлкнул по папке, текст должен остаться на месте. Подсказка просто
        /// закрывает его собой, а при выборе заметки загружается нужный текст.
        /// </summary>
        private void ShowEmptyState(string title = null, string hint = null)
        {
            // Сохраняем открытую заметку, но не стираем её с экрана: иначе
            // выглядит так, будто работа пропала
            CloseLinkWindow();
            SaveCurrentNote(force: true);

            // Далее редактор не считается открытой заметкой: иначе следующий
            // выбор в дереве решил бы, что открывать нечего
            _currentNoteId = null;
            _savedMarkdown = "";

            ResetEmptyStateText();
            if (!string.IsNullOrEmpty(title)) EmptyStateTitle.Text = title;
            if (!string.IsNullOrEmpty(hint)) EmptyStateHint.Text = hint;

            EmptyStatePanel.Visibility = Visibility.Visible;
            UpdateTitlePlaceholder();
            WordCountText.Text = "";

            // Редактор очищаем только теперь: содержимое сохранено выше, а
            // _currentNoteId сброшен — значит очистка не запустит автосохранение
            // и не затрёт заметку пустотой.
            Editor.Clear();
        }

        private void Editor_TextChanged(object sender, EventArgs e)
        {
            if (!_currentNoteId.HasValue) return;

            _debounceTimer?.Stop();
            _debounceTimer?.Start();
            SetStatus("печатает…", "B.Mute");
            UpdateWordCount();
        }

        private void NoteTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTitlePlaceholder();

            if (!_currentNoteId.HasValue) return;

            _debounceTimer?.Stop();
            _debounceTimer?.Start();

            var item = FindById(NotesTree.Items.OfType<NoteTreeItem>(), _currentNoteId.Value, false);
            if (item != null && item.Title != NoteTitleBox.Text.Trim())
            {
                item.Title = NoteTitleBox.Text.Trim();
                Editor.WikiTitles = _notesService.GetAllNoteTitles();
            }
        }

        /// <summary>
        /// Меняет текст пустого состояния. Нужно, чтобы отличить «заметка не выбрана»
        /// от «выбрана группа» — иначе непонятно, почему редактор пуст.
        /// </summary>
        private void SetEmptyStateText(string title, string hint)
        {
            if (EmptyStateTitle != null) EmptyStateTitle.Text = title;
            if (EmptyStateHint != null) EmptyStateHint.Text = hint;
        }

        /// <summary>Возвращает пустое состояние к обычному тексту.</summary>
        private void ResetEmptyStateText() => SetEmptyStateText("Заметка не выбрана",
            "Выберите заметку в списке слева или создайте новую кнопкой «+ Заметка».\n" +
            "Заголовок и текст сохраняются автоматически.");

        /// <summary>
        /// Показывает подсказку «Название заметки», когда поле заголовка пустое.
        /// Раньше заголовок выглядел как обычный текст без рамки, и было не понять,
        /// что его вообще можно править.
        /// </summary>
        private void UpdateTitlePlaceholder()
        {
            if (TitlePlaceholder == null) return;
            TitlePlaceholder.Visibility = string.IsNullOrEmpty(NoteTitleBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SetupDebounceTimer()
        {
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                SaveCurrentNote();
            };
        }

        private void SaveCurrentNote(bool force = false)
        {
            if (!_currentNoteId.HasValue) return;

            try
            {
                var title = NoteTitleBox.Text.Trim();
                var item = FindById(NotesTree.Items.OfType<NoteTreeItem>(), _currentNoteId.Value, false);

                if (!string.IsNullOrEmpty(title) && item != null && item.Title != title)
                {
                    _notesService.UpdateNoteTitle(_currentNoteId.Value, title);
                    item.Title = title;
                }

                if (!Editor.IsDirty && !force) return;

                var markdown = Editor.GetMarkdown();

                // Защита от затирания заметки пустотой.
                //
                // Если редактор пуст, а в заметке был текст — это признак того,
                // что документ не разложился (например, вкладку переключили
                // слишком рано). Сохранение такого «пустого» документа стёрло бы
                // содержимое заметки, поэтому не пишем.
                if (!Editor.HasContent && !string.IsNullOrEmpty(_savedMarkdown))
                {
                    Debug.WriteLine("[NotesView] документ пуст, а в базе текст — сохранение пропущено");
                    SetStatus("содержимое не сохранено: редактор пуст", "B.Danger");
                    return;
                }

                if (!SameMarkdown(markdown, _savedMarkdown) || force)
                {
                    _notesService.SaveNoteContent(_currentNoteId.Value, markdown);
                    RemoveUnusedFiles(_savedMarkdown, markdown);
                    _savedMarkdown = markdown;
                }

                Editor.MarkClean();
                SetStatus("сохранено", "B.Ok");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка сохранения: {ex.Message}");
                SetStatus("ошибка сохранения", "B.Danger");
            }
        }

        /// <summary>
        /// Одинаковый ли это Markdown.
        ///
        /// Редактор сериализует абзацы через CRLF, а из базы текст приходит с LF:
        /// прямое сравнение всегда видело разницу и заставляло переписывать
        /// заметку при каждом сохранении. Для содержимого это неважно, поэтому
        /// переводы строк перед сравнением приводим к одному виду.
        /// </summary>
        private static bool SameMarkdown(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
        }

        private static string Normalize(string text)
            => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        /// <summary>Удаляет картинки, на которые больше нет ссылок.</summary>
        private void RemoveUnusedFiles(string oldMarkdown, string newMarkdown)
        {
            try
            {
                var oldPaths = NotesService.ExtractImagePaths(oldMarkdown).Distinct().ToList();
                var newPaths = new HashSet<string>(NotesService.ExtractImagePaths(newMarkdown), StringComparer.OrdinalIgnoreCase);

                foreach (var path in oldPaths)
                {
                    if (newPaths.Contains(path)) continue;

                    var full = NotePaths.Resolve(path);
                    if (full != null && File.Exists(full))
                    {
                        try { File.Delete(full); }
                        catch { /* файл занят — оставим */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка очистки картинок: {ex.Message}");
            }
        }

        /// <summary>
        /// Счётчик слов. Числа присылает сама страница заметки: раньше здесь
        /// каждый раз переводился HTML → Markdown → текст, и на большой заметке
        /// это заметно тормозило набор.
        /// </summary>
        private void UpdateWordCount()
        {
            try
            {
                int words = Editor.WordCount;
                int chars = Editor.CharCount;
                WordCountText.Text = words == 0 ? "" : $"{words} слов • {chars} симв.";
            }
            catch
            {
                WordCountText.Text = "";
            }
        }

        #endregion

        #region === WIKI-ССЫЛКИ ===

        private void InsertWikiLink()
        {
            if (!_currentNoteId.HasValue)
            {
                SetStatus("сначала откройте заметку", "B.Danger");
                return;
            }

            var titles = _notesService.GetAllNoteTitles();
            var current = FindById(NotesTree.Items.OfType<NoteTreeItem>(), _currentNoteId.Value, false)?.Title;

            var title = WikiPickerWindow.Pick(Window.GetWindow(this), titles, current);
            Editor.RestartAnimations();

            if (string.IsNullOrWhiteSpace(title)) return;

            Editor.InsertWikiLink(title.Trim());
            SetStatus("ссылка вставлена", "B.Ok");
        }

        private void Editor_WikiLinkClicked(string title)
        {
            var noteId = _notesService.FindNoteIdByTitle(title);
            if (noteId.HasValue)
            {
                var target = FindById(NotesTree.Items.OfType<NoteTreeItem>(), noteId.Value, false);
                if (target != null) OpenNote(target);
                LoadTree(noteId.Value);
                SetStatus("переход по ссылке: " + title, "B.Ok");
                return;
            }

            var create = NotePromptWindow.Confirm(Window.GetWindow(this),
                "Заметка не найдена",
                $"Заметки «{title}» пока нет. Создать её сейчас?",
                "Создать", danger: false);

            if (!create) return;

            var groupId = _selectedGroupId;
            var newId = _notesService.CreateNote(title, groupId);
            LoadTree(newId);
            OpenNote(FindById(NotesTree.Items.OfType<NoteTreeItem>(), newId, false));
            SetStatus("заметка создана по ссылке", "B.Ok");
        }

        #endregion

        #region === ВСТАВКИ ===

        private void InsertImage()
        {
            if (!_currentNoteId.HasValue)
            {
                SetStatus("сначала откройте заметку", "B.Danger");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Выберите картинку или GIF",
                Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tiff|Все файлы|*.*"
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            var full = Editor.SaveImageFile(dialog.FileName, out var relative);
            if (full == null)
            {
                SetStatus("не удалось скопировать картинку", "B.Danger");
                return;
            }

            Editor.InsertImage(full, relative);
            SetStatus("картинка вставлена", "B.Ok");
        }

        /// <summary>
        /// Вставляет картинку из буфера обмена.
        ///
        /// Внутри WebView страница не может сохранить файл на диск, поэтому
        /// она только сообщает «в буфере картинка», а сам снимок забирает
        /// и раскладывает по папке заметок программа.
        /// </summary>
        private void InsertImageFromClipboard()
        {
            if (!_currentNoteId.HasValue)
            {
                SetStatus("сначала откройте заметку", "B.Danger");
                return;
            }

            string temp = null;
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    SetStatus("в буфере обмена нет картинки", "B.Mute");
                    return;
                }

                var image = Clipboard.GetImage();
                if (image == null)
                {
                    SetStatus("не удалось прочитать картинку из буфера", "B.Danger");
                    return;
                }

                temp = Path.Combine(Path.GetTempPath(), "hmwt-paste-" + Guid.NewGuid().ToString("N") + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(temp)) encoder.Save(stream);

                var full = Editor.SaveImageFile(temp, out var relative);
                if (full == null)
                {
                    SetStatus("не удалось сохранить картинку из буфера", "B.Danger");
                    return;
                }

                Editor.InsertImage(full, relative);
                SetStatus("картинка вставлена из буфера", "B.Ok");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] вставка из буфера: {ex.Message}");
                SetStatus("не удалось вставить картинку из буфера", "B.Danger");
            }
            finally
            {
                if (temp != null)
                {
                    try { File.Delete(temp); } catch { /* временный файл — не критично */ }
                }
            }
        }

        private void InsertAttachment()
        {
            if (!_currentNoteId.HasValue)
            {
                SetStatus("сначала откройте заметку", "B.Danger");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Выберите файл для вложения",
                Filter = "Все файлы|*.*"
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            var full = Editor.SaveAttachmentFile(dialog.FileName, out var relative);
            if (full == null)
            {
                SetStatus("не удалось прикрепить файл", "B.Danger");
                return;
            }

            _notesService.AddAttachment(_currentNoteId.Value, Path.GetFileName(dialog.FileName), Path.GetFileName(full));
            Editor.InsertAttachmentLink(full, relative, Path.GetFileName(dialog.FileName));

            var item = FindById(NotesTree.Items.OfType<NoteTreeItem>(), _currentNoteId.Value, false);
            if (item != null)
            {
                item.HasAttachment = true;
                item.OnPropertyChanged(nameof(item.HasAttachment));
            }

            SetStatus("файл прикреплён", "B.Ok");
        }

        private void InsertTable()
        {
            if (!_currentNoteId.HasValue)
            {
                SetStatus("сначала откройте заметку", "B.Danger");
                return;
            }

            var rowsText = NotePromptWindow.AskText(Window.GetWindow(this),
                "Таблица", "Сколько строк? (включая заголовок)", "3", "Далее");
            if (rowsText == null) return;

            var colsText = NotePromptWindow.AskText(Window.GetWindow(this),
                "Таблица", "Сколько столбцов?", "3", "Вставить");
            if (colsText == null) return;

            if (!int.TryParse(rowsText, out var rows)) rows = 3;
            if (!int.TryParse(colsText, out var cols)) cols = 3;

            Editor.InsertTable(Math.Min(rows, 30), Math.Min(cols, 12));
            SetStatus("таблица вставлена", "B.Ok");
        }

        #endregion

        #region === ЭКСПОРТ / ИМПОРТ / СПРАВКА ===

        private void ExportCurrentNote()
        {
            SaveCurrentNote(force: true);

            if (!_currentNoteId.HasValue)
            {
                SetStatus("нет открытой заметки", "B.Danger");
                return;
            }

            var title = string.IsNullOrWhiteSpace(NoteTitleBox.Text) ? "заметка" : NoteTitleBox.Text.Trim();
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт заметки в Markdown",
                FileName = title + ".md",
                Filter = "Markdown|*.md|Текстовый файл|*.txt"
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                var markdown = Editor.GetMarkdown();
                var targetFolder = Path.GetDirectoryName(dialog.FileName);
                var assetsFolder = Path.Combine(targetFolder, "assets");

                markdown = CopyAssetsAndRewrite(markdown, assetsFolder);
                File.WriteAllText(dialog.FileName, markdown, new UTF8Encoding(false));

                SetStatus("экспортировано: " + Path.GetFileName(dialog.FileName), "B.Ok");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка экспорта: {ex.Message}");
                SetStatus("ошибка экспорта", "B.Danger");
            }
        }

        /// <summary>Копирует картинки заметки рядом с .md и переписывает ссылки.</summary>
        private string CopyAssetsAndRewrite(string markdown, string assetsFolder)
        {
            var result = markdown;

            foreach (var relative in NotesService.ExtractImagePaths(markdown).Distinct().ToList())
            {
                try
                {
                    var full = NotePaths.Resolve(relative);
                    if (full == null || !File.Exists(full)) continue;

                    Directory.CreateDirectory(assetsFolder);
                    var target = Path.Combine(assetsFolder, Path.GetFileName(full));
                    File.Copy(full, target, true);

                    result = result.Replace(relative, "assets/" + Path.GetFileName(full));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NotesView] Не удалось скопировать картинку при экспорте: {ex.Message}");
                }
            }

            return result;
        }

        private void ImportMarkdownIntoNote()
        {
            if (!_currentNoteId.HasValue)
            {
                SetStatus("сначала откройте заметку", "B.Danger");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Импорт Markdown",
                Filter = "Markdown|*.md;*.txt|Все файлы|*.*"
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                var markdown = File.ReadAllText(dialog.FileName);

                if (Editor.GetMarkdown().Trim().Length > 0)
                {
                    var replace = NotePromptWindow.Confirm(Window.GetWindow(this),
                        "Импорт Markdown",
                        "Заменить текущее содержимое заметки или добавить в конец?",
                        "Заменить", danger: false);

                    if (!replace)
                        markdown = Editor.GetMarkdown() + Environment.NewLine + Environment.NewLine + markdown;
                }

                Editor.LoadMarkdown(markdown, _characterId);
                Editor.MarkDirty();

                _debounceTimer?.Stop();
                _debounceTimer?.Start();

                SetStatus("Markdown импортирован", "B.Ok");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotesView] Ошибка импорта: {ex.Message}");
                SetStatus("ошибка импорта", "B.Danger");
            }
        }

        private void ShowHelp()
        {
            const string help =
                "Разметка (поддерживается Markdown):\n" +
                "  # Заголовок 1,  ## Заголовок 2,  ### Заголовок 3\n" +
                "  **жирный**,  *курсив*,  <u>подчёркнутый</u>,  ~~зачёркнутый~~\n" +
                "  ==выделение==,  `код`,  $формула$\n" +
                "  - список,  1. нумерованный список,  - [ ] чек-лист\n" +
                "  > цитата,   --- разделитель,   ``` блок кода\n" +
                "  [[Название заметки]] — wiki-ссылка (кликабельна, можно создать заметку)\n" +
                "  ![image|400](NoteImages/…/file.png) — картинка с шириной\n\n" +
                "Горячие клавиши:\n" +
                "  Ctrl+B — жирный,  Ctrl+I — курсив,  Ctrl+U — подчёркнутый\n" +
                "  Ctrl+Shift+U — зачёркнутый,  Ctrl+Shift+E — код\n" +
                "  Ctrl+Shift+1/2/3 — заголовки,  Ctrl+Shift+0 — обычный текст\n" +
                "  Ctrl+K — wiki-ссылка,  Ctrl+S — сохранить,  Ctrl+колесо — масштаб\n\n" +
                "Картинки и GIF:\n" +
                "  Перетащите файл прямо в текст или нажмите кнопку картинки.\n" +
                "  Клик по картинке — панель выбора размера. GIF анимируется.\n" +
                "  Ctrl+V вставляет картинку из буфера обмена.\n\n" +
                "Дерево:\n" +
                "  Кнопка «+ Группа» создаёт группу внутри выбранной (подгруппа).\n" +
                "  Перетаскивание меняет вложенность, ПКМ — контекстное меню.";

            NotePromptWindow.Message(Window.GetWindow(this), "Справка по заметкам", help);
        }

        #endregion

        #region === СТАТУС ===

        private void SetStatus(string text, string brushKey)
        {
            StatusText.Text = text;
            try
            {
                StatusText.Foreground = FindResource(brushKey) as System.Windows.Media.Brush
                                       ?? StatusText.Foreground;
            }
            catch
            {
                // Кисть может отсутствовать — не критично
            }
        }

        #endregion
    }
}
