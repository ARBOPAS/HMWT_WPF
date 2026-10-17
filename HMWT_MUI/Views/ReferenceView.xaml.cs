using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Вкладка «Справка»: статьи справочника 5e SRD.
    ///
    /// Разделы берутся из самой базы — таблица SrdReference хранит их в поле
    /// category, поэтому здесь нет зашитого списка: что игрок импортировал,
    /// то и появилось. Тексты статей приходят готовыми от источника, программа
    /// их не переписывает.
    /// </summary>
    public partial class ReferenceView : UserControl
    {
        /// <summary>
        /// Подписи разделов. Ключ — имя файла базы, по нему же статьи ищутся
        /// в таблице. Незнакомый раздел показывается как есть, без подписи,
        /// чтобы новая версия базы не пропала из вкладки.
        /// </summary>
        private static readonly Dictionary<string, string> SectionTitles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ability-Scores"] = "Характеристики",
                ["Alignments"] = "Мировоззрения",
                ["Backgrounds"] = "Предыстории",
                ["Classes"] = "Классы",
                ["Subclasses"] = "Подклассы",
                ["Conditions"] = "Состояния",
                ["Damage-Types"] = "Типы урона",
                ["Equipment-Categories"] = "Категории снаряжения",
                ["Feats"] = "Черты",
                ["Features"] = "Умения классов",
                ["Languages"] = "Языки",
                ["Levels"] = "Уровни",
                ["Magic-Schools"] = "Школы магии",
                ["Poisons"] = "Яды",
                ["Proficiencies"] = "Владения",
                ["Races"] = "Расы",
                ["Subraces"] = "Подрасы",
                ["Species"] = "Виды",
                ["Subspecies"] = "Подвиды",
                ["Rules"] = "Правила",
                ["Skills"] = "Навыки",
                ["Traits"] = "Умения",
                ["Weapon-Properties"] = "Свойства оружия",
                ["Weapon-Mastery-Properties"] = "Свойства мастерства оружия"
            };

        /// <summary>Порядок разделов на вкладке: сначала то, что смотрят чаще.</summary>
        private static readonly string[] SectionOrder =
        {
            "Conditions", "Skills", "Races", "Species", "Classes", "Backgrounds",
            "Feats", "Features", "Traits", "Spells", "Equipment-Categories",
            "Weapon-Properties", "Weapon-Mastery-Properties", "Magic-Schools",
            "Damage-Types", "Poisons", "Languages", "Alignments", "Ability-Scores",
            "Proficiencies", "Rules", "Levels", "Subclasses", "Subraces", "Subspecies"
        };

        private List<SrdEntry> _entries = new List<SrdEntry>();

        public ReferenceView()
        {
            InitializeComponent();

            // Разделы читаем сразу, а не по событию Loaded: вкладка должна быть
            // готовой к моменту показа, иначе первый кадр пустой
            // Кнопки правки подключаем кодом: так же сделано в остальных видах
            btnEditEntry.Click += BtnEditEntry_Click;
            btnNewEntry.Click += BtnNewEntry_Click;
            btnDeleteEntry.Click += BtnDeleteEntry_Click;
            btnRefSave.Click += BtnRefSave_Click;
            btnRefCancel.Click += BtnRefCancel_Click;

            LoadSections();
        }

        /// <summary>Заполняет список разделов тем, что реально лежит в базе.</summary>
        private void LoadSections()
        {
            List<string> categories;
            try { categories = SrdImportService.GetCategories(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Справка] разделы: {ex.Message}");
                categories = new List<string>();
            }

            if (categories.Count == 0)
            {
                pnlEmpty.Visibility = Visibility.Visible;
                return;
            }

            pnlEmpty.Visibility = Visibility.Collapsed;
            categories.Sort(CompareSections);

            foreach (var category in categories)
                lstCategories.Items.Add(new ListBoxItem
                {
                    Content = TitleOf(category),
                    Tag = category
                });

            lstCategories.SelectedIndex = 0;
        }

        /// <summary>Сначала разделы из привычного порядка, потом остальные по алфавиту.</summary>
        private static int CompareSections(string left, string right)
        {
            int leftIndex = Array.IndexOf(SectionOrder, left);
            int rightIndex = Array.IndexOf(SectionOrder, right);

            if (leftIndex < 0) leftIndex = int.MaxValue;
            if (rightIndex < 0) rightIndex = int.MaxValue;
            if (leftIndex != rightIndex) return leftIndex.CompareTo(rightIndex);

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string TitleOf(string category)
            => SectionTitles.TryGetValue(category, out string title) ? title : category;

        private void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Поле поиска относится к разделу, поэтому при смене раздела очищается
            if (txtSearch.Text.Length > 0) txtSearch.Text = "";
            else ReloadEntries();
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => ReloadEntries();

        private void ReloadEntries()
        {
            string category = SelectedCategory;
            lstEntries.Items.Clear();
            _entries = new List<SrdEntry>();
            txtTitle.Text = "";
            txtBody.Text = "";
            txtSource.Text = "";

            if (category.Length == 0) return;

            try { _entries = SrdImportService.Search(category, txtSearch.Text.Trim()); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Справка] статьи: {ex.Message}");
            }

            foreach (var entry in _entries) lstEntries.Items.Add(entry.Name);

            txtCount.Text = _entries.Count == 0
                ? "Ничего не найдено"
                : $"Статей: {_entries.Count}";

            if (lstEntries.Items.Count > 0) lstEntries.SelectedIndex = 0;
        }

        private void Entries_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = lstEntries.SelectedIndex;
            if (index < 0 || index >= _entries.Count) return;

            var entry = _entries[index];
            txtTitle.Text = entry.Name;
            txtBody.Text = string.IsNullOrWhiteSpace(entry.Description)
                ? "У этой статьи в базе только название."
                : entry.Description;

            // Источник указывается рядом с текстом: справочные тексты приходят
            // из открытой базы и требуют указания авторства
            txtSource.Text = string.IsNullOrWhiteSpace(entry.Publisher)
                ? entry.Book
                : $"{entry.Book} · {entry.Publisher}";
        }

        /// <summary>Номер правимой статьи; 0 — создаём новую.</summary>
        private long _editingId;

        private void BtnEditEntry_Click(object sender, RoutedEventArgs e)
        {
            int index = lstEntries.SelectedIndex;
            if (index < 0 || index >= _entries.Count) { txtCount.Text = "Сначала выберите статью"; return; }
            OpenEditor(_entries[index]);
        }

        private void BtnNewEntry_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCategory.Length == 0) { txtCount.Text = "Сначала выберите раздел"; return; }
            OpenEditor(null);
        }

        private void OpenEditor(SrdEntry entry)
        {
            _editingId = entry?.Id ?? 0;
            edRefName.Text = entry?.Name ?? "";
            edRefBody.Text = entry?.Description ?? "";
            edRefBook.Text = entry?.Book ?? "";
            edRefPublisher.Text = entry?.Publisher ?? "";
            pnlEditEntry.Visibility = Visibility.Visible;
            edRefName.Focus();
        }

        private void BtnRefCancel_Click(object sender, RoutedEventArgs e)
            => pnlEditEntry.Visibility = Visibility.Collapsed;

        private void BtnRefSave_Click(object sender, RoutedEventArgs e)
        {
            string name = edRefName.Text.Trim();
            if (name.Length == 0) { txtCount.Text = "У статьи должно быть название"; edRefName.Focus(); return; }

            try
            {
                if (_editingId > 0)
                    SrdImportService.UpdateEntry(_editingId, name, edRefBody.Text,
                        edRefBook.Text.Trim(), edRefPublisher.Text.Trim());
                else
                    SrdImportService.AddEntry(SelectedCategory, name, edRefBody.Text,
                        edRefBook.Text.Trim(), edRefPublisher.Text.Trim());

                pnlEditEntry.Visibility = Visibility.Collapsed;
                ReloadEntries();

                // выделяем то, что только что сохранили
                for (int i = 0; i < _entries.Count; i++)
                    if (string.Equals(_entries[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    { lstEntries.SelectedIndex = i; break; }

                txtCount.Text = _editingId > 0 ? "Статья сохранена" : "Статья добавлена";
            }
            catch (Exception ex)
            {
                txtCount.Text = "Не сохранилось: " + ex.Message;
            }
        }

        private void BtnDeleteEntry_Click(object sender, RoutedEventArgs e)
        {
            int index = lstEntries.SelectedIndex;
            if (index < 0 || index >= _entries.Count) { txtCount.Text = "Сначала выберите статью"; return; }

            var entry = _entries[index];
            if (!Windows.MessageDialog.Confirm(this, $"Убрать статью «{entry.Name}» из справочника?", "Удаление"))
                return;

            try
            {
                SrdImportService.DeleteEntry(entry.Id);
                pnlEditEntry.Visibility = Visibility.Collapsed;
                ReloadEntries();
            }
            catch (Exception ex)
            {
                txtCount.Text = "Не удалилось: " + ex.Message;
            }
        }

        private string SelectedCategory
        {
            get
            {
                if (lstCategories.SelectedItem is ListBoxItem item && item.Tag is string category)
                    return category;

                return "";
            }
        }
    }
}
