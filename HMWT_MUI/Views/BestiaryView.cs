using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Бестиарий: список своих существ слева, карточка справа.
    ///
    /// Существа хранятся только у того, кто их создал: по сети бестиарий не
    /// передаётся. В трекер инициативы уходит лишь имя и результат броска.
    /// </summary>
    public sealed class BestiaryView : UserControl
    {
        private readonly int _characterId;

        private readonly StackPanel _listHost = new StackPanel();
        private readonly StackPanel _cardHost = new StackPanel();
        private readonly TextBox _search = DmUiKit.Field("", 220, "Поиск: имя, тип, метка. Слова ищутся по отдельности");
        private readonly ComboBox _typeFilter = DmUiKit.Combo(new[] { "Все типы" }, "Все типы", false, 160);
        private readonly ComboBox _sizeFilter = DmUiKit.Combo(new[] { "Все размеры" }, "Все размеры", false, 150);
        private readonly CheckBox _onlyCustom = DmUiKit.Check("Только свои", false);
        private readonly ComboBox _sourceFilter = DmUiKit.Combo(new[] { "Все источники" }, "Все источники", false, 170);
        private readonly ComboBox _ratingFilter = DmUiKit.Combo(new[] { "Любой ПО" }, "Любой ПО", false, 150);
        private readonly TextBlock _countText = DmUiKit.Value("", 10.5, DmUiKit.B("Mute"));
        private readonly TextBlock _statusText = DmUiKit.Value("", 11, DmUiKit.B("Mute"));

        private Grid _root;
        private Border _editorLayer;
        private MonsterEditor _editor;

        private List<MonsterData> _all = new List<MonsterData>();
        private List<MonsterData> _shown = new List<MonsterData>();
        private MonsterData _selected;
        // Список рисуем порциями: 550 карточек сразу — это несколько секунд ожидания
        private const int PageSize = 60;
        private int _renderLimit = PageSize;

        /// <summary>Мастер нажал «в бой»: трекер должен добавить это существо.</summary>
        public event Action<MonsterData> AddToEncounterRequested;

        public BestiaryView(int characterId)
        {
            _characterId = characterId;
            BuildShell();
            Loaded += (s, e) => Refresh();
        }

        // ══════════════════════ ОБОЛОЧКА ══════════════════════

        private void BuildShell()
        {
            _root = new Grid { Background = Brushes.Transparent };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ── панель инструментов ──
            var toolbar = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(DmUiKit.Title("БЕСТИАРИЙ"));
            _countText.VerticalAlignment = VerticalAlignment.Bottom;
            _countText.Margin = new Thickness(12, 0, 0, 3);
            titleRow.Children.Add(_countText);
            Grid.SetColumn(titleRow, 0);
            topRow.Children.Add(titleRow);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(DmUiKit.Action("＋ Новое существо", (s, e) => OpenEditor(new MonsterData(), true),
                DmUiKit.ButtonKind.Primary, "Создать своё существо", 160, 30));
            buttons.Children.Add(DmUiKit.Action("Примеры", (s, e) => AddSamples(), DmUiKit.ButtonKind.Ghost,
                "Добавить гоблина и волка как образцы заполнения", 100, 30));
            buttons.Children.Add(DmUiKit.Action("Обновить", (s, e) => Refresh(), DmUiKit.ButtonKind.Ghost, null, 100, 30));
            buttons.Children.Add(DmUiKit.Action("Экспорт пака", (s, e) => ExportPack(), DmUiKit.ButtonKind.Ghost,
                "Сохранить бестиарий в файл .hmwtpack — его можно отдать другим", 130, 30));
            buttons.Children.Add(DmUiKit.Action("Импорт пака", (s, e) => ImportPack(), DmUiKit.ButtonKind.Ghost,
                "Добавить существ из файла .hmwtpack", 130, 30));
            Grid.SetColumn(buttons, 1);
            topRow.Children.Add(buttons);

            toolbar.Children.Add(topRow);

            var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _search.Margin = new Thickness(0, 0, 8, 0);
            _search.TextChanged += (s, e) => ApplyFilter();
            filters.Children.Add(_search);

            _typeFilter.SelectionChanged += (s, e) => ApplyFilter();
            _typeFilter.Margin = new Thickness(0, 0, 8, 0);
            filters.Children.Add(_typeFilter);

            _sizeFilter.SelectionChanged += (s, e) => ApplyFilter();
            _sizeFilter.Margin = new Thickness(0, 0, 8, 0);
            filters.Children.Add(_sizeFilter);

            _onlyCustom.Checked += (s, e) => ApplyFilter();
            _onlyCustom.Unchecked += (s, e) => ApplyFilter();
            filters.Children.Add(_onlyCustom);

            _sourceFilter.SelectionChanged += (s, e) => ApplyFilter();
            _sourceFilter.Margin = new Thickness(0, 0, 8, 0);
            filters.Children.Add(_sourceFilter);

            _ratingFilter.SelectionChanged += (s, e) => ApplyFilter();
            _ratingFilter.Margin = new Thickness(0, 0, 8, 0);
            filters.Children.Add(_ratingFilter);

            _statusText.VerticalAlignment = VerticalAlignment.Center;
            _statusText.Margin = new Thickness(12, 0, 0, 0);
            filters.Children.Add(_statusText);
            toolbar.Children.Add(filters);

            Grid.SetRow(toolbar, 0);
            _root.Children.Add(toolbar);

            // ── список и карточка ──
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 250 });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var listPanel = DmUiKit.Panel(DmUiKit.Scroll(_listHost), new Thickness(8), DmUiKit.B("Panel"), DmUiKit.B("Line"), 8);
            Grid.SetColumn(listPanel, 0);
            content.Children.Add(listPanel);

            var cardPanel = DmUiKit.Panel(DmUiKit.Scroll(_cardHost), new Thickness(14), DmUiKit.B("Panel"), DmUiKit.B("Line"), 8,
                new Thickness(10, 0, 0, 0));
            Grid.SetColumn(cardPanel, 1);
            content.Children.Add(cardPanel);

            Grid.SetRow(content, 1);
            _root.Children.Add(content);

            // ── слой редактора ──
            _editor = new MonsterEditor();
            _editor.Saved += saved =>
            {
                CloseEditor();
                Refresh();
                SelectMonster(saved.Id);
                DmRolls.Toast(this, $"Бестиарий обновлён: {saved.Name}");
            };
            _editor.Cancelled += CloseEditor;
            _editor.HorizontalAlignment = HorizontalAlignment.Center;
            _editor.VerticalAlignment = VerticalAlignment.Stretch;
            _editor.Width = Math.Min(1520, Math.Max(1080, SystemParameters.WorkArea.Width - 80));
            _editor.MaxWidth = _editor.Width;

            _editorLayer = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x05, 0x07, 0x0E)),
                Visibility = Visibility.Collapsed,
                Child = _editor
            };
            Panel.SetZIndex(_editorLayer, 100);
            Grid.SetRow(_editorLayer, 0);
            Grid.SetRowSpan(_editorLayer, 2);
            _root.Children.Add(_editorLayer);

            Content = _root;
        }

        // ══════════════════════ ДАННЫЕ ══════════════════════

        /// <summary>Перечитывает бестиарий из базы.</summary>
        public void Refresh()
        {
            try
            {
                _all = BestiaryService.LoadAllBrief();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BestiaryView] загрузка: {ex.Message}");
                _all = new List<MonsterData>();
            }

            RebuildFilterOptions();
            ApplyFilter();

            // Выбранное существо могло быть удалено с другой вкладки
            if (_selected != null)
            {
                var again = _all.FirstOrDefault(m => m.Id == _selected.Id);
                if (again == null) _selected = null;
                else _selected = again;
            }

            if (_selected == null && _shown.Count > 0) _selected = _shown[0];
            RenderCard();
        }

        private void RebuildFilterOptions()
        {
            string type = _typeFilter.SelectedItem as string ?? "Все типы";
            string size = _sizeFilter.SelectedItem as string ?? "Все размеры";

            _typeFilter.Items.Clear();
            _typeFilter.Items.Add("Все типы");
            foreach (string value in _all.Select(m => (m.CreatureType ?? "").Trim())
                                        .Where(v => v.Length > 0).Distinct()
                                        .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase))
                _typeFilter.Items.Add(value);
            _typeFilter.SelectedItem = _typeFilter.Items.Contains(type) ? type : "Все типы";

            _sizeFilter.Items.Clear();
            _sizeFilter.Items.Add("Все размеры");
            foreach (string value in _all.Select(m => (m.Size ?? "").Trim())
                                        .Where(v => v.Length > 0).Distinct()
                                        .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase))
                _sizeFilter.Items.Add(value);
            _sizeFilter.SelectedItem = _sizeFilter.Items.Contains(size) ? size : "Все размеры";

            // Источники: MM, VGtM, SRD и прочие, что реально есть в бестиарии
            string source = _sourceFilter.SelectedItem as string ?? "Все источники";
            _sourceFilter.Items.Clear();
            _sourceFilter.Items.Add("Все источники");
            foreach (string value in _all.Select(m => (m.Source ?? "").Trim())
                                        .Where(v => v.Length > 0).Distinct()
                                        .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase))
                _sourceFilter.Items.Add(value);
            _sourceFilter.SelectedItem = _sourceFilter.Items.Contains(source) ? source : "Все источники";

            // Уровень опасности: значения как в книге, по возрастанию
            string rating = _ratingFilter.SelectedItem as string ?? "Любой ПО";
            _ratingFilter.Items.Clear();
            _ratingFilter.Items.Add("Любой ПО");
            foreach (string value in _all.Select(m => (m.ChallengeRating ?? "").Trim())
                                        .Where(v => v.Length > 0).Distinct()
                                        .OrderBy(v => { DiceFormulaParser.TryParseChallenge(v, out double r, out _); return r; }))
                _ratingFilter.Items.Add(value);
            _ratingFilter.SelectedItem = _ratingFilter.Items.Contains(rating) ? rating : "Любой ПО";
        }

        private void ApplyFilter()
        {
            string type = _typeFilter.SelectedItem as string ?? "Все типы";
            string size = _sizeFilter.SelectedItem as string ?? "Все размеры";

            string source = _sourceFilter.SelectedItem as string ?? "Все источники";
            string rating = _ratingFilter.SelectedItem as string ?? "Любой ПО";

            _shown = BestiaryService.Filter(_all, _search.Text,
                type == "Все типы" ? "" : type,
                size == "Все размеры" ? "" : size,
                _onlyCustom.IsChecked == true,
                rating == "Любой ПО" ? "" : rating,
                rating == "Любой ПО" ? "" : rating,
                source == "Все источники" ? "" : source);

            _renderLimit = PageSize;
            _countText.Text = _all.Count == 0
                ? "пусто"
                : $"{_shown.Count} из {_all.Count}";

            _statusText.Text = _all.Count == 0
                ? "Бестиарий пуст — нажмите «Новое существо» или «Примеры»"
                : "";

            RenderList();
        }

        private void RenderList()
        {
            _listHost.Children.Clear();

            if (_shown.Count == 0)
            {
                _listHost.Children.Add(DmUiKit.EmptyState(
                    _all.Count == 0 ? "Бестиарий пуст" : "Ничего не найдено",
                    _all.Count == 0
                        ? "Создайте своё существо: заполните статблок один раз — и добавляйте его в бой одной кнопкой."
                        : "Измените запрос или сбросьте фильтры."));
                return;
            }

            foreach (var monster in _shown.Take(_renderLimit))
            {
                bool isSelected = _selected != null && _selected.Id == monster.Id;
                var row = new StackPanel();

                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock
                {
                    Text = monster.Name,
                    // Книжный шрифт и крупнее: при 100% масштабе список читался мелко
                    FontFamily = DmUiKit.Display,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = isSelected ? DmUiKit.B("Gold") : DmUiKit.B("Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 190
                });
                if (monster.IsCustom)
                {
                    var chip = DmUiKit.Chip("СВОЁ", DmUiKit.B("Ok"));
                    // У плашки свой отступ только справа — слева добавляем сами,
                    // иначе метка прилипает к имени
                    chip.Margin = new Thickness(6, 0, 6, 0);
                    head.Children.Add(chip);
                }
                row.Children.Add(head);

                string typeLine = monster.TypeLine();
                row.Children.Add(new TextBlock
                {
                    Text = typeLine.Length > 0 ? typeLine : "тип не указан",
                    FontSize = 11,
                    Foreground = DmUiKit.B("Mute"),
                    TextWrapping = TextWrapping.Wrap
                });

                var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                meta.Children.Add(DmUiKit.Chip($"ПО {monster.ChallengeRating}", DmUiKit.B("Arcane")));
                meta.Children.Add(DmUiKit.Chip($"КД {monster.ArmorClass}", DmUiKit.B("Frost")));
                meta.Children.Add(DmUiKit.Chip($"ХИТЫ {monster.HitPoints}", DmUiKit.B("Danger")));
                if (!string.IsNullOrWhiteSpace(monster.Source))
                {
                    var sourceChip = DmUiKit.Chip(monster.Source.ToUpperInvariant(), DmUiKit.B("Mute"));
                    sourceChip.ToolTip = MonsterCardRenderer.SourceTitle(monster.Source);
                    meta.Children.Add(sourceChip);
                }
                row.Children.Add(meta);

                var border = DmUiKit.Panel(row, new Thickness(10, 7, 10, 7),
                    isSelected ? DmUiKit.Fade(DmUiKit.B("Gold"), 0x1E) : DmUiKit.B("Slate"),
                    isSelected ? DmUiKit.B("Gold") : DmUiKit.B("Line"), 6,
                    new Thickness(0, 0, 0, 6));

                border.Cursor = Cursors.Hand;
                border.MouseLeftButtonUp += (s, e) =>
                {
                    // в списке лежит краткая запись — карточке нужны черты и действия
                    _selected = BestiaryService.Load(monster.Id) ?? monster;
                    RenderList();
                    RenderCard();
                };
                border.ToolTip = "Показать карточку";

                _listHost.Children.Add(border);
            }

            int left = _shown.Count - _renderLimit;
            if (left > 0)
            {
                var more = DmUiKit.Action($"Показать ещё ({left})", (s, e) =>
                {
                    _renderLimit += PageSize * 2;
                    RenderList();
                }, DmUiKit.ButtonKind.Ghost, "Догрузить остальных из бестиария", 200, 30);
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Margin = new Thickness(0, 4, 0, 4);
                _listHost.Children.Add(more);
            }
        }

        /// <summary>Выбирает существо по идентификатору (переход из трекера инициативы).</summary>
        public void SelectMonster(int id)
        {
            var monster = BestiaryService.Load(id) ?? _all.FirstOrDefault(m => m.Id == id);
            if (monster == null)
            {
                Refresh();
                monster = _all.FirstOrDefault(m => m.Id == id);
            }
            if (monster == null) return;

            if (!_shown.Any(m => m.Id == id))
            {
                // Существо скрыто фильтром — сбрасываем фильтры, иначе будет пусто
                _search.Text = "";
                _typeFilter.SelectedIndex = 0;
                _sizeFilter.SelectedIndex = 0;
                _onlyCustom.IsChecked = false;
                ApplyFilter();
            }

            _selected = monster;
            RenderList();
            RenderCard();
        }

        private void RenderCard()
        {
            _cardHost.Children.Clear();

            if (_selected == null)
            {
                _cardHost.Children.Add(DmUiKit.EmptyState("Существо не выбрано",
                    "Выберите запись слева, создайте новое существо или добавьте примеры."));
                return;
            }

            var monster = _selected;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            buttons.Children.Add(DmUiKit.Action("⚔ В бой", (s, e) => AddToEncounterRequested?.Invoke(monster),
                DmUiKit.ButtonKind.Primary, "Добавить жетон существа в трекер инициативы", 110, 30));
            buttons.Children.Add(DmUiKit.Action("✎ Редактировать", (s, e) => OpenEditor(monster, false),
                DmUiKit.ButtonKind.Ghost, null, 140, 30));
            buttons.Children.Add(DmUiKit.Action("Копия", (s, e) => Duplicate(monster), DmUiKit.ButtonKind.Ghost,
                "Сделать копию существа — удобно для «вождя»", 90, 30));
            buttons.Children.Add(DmUiKit.Action("Удалить", (s, e) => Delete(monster), DmUiKit.ButtonKind.Danger,
                "Удалить существо из бестиария", 100, 30));
            _cardHost.Children.Add(buttons);

            _cardHost.Children.Add(MonsterCardRenderer.Build(monster, this));
        }

        // ══════════════════════ ДЕЙСТВИЯ ══════════════════════

        // ══════════════════════ ПАКИ ══════════════════════

        /// <summary>
        /// Выгружает бестиарий в .hmwtpack. Перед сохранением спрашиваем
        /// название и источник — по ним потом видно, откуда данные.
        /// </summary>
        private void ExportPack()
        {
            try
            {
                var all = BestiaryService.LoadAll();
                if (all.Count == 0)
                {
                    Windows.MessageDialog.Info(this, "Бестиарий пуст — выгружать нечего.", "Экспорт пака");
                    return;
                }

                string name = Windows.NotePromptWindow.AskText(Window.GetWindow(this),
                    "Пак бестиария", "Название пака (кириллицей лучше не надо — это имя файла)", "Monster", "Далее");
                if (string.IsNullOrWhiteSpace(name)) return;

                string automatic = string.Join(", ", all
                    .Select(m => (m.Source ?? "").Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

                string sourceNote = Windows.NotePromptWindow.AskText(Window.GetWindow(this),
                    "Пак бестиария", "Откуда данные (книги, перевод, ссылка)", automatic, "Сохранить");
                if (sourceNote == null) return;

                var dialog = new SaveFileDialog
                {
                    Title = "Сохранить пак бестиария",
                    Filter = "Пак бестиария (*.hmwtpack)|*.hmwtpack|Все файлы (*.*)|*.*",
                    FileName = name.Trim() + BestiaryPackService.FileExtension,
                    DefaultExt = BestiaryPackService.FileExtension
                };
                if (dialog.ShowDialog() != true) return;

                if (BestiaryPackService.Export(dialog.FileName, all, name, sourceNote, out int images, out string error))
                {
                    DmRolls.Toast(this, $"Пак сохранён: существ {all.Count}, картинок {images}");
                    Windows.MessageDialog.Info(this,
                        $"Пак сохранён.\n\nФайл: {System.IO.Path.GetFileName(dialog.FileName)}\n" +
                        $"Существ: {all.Count}\nКартинок: {images}\n\n" +
                        "Внутри manifest.json — название, список источников и место под лицензию. " +
                        "Перед публикацией проверьте, что у вас есть право распространять текст и картинки: " +
                        "содержимое SRD идёт под CC-BY-4.0, всё остальное — нет.",
                        "Экспорт пака");
                }
                else
                {
                    Windows.MessageDialog.Error(this, error, "Не удалось сохранить пак");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BestiaryView] экспорт пака: {ex}");
                Windows.MessageDialog.Error(this, ex.Message, "Не удалось сохранить пак");
            }
        }

        /// <summary>Добавляет существ из пака; совпадающие по имени пропускает.</summary>
        private void ImportPack()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Открыть пак бестиария",
                    Filter = "Пак бестиария (*.hmwtpack)|*.hmwtpack|Все файлы (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;

                if (!BestiaryPackService.Import(dialog.FileName, out int added, out int skipped, out string error))
                {
                    Windows.MessageDialog.Error(this, error, "Не удалось прочитать пак");
                    return;
                }

                Refresh();
                DmRolls.Toast(this, $"Из пака добавлено: {added}, пропущено: {skipped}");
                if (added == 0 && skipped > 0)
                {
                    Windows.MessageDialog.Info(this,
                        "Все существа из пака уже есть в бестиарии — добавлять нечего.",
                        "Импорт пака");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BestiaryView] импорт пака: {ex}");
                Windows.MessageDialog.Error(this, ex.Message, "Не удалось прочитать пак");
            }
        }
        private void OpenEditor(MonsterData monster, bool isNew)
        {
            _editor.LoadMonster(monster, isNew);
            _editorLayer.Visibility = Visibility.Visible;
        }

        private void CloseEditor()
        {
            _editorLayer.Visibility = Visibility.Collapsed;
        }

        private void AddSamples()
        {
            bool confirmed = Windows.MessageDialog.Confirm(this,
                "Добавить в бестиарий гоблина и волка как образцы заполнения?",
                "Примеры существ", "Добавить");
            if (!confirmed) return;

            int added = BestiaryService.AddSamples();
            Refresh();
            DmRolls.Toast(this, added > 0
                ? $"Добавлено существ: {added}"
                : "Не удалось добавить примеры", added == 0);
        }

        private void Duplicate(MonsterData monster)
        {
            int id = BestiaryService.Duplicate(monster.Id, monster.Name + " (копия)");
            if (id <= 0)
            {
                DmRolls.Toast(this, "Не удалось сделать копию", true);
                return;
            }

            Refresh();
            SelectMonster(id);
            DmRolls.Toast(this, $"Создана копия: {monster.Name} (копия)");
        }

        private void Delete(MonsterData monster)
        {
            bool confirmed = Windows.MessageDialog.Confirm(this,
                $"Удалить существо «{monster.Name}» из бестиария?\n\n" +
                "Жетоны в трекере инициативы останутся: они хранят только имя и результат броска.",
                "Удаление существа", "Удалить");
            if (!confirmed) return;

            if (!BestiaryService.Delete(monster.Id))
            {
                DmRolls.Toast(this, "Не удалось удалить существо", true);
                return;
            }

            if (_selected != null && _selected.Id == monster.Id) _selected = null;
            Refresh();
            DmRolls.Toast(this, $"Существо «{monster.Name}» удалено");
        }
    }
}
