using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Считает отступ пункта дерева по уровню вложенности: каждая вложенная
    /// группа и заметка внутри неё сдвигаются вправо, чтобы иерархия читалась.
    ///
    /// Отступ ограничен сверху: при большой вложенности названия уезжали за край
    /// панели и пропадали совсем. Дальше предела дерево просто остаётся на месте.
    /// </summary>
    public class TreeIndentConverter : IValueConverter
    {
        /// <summary>Ширина одного уровня вложенности.</summary>
        public double Step { get; set; } = 12;

        /// <summary>Максимальный суммарный отступ, чтобы текст не уходил за панель.</summary>
        public double MaxIndent { get; set; } = 72;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Отступ теперь считает сам элемент: он знает свой уровень вложенности,
            // и это надёжнее обхода визуального дерева
            if (value is HMWT_MUI.Models.NoteTreeItem note)
            {
                double offset = Math.Min(MaxIndent, Math.Max(0, note.Depth) * Step);
                return new Thickness(offset, 0, 0, 0);
            }

            if (!(value is DependencyObject node)) return new Thickness(0);

            int depth = 0;
            var current = node;
            while (current != null)
            {
                current = FindParent(current);
                if (current is System.Windows.Controls.TreeViewItem) depth++;
                if (depth > 32) break;
            }

            if (depth > 0) depth--; // сам элемент не считается уровнем вложенности
            return new Thickness(Math.Min(MaxIndent, depth * Step), 0, 0, 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static DependencyObject FindParent(DependencyObject node)
        {
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(node);
            return LogicalTreeHelper.GetParent(node);
        }
    }
}
