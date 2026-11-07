using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMWT_MUI.Services;
using Microsoft.Win32;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// «Призывы» — краткий лист существ, которыми управляет персонаж.
    ///
    /// Нужно игрокам: призванный водный элементаль или волк идут на карту
    /// своим жетоном, и двигает его игрок, а не мастер. Лист короткий, как в
    /// рулбуке: размер, ХП, КД, скорость, характеристики, атаки и особенности.
    /// </summary>
    public partial class SummonWindow : Window
    {
        private readonly int _characterId;
        private readonly List<SummonData> _items = new List<SummonData>();

        private SummonData _selected;
        private string _pickedPhotoFile;
        private bool _photoCleared;

        /// <summary>Что просят поставить на карту (после «На карту»).</summary>
        public SummonData Result { get; private set; }

        /// <summary>Нажали «На карту»: окно закрылось с готовым листом.</summary>
        public bool PlaceRequested { get; private set; }

        public SummonWindow(int characterId)
        {
            InitializeComponent();

            _characterId = characterId;

            SizeBox.Items.Add("½ клетки");
            for (int n = 1; n <= 10; n++)
                SizeBox.Items.Add(n == 1 ? "1 клетка" : (n < 5 ? n + " клетки" : n + " клеток"));
            SizeBox.SelectedIndex = 1;

            RefreshList();
            NewItem();
        }

        // ── список ──

        private void RefreshList()
        {
            int keepId = _selected?.Id ?? 0;

            _items.Clear();
            _items.AddRange(SummonService.Load(_characterId));

            string query = (SearchBox?.Text ?? "").Trim();
            var shown = query.Length == 0
                ? _items
                : _items.Where(s => (s.Name ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        .ToList();

            List.Items.Clear();
            foreach (var summon in shown)
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock
                {
                    Text = summon.Name,
                    Foreground = (Brush)FindResource("B.Ink"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                if (!string.IsNullOrWhiteSpace(summon.Summary))
                {
                    text.Children.Add(new TextBlock
                    {
                        Text = summon.Summary,
                        Foreground = (Brush)FindResource("B.Frost"),
                        FontSize = 10.5,
                        Margin = new Thickness(0, 2, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                }

                List.Items.Add(new ListBoxItem { Content = text, Tag = summon });
            }

            bool empty = shown.Count == 0;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = _items.Count == 0
                ? "Пока пусто. Нажмите «Новый» — или возьмите готовый лист водного элементаля."
                : "По запросу «" + query + "» ничего не найдено.";

            StatusText.Text = _items.Count == 0
                ? ""
                : query.Length == 0
                    ? "Призывов: " + _items.Count
                    : "Найдено: " + shown.Count + " из " + _items.Count;

            if (keepId > 0)
            {
                var again = List.Items.OfType<ListBoxItem>()
                    .FirstOrDefault(i => (i.Tag as SummonData)?.Id == keepId);
                if (again != null) List.SelectedItem = again;
            }
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

        private void New_Click(object sender, RoutedEventArgs e) => NewItem();

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (List.SelectedItem is ListBoxItem item && item.Tag is SummonData summon)
                ShowItem(summon);
        }

        // ── форма ──

        private void ShowItem(SummonData summon)
        {
            _selected = summon;
            _pickedPhotoFile = null;
            _photoCleared = false;
            ErrorText.Visibility = Visibility.Collapsed;

            NameBox.Text = summon.Name ?? "";
            TypeBox.Text = summon.CreatureType ?? "";
            HpBox.Text = summon.Hp.ToString();
            MaxHpBox.Text = summon.MaxHp.ToString();
            AcBox.Text = summon.Ac.ToString();
            SpeedBox.Text = summon.Speed ?? "";
            StrBox.Text = summon.Str.ToString();
            DexBox.Text = summon.Dex.ToString();
            ConBox.Text = summon.Con.ToString();
            IntBox.Text = summon.Int.ToString();
            WisBox.Text = summon.Wis.ToString();
            ChaBox.Text = summon.Cha.ToString();
            AttacksBox.Text = summon.Attacks ?? "";
            AbilitiesBox.Text = summon.Abilities ?? "";

            SetSizeCombo(summon.SizeCells);
            ShowPhoto(PhotoHelper.ResolvePhotoPath(summon.PhotoPath));
        }

        private void NewItem()
        {
            _selected = null;
            _pickedPhotoFile = null;
            _photoCleared = false;
            List.SelectedItem = null;
            ErrorText.Visibility = Visibility.Collapsed;

            NameBox.Text = "";
            TypeBox.Text = "";
            HpBox.Text = "0";
            MaxHpBox.Text = "0";
            AcBox.Text = "0";
            SpeedBox.Text = "";
            StrBox.Text = "10";
            DexBox.Text = "10";
            ConBox.Text = "10";
            IntBox.Text = "10";
            WisBox.Text = "10";
            ChaBox.Text = "10";
            AttacksBox.Text = "";
            AbilitiesBox.Text = "";
            SetSizeCombo(1);
            ShowPhoto(null);
            NameBox.Focus();
        }

        private void SetSizeCombo(double sizeCells)
        {
            SizeBox.SelectedIndex = sizeCells <= 0.75
                ? 0
                : Math.Max(1, Math.Min(10, (int)Math.Round(sizeCells)));
        }

        private double ReadSizeCombo()
            => SizeBox.SelectedIndex <= 0 ? 0.5 : Math.Max(1, Math.Min(10, SizeBox.SelectedIndex));

        private void ShowPhoto(string fullPath)
        {
            bool has = !string.IsNullOrEmpty(fullPath) && File.Exists(fullPath);
            PhotoPreview.Source = has ? PhotoHelper.LoadUnlocked(fullPath) : null;
            PhotoHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            ClearPhotoButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PickPhoto_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Картинка призыва",
                Filter = "Картинки|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|Все файлы|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            _pickedPhotoFile = dialog.FileName;
            _photoCleared = false;
            ShowPhoto(dialog.FileName);
        }

        private void ClearPhoto_Click(object sender, RoutedEventArgs e)
        {
            _pickedPhotoFile = null;
            _photoCleared = true;
            ShowPhoto(null);
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            var preset = SummonService.WaterElemental(_characterId);
            ShowItem(preset);
            _selected = null;          // это шаблон, а не запись из базы
            List.SelectedItem = null;
            StatusText.Text = "Заполнен лист водного элементаля — сохраните под своим именем";
        }

        // ── сохранение и карта ──

        private SummonData ReadForm()
        {
            return new SummonData
            {
                Id = _selected?.Id ?? 0,
                CharacterId = _characterId,
                Name = (NameBox.Text ?? "").Trim(),
                CreatureType = (TypeBox.Text ?? "").Trim(),
                SizeCells = ReadSizeCombo(),
                Hp = ToInt(HpBox.Text),
                MaxHp = ToInt(MaxHpBox.Text),
                Ac = ToInt(AcBox.Text),
                Speed = (SpeedBox.Text ?? "").Trim(),
                Str = ToInt(StrBox.Text),
                Dex = ToInt(DexBox.Text),
                Con = ToInt(ConBox.Text),
                Int = ToInt(IntBox.Text),
                Wis = ToInt(WisBox.Text),
                Cha = ToInt(ChaBox.Text),
                Attacks = (AttacksBox.Text ?? "").Trim(),
                Abilities = (AbilitiesBox.Text ?? "").Trim(),
                PhotoPath = _selected?.PhotoPath ?? ""
            };
        }

        /// <summary>Сохраняет лист. Возвращает запись или null, если не вышло.</summary>
        private SummonData SaveCurrent()
        {
            var data = ReadForm();

            if (string.IsNullOrWhiteSpace(data.Name))
            {
                ErrorText.Text = "Укажите имя — по нему призыв ищется в списке.";
                ErrorText.Visibility = Visibility.Visible;
                NameBox.Focus();
                return null;
            }

            if (data.MaxHp <= 0) data.MaxHp = Math.Max(0, data.Hp);
            if (data.Hp <= 0 && data.MaxHp > 0) data.Hp = data.MaxHp;

            int id = SummonService.Save(data);
            if (id <= 0)
            {
                ErrorText.Text = "Не удалось сохранить призыв.";
                ErrorText.Visibility = Visibility.Visible;
                return null;
            }

            // Картинка копируется в папку данных после сохранения: нужен id записи
            if (_pickedPhotoFile != null)
            {
                string relative = SummonService.ImportPhoto(id, _pickedPhotoFile);
                if (!string.IsNullOrEmpty(relative))
                {
                    data.PhotoPath = relative;
                    SummonService.Save(data);
                }
            }
            else if (_photoCleared && !string.IsNullOrWhiteSpace(data.PhotoPath))
            {
                string old = PhotoHelper.ResolvePhotoPath(data.PhotoPath);
                try
                {
                    if (!string.IsNullOrEmpty(old) && File.Exists(old)) File.Delete(old);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Призывы] старая картинка: {ex.Message}");
                }

                data.PhotoPath = "";
                SummonService.Save(data);
            }

            _selected = data;
            _pickedPhotoFile = null;
            _photoCleared = false;
            ErrorText.Visibility = Visibility.Collapsed;
            return data;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var saved = SaveCurrent();
            if (saved == null) return;

            RefreshList();
            StatusText.Text = "Сохранено: " + saved.Name;
        }

        private void Place_Click(object sender, RoutedEventArgs e)
        {
            var saved = SaveCurrent();
            if (saved == null) return;

            Result = saved;
            PlaceRequested = true;
            DialogResult = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _selected.Id <= 0)
            {
                StatusText.Text = "Выберите призыв в списке — новый удалять нечего";
                return;
            }

            string text = "Удалить призыв «" + _selected.Name + "»?";
            if (!MessageDialog.Confirm(this, text, "Призывы", "Удалить", "Отмена", true))
                return;

            SummonService.Delete(_selected.Id);
            _selected = null;
            RefreshList();
            NewItem();
            StatusText.Text = "Призыв удалён";
        }

        // ── окно ──

        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
        }

        private static int ToInt(string raw)
            => int.TryParse((raw ?? "").Trim(), out int value) ? value : 0;
    }
}
