using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Окно выбора заметки для вставки wiki-ссылки [[...]].
    /// </summary>
    public partial class WikiPickerWindow : Window
    {
        private readonly List<string> _allTitles;
        private List<string> _filtered;

        public string SelectedTitle { get; private set; }

        private WikiPickerWindow(IEnumerable<string> titles, string currentNoteTitle, bool attachmentsMode)
        {
            InitializeComponent();

            _allTitles = titles?.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList() ?? new List<string>();
            _filtered = new List<string>(_allTitles);

            if (attachmentsMode)
            {
                HeaderText.Text = "Вставка ссылки на вложение";
                SearchBox.Text = "";
                HintText.Text = "Введите путь или имя файла";
            }
            else
            {
                HeaderText.Text = "Вставка ссылки [[…]]";
                HintText.Text = "Двойной клик — вставить";
                if (!string.IsNullOrEmpty(currentNoteTitle))
                    HintText.Text += " • текущая: " + currentNoteTitle;
            }

            RefreshList();
            Loaded += (s, e) => SearchBox.Focus();
        }

        /// <summary>Показывает список заметок. Возвращает выбранный заголовок или null.</summary>
        public static string Pick(Window owner, IEnumerable<string> titles, string currentNoteTitle)
        {
            var window = new WikiPickerWindow(titles, currentNoteTitle, false)
            {
                Owner = owner ?? Application.Current?.MainWindow
            };
            return window.ShowDialog() == true ? window.SelectedTitle : null;
        }

        /// <summary>Режим ввода пути вложения (без списка заметок).</summary>
        public static string PickAttachmentName(Window owner)
        {
            var window = new WikiPickerWindow(new List<string>(), null, true)
            {
                Owner = owner ?? Application.Current?.MainWindow
            };
            return window.ShowDialog() == true ? window.SelectedTitle : null;
        }

        private void RefreshList()
        {
            ItemsList.Items.Clear();
            foreach (var title in _filtered)
                ItemsList.Items.Add(title);

            if (ItemsList.Items.Count > 0)
                ItemsList.SelectedIndex = 0;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = (SearchBox.Text ?? "").Trim();
            _filtered = string.IsNullOrEmpty(query)
                ? new List<string>(_allTitles)
                : _allTitles.Where(t => t.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            RefreshList();
        }

        private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Confirm();
        }

        private void ItemsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Confirm();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Confirm();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Confirm()
        {
            var typed = (SearchBox.Text ?? "").Trim();
            var selected = ItemsList.SelectedItem as string;

            // Если пользователь что-то набрал и в списке нет точного совпадения — берём набранное
            if (!string.IsNullOrEmpty(typed) &&
                (selected == null || !string.Equals(selected, typed, StringComparison.CurrentCultureIgnoreCase)))
            {
                if (_filtered.Count == 0)
                {
                    SelectedTitle = typed;
                    DialogResult = true;
                    return;
                }
            }

            SelectedTitle = selected ?? typed;
            if (string.IsNullOrWhiteSpace(SelectedTitle))
            {
                DialogResult = false;
                return;
            }

            DialogResult = true;
        }
    }
}
