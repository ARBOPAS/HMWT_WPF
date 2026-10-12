using HMWT_MUI.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HMWT_MUI.Windows
{
    public partial class TagManagerWindow : Window
    {
        private TagService _tagService;

        public TagManagerWindow()
        {
            InitializeComponent();

            // Масштаб интерфейса: применяется ко всем окнам одинаково
            Services.UiScaleManager.Register(this);
            _tagService = new TagService();

            btnAddTag.Click += BtnAddTag_Click;
            btnDeleteTag.Click += BtnDeleteTag_Click;
            btnClose.Click += (s, e) => Close();

            LoadTags();
        }

        private void LoadTags()
        {
            lbTags.Items.Clear();
            var tags = _tagService.GetAllTags();

            foreach (var tag in tags)
            {
                var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };

                var colorBox = new Border
                {
                    Width = 20,
                    Height = 20,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tag.TagColor)),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 10, 0)
                };

                var tagName = new TextBlock
                {
                    Text = tag.TagName,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };

                stackPanel.Children.Add(colorBox);
                stackPanel.Children.Add(tagName);

                var listBoxItem = new ListBoxItem { Content = stackPanel, Tag = tag.Id };
                lbTags.Items.Add(listBoxItem);
            }
        }

        private void BtnAddTag_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtNewTag.Text))
            {
                MessageDialog.Error(this, "Введите название тега!");
                return;
            }

            try
            {
                _tagService.CreateTag(txtNewTag.Text.Trim());
                txtNewTag.Text = "";
                LoadTags();
            }
            catch (Exception ex)
            {
                MessageDialog.Error(this, $"Ошибка создания тега: {ex.Message}");
            }
        }

        private void BtnDeleteTag_Click(object sender, RoutedEventArgs e)
        {
            if (lbTags.SelectedItem is ListBoxItem selectedItem && selectedItem.Tag != null)
            {
                int tagId = (int)selectedItem.Tag;

                bool confirmed = MessageDialog.Confirm(this, "Удалить этот тег?", "Подтверждение", "Удалить");

                if (confirmed)
                {
                    try
                    {
                        _tagService.DeleteTag(tagId);
                        LoadTags();
                    }
                    catch (Exception ex)
                    {
                        MessageDialog.Error(this, $"Ошибка удаления: {ex.Message}");
                    }
                }
            }
            else
            {
                MessageDialog.Info(this, "Выберите тег для удаления!", "Внимание");
            }
        }
    }
}