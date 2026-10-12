using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HMWT_MUI.Services;
using Microsoft.Win32;

namespace HMWT_MUI.Controls
{
    /// <summary>
    /// Редактор профиля ведущего: никнейм, подпись и аватар.
    ///
    /// Показывается поверх всего окна. Нужен потому, что у ведущего может не быть
    /// персонажа, а в отряде он должен быть с именем и картинкой.
    /// </summary>
    public partial class DmProfileEditor : UserControl
    {
        /// <summary>Выбранный, но ещё не сохранённый файл аватара.</summary>
        private string _pendingPhoto;

        /// <summary>Убрать ли текущий аватар.</summary>
        private bool _clearPhoto;

        /// <summary>Что делать после сохранения (например, обновить комнату).</summary>
        public Action Saved { get; set; }

        /// <summary>Что делать при отмене и после сохранения — убрать редактор.</summary>
        public Action Closed { get; set; }

        public DmProfileEditor()
        {
            InitializeComponent();

            // Заголовок зависит от роли: окно одно и то же, но называть профиль
            // игрока «профилем ведущего» неверно
            ApplyRoleTitle();
            LoadCurrent();

            rootOverlay.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource == rootOverlay) Close();
            };
            rootOverlay.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) Close();
            };
            Loaded += (s, e) => txtName.Focus();
        }

        /// <summary>
        /// Заголовок по роли: у ведущего в комнате роль GameMaster.
        /// </summary>
        private void ApplyRoleTitle()
        {
            try
            {
                bool isGameMaster = Net.RoomSession.Instance.AmIGameMaster;

                txtTitle.Text = isGameMaster ? "ПРОФИЛЬ ВЕДУЩЕГО" : "ПРОФИЛЬ В ОТРЯДЕ";
                txtSubtitleHint.Text = isGameMaster
                    ? "Так вас увидят в отряде. Ведущий может играть без персонажа — тогда берётся это имя."
                    : "Так вас увидят в отряде. Имя можно взять от персонажа или задать своё.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmProfileEditor] заголовок: {ex.Message}");
            }
        }

        private void LoadCurrent()
        {
            txtName.Text = DmProfileService.DisplayName;
            txtSubtitle.Text = DmProfileService.Subtitle;
            ShowPhoto(DmProfileService.PhotoPath);
        }

        private void ShowPhoto(string relativePath)
        {
            string full = string.IsNullOrWhiteSpace(relativePath)
                ? null
                : HMWT_MUI.PhotoHelper.ResolvePhotoPath(relativePath);

            if (string.IsNullOrEmpty(full))
            {
                imgAvatar.Source = null;
                imgAvatar.Visibility = Visibility.Collapsed;
                txtAvatarLetter.Visibility = Visibility.Visible;
                txtAvatarLetter.Text = FirstLetter(txtName.Text);
                return;
            }

            try
            {
                // Первый кадр: аватарка может быть гифкой, в редакторе она статична
                imgAvatar.Source = GifFrameCache.GetFirstFrame(full);
                imgAvatar.Visibility = Visibility.Visible;
                txtAvatarLetter.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DmProfileEditor] аватар: {ex.Message}");
            }
        }

        private static string FirstLetter(string name)
            => string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();

        #region === ДЕЙСТВИЯ ===

        private void BtnChoosePhoto_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Выберите аватар ведущего",
                Filter = "Изображения (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|Все файлы (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            _pendingPhoto = dialog.FileName;
            _clearPhoto = false;
            ShowPhoto(_pendingPhoto);
        }

        private void BtnClearPhoto_Click(object sender, RoutedEventArgs e)
        {
            _pendingPhoto = null;
            _clearPhoto = true;
            ShowPhoto(null);
            txtAvatarLetter.Text = FirstLetter(txtName.Text);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string name = (txtName.Text ?? "").Trim();

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(_pendingPhoto) && !_clearPhoto
                && string.IsNullOrEmpty(DmProfileService.PhotoPath))
            {
                ShowHint("Укажите никнейм или выберите аватар — иначе в отряде вы останетесь безымянным.");
                return;
            }

            try
            {
                if (_clearPhoto) DmProfileService.ClearPhoto();
                else if (!string.IsNullOrEmpty(_pendingPhoto) && System.IO.File.Exists(_pendingPhoto))
                    DmProfileService.SavePhoto(_pendingPhoto);

                DmProfileService.Save(name, (txtSubtitle.Text ?? "").Trim());
            }
            catch (Exception ex)
            {
                ShowHint("Не удалось сохранить профиль: " + ex.Message);
                return;
            }

            Saved?.Invoke();
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowHint(string text)
        {
            txtHint.Text = text;
            txtHint.Visibility = Visibility.Visible;
        }

        private void Close()
        {
            Closed?.Invoke();
            var parent = Parent as Panel;
            parent?.Children.Remove(this);
        }

        #endregion
    }
}
