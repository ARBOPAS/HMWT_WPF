using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Редактор базы: показывает любую таблицу и позволяет её править.
    ///
    /// Нужен «на случай, если приспичило»: поправить значение, которого нет в
    /// обычных окнах, или залезть в служебные таблицы. Поэтому здесь всё честно:
    /// правка идёт прямо в базу, отменить её нельзя, и перед изменениями
    /// предлагается сделать копию базы.
    ///
    /// Копия пригодится и при обновлении программы: заполненный лист персонажа
    /// можно сохранить файлом, а потом загрузить обратно.
    /// </summary>
    public partial class DatabaseEditorView : UserControl
    {
        /// <summary>Имя колонки с номером строки: по нему правим и удаляем.</summary>
        private const string RowIdColumn = "№ строки";

        private DataTable _table;
        private string _tableName = "";

        public DatabaseEditorView()
        {
            InitializeComponent();

            btnReload.Click += (_, __) => LoadTable(_tableName);
            btnSave.Click += BtnSave_Click;
            btnAddRow.Click += BtnAddRow_Click;
            btnDeleteRow.Click += BtnDeleteRow_Click;
            btnBackup.Click += BtnBackup_Click;
            btnRestore.Click += BtnRestore_Click;

            txtWhere.Text = "Файл базы: " + DatabaseHelper.GetDatabasePath();
            LoadTables();
        }

        /// <summary>Список таблиц берём из самой базы, а не из жёсткого перечня.</summary>
        private void LoadTables()
        {
            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader[0]?.ToString() ?? "";
                    if (name.Length > 0) lstTables.Items.Add(name);
                }

                if (lstTables.Items.Count > 0) lstTables.SelectedIndex = 0;
                else Status("В базе нет таблиц");
            }
            catch (Exception ex)
            {
                Status("Не удалось прочитать список таблиц: " + ex.Message);
            }
        }

        private void Tables_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => LoadTable(lstTables.SelectedItem as string);

        private void LoadTable(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                using var conn = DatabaseHelper.GetConnection();
                conn.Open();

                // rowid есть у обычных таблиц: по нему потом точно найдём строку
                using var cmd = new SqliteCommand($"SELECT rowid AS \"{RowIdColumn}\", * FROM \"{name}\"", conn);
                using var reader = cmd.ExecuteReader();

                var table = new DataTable(name);
                for (int i = 0; i < reader.FieldCount; i++)
                    table.Columns.Add(reader.GetName(i), typeof(object));

                while (reader.Read())
                {
                    var row = table.NewRow();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                    table.Rows.Add(row);
                }

                // Запоминаем прочитанное как «исходное»: тогда сохранятся только
                // те строки, которые игрок действительно тронул
                table.AcceptChanges();

                _table = table;
                _tableName = name;
                gridTable.ItemsSource = table.DefaultView;
                Status($"Таблица «{name}»: строк {table.Rows.Count}. Правьте ячейки и нажмите «Сохранить».");
            }
            catch (Exception ex)
            {
                Status("Не удалось открыть таблицу: " + ex.Message);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_table == null) { Status("Сначала выберите таблицу"); return; }

            DataTable changes = _table.GetChanges();
            if (changes == null) { Status("Изменений нет"); return; }

            try
            {
                int updated = 0, inserted = 0, deleted = 0;

                using var conn = DatabaseHelper.GetConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                // Колонки берём у таблицы изменений: у неё тот же состав, что у
                // строки, и не бывает расхождения имён
                var columns = changes.Columns.Cast<DataColumn>()
                    .Where(c => c.ColumnName != RowIdColumn)
                    .ToList();

                foreach (DataRow row in changes.Rows)
                {
                    if (row.RowState == DataRowState.Deleted)
                    {
                        object rowId = row[RowIdColumn, DataRowVersion.Original];
                        if (rowId == null || rowId == DBNull.Value) continue;

                        using var del = new SqliteCommand($"DELETE FROM \"{_tableName}\" WHERE rowid = @r", conn, tx);
                        del.Parameters.AddWithValue("@r", rowId);
                        del.ExecuteNonQuery();
                        deleted++;
                    }
                    else if (row.RowState == DataRowState.Added)
                    {
                        string names = string.Join(", ", columns.Select(c => "\"" + c.ColumnName + "\""));
                        string values = string.Join(", ", columns.Select((c, i) => "@p" + i));

                        using var ins = new SqliteCommand(
                            $"INSERT INTO \"{_tableName}\" ({names}) VALUES ({values})", conn, tx);
                        for (int i = 0; i < columns.Count; i++)
                            ins.Parameters.AddWithValue("@p" + i, row[columns[i]] ?? DBNull.Value);
                        ins.ExecuteNonQuery();
                        inserted++;
                    }
                    else if (row.RowState == DataRowState.Modified)
                    {
                        object rowId = row[RowIdColumn, DataRowVersion.Original];
                        if (rowId == null || rowId == DBNull.Value) continue;

                        string sets = string.Join(", ", columns.Select((c, i) => "\"" + c.ColumnName + "\" = @p" + i));
                        using var upd = new SqliteCommand(
                            $"UPDATE \"{_tableName}\" SET {sets} WHERE rowid = @r", conn, tx);
                        for (int i = 0; i < columns.Count; i++)
                            upd.Parameters.AddWithValue("@p" + i, row[columns[i]] ?? DBNull.Value);
                        upd.Parameters.AddWithValue("@r", rowId);
                        upd.ExecuteNonQuery();
                        updated++;
                    }
                }

                tx.Commit();
                _table.AcceptChanges();
                Status($"Сохранено: изменено {updated}, добавлено {inserted}, удалено {deleted}");
                LoadTable(_tableName);
            }
            catch (Exception ex)
            {
                Status("Не сохранилось: " + ex.Message);
            }
        }

        private void BtnAddRow_Click(object sender, RoutedEventArgs e)
        {
            if (_table == null) { Status("Сначала выберите таблицу"); return; }

            _table.Rows.Add(_table.NewRow());
            gridTable.ScrollIntoView(_table.DefaultView[_table.Rows.Count - 1]);
            Status("Строка добавлена. Заполните ячейки и нажмите «Сохранить».");
        }

        private void BtnDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if (_table == null) { Status("Сначала выберите таблицу"); return; }

            if (!(gridTable.CurrentItem is DataRowView view)) { Status("Выберите строку"); return; }

            string name = view.Row.ItemArray.FirstOrDefault(v => v != null && v != DBNull.Value)?.ToString() ?? "";
            if (!Windows.MessageDialog.Confirm(this, $"Удалить строку{(name.Length > 0 ? " «" + name + "»" : "")}? Действие не отменяется.", "Удаление строки"))
                return;

            view.Row.Delete();
            Status("Строка помечена на удаление. Нажмите «Сохранить».");
        }

        /// <summary>
        /// Копия базы: главное средство страховки. Файл кладём туда, куда укажет
        /// игрок, — при обновлении программы лист персонажа можно вернуть.
        /// </summary>
        private void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Куда сохранить копию базы",
                Filter = "База данных (*.db)|*.db",
                FileName = $"HMWT_база_{DateTime.Now:yyyy-MM-dd_HH-mm}.db"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                if (!DatabaseHelper.BackupTo(dialog.FileName, out string error))
                {
                    Status("Не удалось сделать копию: " + error);
                    return;
                }

                Status("Копия базы сохранена: " + dialog.FileName);
            }
            catch (Exception ex)
            {
                Status("Не удалось сделать копию: " + ex.Message);
            }
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Какую копию базы загрузить",
                Filter = "База данных (*.db)|*.db|Все файлы (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            if (!Windows.MessageDialog.Confirm(this,
                    "Заменить текущую базу выбранной копией?\n\n" +
                    "Всё, что сделано после этой копии, будет потеряно. " +
                    "Текущая база сохранится рядом с прежней как файл с добавкой «.перед-восстановлением». " +
                    "После замены программу нужно закрыть и открыть заново.",
                    "Восстановление базы"))
                return;

            try
            {
                string dbPath = DatabaseHelper.GetDatabasePath();
                string safety = dbPath + ".перед-восстановлением";

                // Соединения держат файл, поэтому сначала освобождаем их
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) File.Copy(dbPath, safety, true);
                File.Copy(dialog.FileName, dbPath, true);

                Status("База заменена. Закройте и откройте программу заново.");
                Windows.MessageDialog.Info(this,
                    "База заменена.\n\nЗакройте программу и откройте заново — тогда данные перечитаются.\n\n" +
                    "Прежняя база сохранена рядом:\n" + safety,
                    "Восстановление базы");
            }
            catch (Exception ex)
            {
                Status("Не удалось заменить базу: " + ex.Message);
            }
        }

        private void Status(string text) => txtStatus.Text = text;
    }
}
