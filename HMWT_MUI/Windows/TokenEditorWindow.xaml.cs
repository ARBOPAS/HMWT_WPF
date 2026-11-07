using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Небольшое окно «Жетон»: имя, картинка, размер и видимость.
    ///
    /// Нужно для жетонов, которых нет в бестиарии: «Старик» за стойкой или
    /// бочка в углу. Раньше такие метки приходилось заводить через трекер
    /// инициативы, и бой засорялся существами, которые в нём не участвуют.
    ///
    /// Окно одно и для создания, и для правки: у жетона с карты «Картинка
    /// жетона…» открывает его же.
    /// </summary>
    public partial class TokenEditorWindow : Window
    {
        private string _photoAbsolute;
        private bool _built;

        public TokenEditorWindow()
        {
            InitializeComponent();

            // ½ клетки — для мелких существ и предметов: кот, кувшин, свеча.
            // Остальные размеры — целые клетки от 1 до 10.
            SizeBox.Items.Add("½ клетки");
            for (int n = 1; n <= 10; n++)
                SizeBox.Items.Add(n == 1 ? "1 клетка" : (n < 5 ? n + " клетки" : n + " клеток"));
            SizeBox.SelectedIndex = 1;
            _built = true;

            Loaded += (s, e) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };
        }

        /// <summary>Имя жетона (пустое заменяется на «Жетон»).</summary>
        public string TokenLabel
        {
            get
            {
                string text = (NameBox.Text ?? "").Trim();
                return text.Length > 0 ? text : "Жетон";
            }
        }

        /// <summary>Полный путь к выбранной картинке; пусто — картинки нет.</summary>
        public string PhotoFile => _photoAbsolute ?? "";

        /// <summary>Размер в клетках: 0.5 — половина клетки, дальше 1…10.</summary>
        public double SizeCells => SizeBox.SelectedIndex <= 0
            ? 0.5
            : Math.Max(1, Math.Min(10, SizeBox.SelectedIndex));

        public bool IsHidden => HiddenBox.IsChecked == true;

        /// <summary>Добавить картинку в библиотеку, чтобы ставить её снова.</summary>
        public bool SaveToLibrary => LibraryBox.IsChecked == true;

        /// <summary>Заполняет окно значениями. photoAbsolute — полный путь к файлу.</summary>
        public void LoadValues(string label, string photoAbsolute, double sizeCells, bool hidden)
        {
            if (!_built) return;

            NameBox.Text = label ?? "";
            _photoAbsolute = string.IsNullOrWhiteSpace(photoAbsolute) ? null : photoAbsolute;
            SizeBox.SelectedIndex = sizeCells <= 0.75
                ? 0
                : Math.Max(1, Math.Min(10, (int)Math.Round(sizeCells)));
            HiddenBox.IsChecked = hidden;
            UpdatePreview();
        }

        /// <summary>Кнопка подтверждения: «Поставить» или «Сохранить».</summary>
        public void SetOkText(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) OkButton.Content = text;
        }

        private void UpdatePreview()
        {
            try
            {
                if (string.IsNullOrEmpty(_photoAbsolute) || !File.Exists(_photoAbsolute))
                {
                    PreviewImage.Source = null;
                    PhotoHint.Visibility = Visibility.Visible;
                    return;
                }

                // Читаем через память: файл не остаётся заблокированным, и его
                // можно скопировать в папку жетонов сразу после выбора.
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(File.ReadAllBytes(_photoAbsolute));
                image.EndInit();
                image.Freeze();

                PreviewImage.Source = image;
                PhotoHint.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TokenEditor] предпросмотр: {ex.Message}");
                PreviewImage.Source = null;
                PhotoHint.Visibility = Visibility.Visible;
            }
        }

        private void PickPhoto_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Картинка жетона",
                Filter = "Картинки|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Все файлы|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            _photoAbsolute = dialog.FileName;
            UpdatePreview();
        }

        private void ClearPhoto_Click(object sender, RoutedEventArgs e)
        {
            _photoAbsolute = null;
            UpdatePreview();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = "Жетон";
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }
    }
}
