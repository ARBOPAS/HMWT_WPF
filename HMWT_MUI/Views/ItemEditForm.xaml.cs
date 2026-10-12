using HMWT_MUI.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMWT_MUI.Windows;

namespace HMWT_MUI.Views
{
    public partial class ItemEditForm : Window
    {
        private int _itemId;
        private int _characterId;
        private string _photoPath = "";
        private List<int> _selectedTagIds = new List<int>();
        private TagService _tagService;
        private Dictionary<int, CheckBox> _tagCheckBoxes = new Dictionary<int, CheckBox>();
       
        public ItemEditForm(int characterId, int itemId = 0)
        {
            InitializeComponent();
            _characterId = characterId;
            _itemId = itemId;  // ВАЖНО: сохраняем ID

            _tagService = new TagService();

            btnCancel.Click += (s, e) => DialogResult = false;
            btnSave.Click += BtnSave_Click;
            btnSelectPhoto.Click += BtnSelectPhoto_Click;

            LoadTags();

            if (itemId > 0)
            {
                Title = "Редактирование предмета";
                LoadItemData(itemId);
            }
            else
            {
                Title = "Новый предмет";
            }
        }

        private List<TagItem> _tagItems = new List<TagItem>();

        // Класс для绑定 чекбоксов в ComboBox
        public class TagItem
        {
            public int Id { get; set; }
            public string TagName { get; set; }
            public bool IsChecked { get; set; }
        }

        private void LoadTags()
        {
            _tagItems.Clear();
            var allTags = _tagService.GetAllTags();

            foreach (var tag in allTags)
            {
                _tagItems.Add(new TagItem
                {
                    Id = tag.Id,
                    TagName = tag.TagName,
                    IsChecked = false
                });
            }

            cbTags.ItemsSource = _tagItems;
            UpdateSelectedTagsCount();

            // Подписка на изменение чекбоксов
            foreach (var item in _tagItems)
            {
                // Обновляем счётчик при изменении
            }

            cbTags.DropDownOpened += (s, e) => UpdateSelectedTagsCount();
        }

        private void UpdateSelectedTagsCount()
        {
            int count = _tagItems.Count(t => t.IsChecked);
            txtSelectedTagsCount.Text = count == 0 ? "0 выбрано" :
                                        count == 1 ? "1 выбран" :
                                        $"{count} выбрано";
        }

        private void LoadItemData(int itemId)
        {
            // ... существующий код загрузки полей ...

            // Теги - отмечаем выбранные
            var  Tags= _tagService.GetItemTags(itemId);
            foreach (var tag in Tags)
            {
                var tagItem = _tagItems.FirstOrDefault(t => t.Id == tag.Id);
                if (tagItem != null)
                {
                    tagItem.IsChecked = true;
                }
            }

            // Обновляем UI
            cbTags.Items.Refresh();
            UpdateSelectedTagsCount();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageDialog.Error(this, "Введите название предмета!");
                return;
            }

            // Создаем itemData ЗДЕСЬ, в начале метода
            var itemData = new ItemData
            {
                Name = txtName.Text.Trim(),
                Category = (cbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Прочее",
                Description = txtDescription.Text.Trim(),
                Quantity = int.TryParse(txtQuantity.Text, out int qty) ? qty : 1,
                Weight = decimal.TryParse(txtWeight.Text, out decimal weight) ? weight : 0,
                CostGold = int.TryParse(txtCostGold.Text, out int gold) ? gold : 0,
                CostSilver = int.TryParse(txtCostSilver.Text, out int silver) ? silver : 0,
                CostCopper = int.TryParse(txtCostCopper.Text, out int copper) ? copper : 0,
                DamageDice = txtDamageDice.Text.Trim(),
                DamageType = (cbDamageType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "",
                ArmorClassBonus = int.TryParse(txtArmorBonus.Text, out int ac) ? ac : 0,
                MaxCharges = int.TryParse(txtMaxCharges.Text, out int maxCharges) ? maxCharges : 0,
                Rarity = (cbRarity.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Обычный",
                Notes = txtNotes.Text.Trim(),
                IsStackable = chkIsStackable.IsChecked ?? false,
                PhotoPath = _photoPath
            };

            var service = new InventoryService(_characterId);
            try
            {
                int savedItemId;
                if (_itemId == 0)
                {
                    var (exists, existingItemId, currentQuantity) = service.CheckDuplicateItem(itemData.Name, itemData.Category);
                    if (exists)
                    {
                        bool addToExisting = MessageDialog.Confirm(this,
                            $"Предмет \"{itemData.Name}\" ({itemData.Category}) уже существует (количество: {currentQuantity}).\n\n" +
                            $"Добавить {itemData.Quantity} шт. к существующему?",
                            "Дубликат предмета",
                            "Добавить");
                        if (addToExisting)
                        {
                            service.IncreaseItemQuantity(existingItemId, itemData.Quantity);
                            DialogResult = true;
                            return;
                        }
                    }
                    savedItemId = service.SaveItem(0, itemData);
                }
                else
                {
                    savedItemId = service.SaveItem(_itemId, itemData);
                }

                // Сохраняем теги
                if (_itemId > 0)
                {
                    var oldTags = _tagService.GetItemTags(_itemId);
                    foreach (var tag in oldTags)
                    {
                        _tagService.RemoveTagFromItem(_itemId, tag.Id);
                    }
                }

                foreach (var kvp in _tagCheckBoxes)
                {
                    if (kvp.Value.IsChecked == true)
                    {
                        _tagService.AssignTagToItem(savedItemId, kvp.Key);
                    }
                }

                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageDialog.Error(this, $"Ошибка сохранения: {ex.Message}");
            }
        }

        private void BtnSelectPhoto_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
                Title = "Выберите фото предмета"
            };

            if (dialog.ShowDialog() == true)
            {
                _photoPath = dialog.FileName;
                LoadPhotoPreview(_photoPath);
            }
        }

        private void LoadPhotoPreview(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                imgPreview.Source = bitmap;
            }
            catch
            {
                imgPreview.Source = null;
            }
        }


        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}