using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>Выбор уязвимости/сопротивления: справочник + свободный ввод.</summary>
    public partial class DefensePickerWindow : Window
    {
        public string SelectedKind { get; private set; } = DefenseKind.Resistance;
        public string SelectedName { get; private set; } = "";

        public DefensePickerWindow()
        {
            InitializeComponent();
            foreach (string kind in DefenseKind.All)
                cmbKind.Items.Add(DefenseKind.Label(kind));
            cmbKind.SelectedIndex = 1;
            Loaded += (_, __) => txtCustom.Focus();
        }

        private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstCatalog == null) return;
            int index = cmbKind.SelectedIndex;
            if (index < 0) index = 0;
            SelectedKind = DefenseKind.All[index];
            lstCatalog.Items.Clear();
            foreach (string name in DefenseCatalog.ForKind(SelectedKind))
                lstCatalog.Items.Add(name);
        }

        private void Catalog_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => Add_Click(sender, e);

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            string custom = (txtCustom.Text ?? "").Trim();
            string fromList = lstCatalog.SelectedItem as string ?? "";
            string name = !string.IsNullOrWhiteSpace(custom) ? custom : fromList;

            if (string.IsNullOrWhiteSpace(name))
            {
                txtError.Text = "Выбери из справочника или впиши своё название.";
                txtError.Visibility = Visibility.Visible;
                return;
            }

            SelectedKind = DefenseKind.All[System.Math.Max(0, cmbKind.SelectedIndex)];
            SelectedName = name;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
