using System;
using System.Windows;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>Ручное добавление черты для хоумбрю.</summary>
    public partial class FeatureEditorWindow : Window
    {
        public FeatureEntry Result { get; private set; }

        public FeatureEditorWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => txtName.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = (txtName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                txtError.Text = "Название не может быть пустым.";
                txtError.Visibility = Visibility.Visible;
                return;
            }

            int level = 0;
            int.TryParse((txtLevel.Text ?? "").Trim(), out level);
            Result = new FeatureEntry
            {
                Name = name,
                Source = (txtSource.Text ?? "").Trim(),
                Kind = "Custom",
                Level = Math.Max(0, level),
                Description = (txtDescription.Text ?? "").Trim()
            };
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
