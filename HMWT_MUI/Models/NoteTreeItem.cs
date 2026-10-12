using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HMWT_MUI.Models
{
    /// <summary>
    /// Узел дерева заметок: группа (папка) или заметка.
    /// Группы могут быть вложены друг в друга, заметки — только внутри группы.
    /// </summary>
    public class NoteTreeItem : INotifyPropertyChanged
    {
        public long Id { get; set; }

        /// <summary>Родительская группа (null — корень).</summary>
        public long? ParentId { get; set; }

        /// <summary>Порядок среди соседей.</summary>
        public int SortOrder { get; set; }

        private string _title;
        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public bool IsGroup { get; set; }

        /// <summary>У заметки есть вложения.</summary>
        public bool HasAttachment { get; set; }

        public ObservableCollection<NoteTreeItem> Children { get; set; } = new ObservableCollection<NoteTreeItem>();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isVisible = true;

        /// <summary>Виден ли узел при текущем фильтре поиска.</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>Заголовок без иконки: вид дерева сам рисует стрелку для групп.</summary>
        public string DisplayTitle => Title;

        /// <summary>
        /// Уровень вложенности: 0 у корня. Нужен, чтобы нарисовать вертикальные
        /// направляющие уровня, как в Obsidian.
        /// </summary>
        public int Depth { get; set; }

        /// <summary>
        /// Список уровней для направляющих: по одному элементу на каждый
        /// родительский уровень. Привязывается к ItemsControl в шаблоне дерева.
        /// </summary>
        public System.Collections.Generic.List<int> GuideDepths
        {
            get
            {
                var list = new System.Collections.Generic.List<int>(Depth);
                for (int i = 0; i < Depth; i++) list.Add(i);
                return list;
            }
        }

        /// <summary>Проставляет уровни вложенности всему поддереву.</summary>
        public void ApplyDepth(int depth)
        {
            Depth = depth;
            OnPropertyChanged(nameof(Depth));
            OnPropertyChanged(nameof(GuideDepths));

            foreach (var child in Children) child.ApplyDepth(depth + 1);
        }

        /// <summary>Число заметок внутри группы (рекурсивно).</summary>
        public int NoteCount
        {
            get
            {
                int count = 0;
                foreach (var child in Children)
                    count += child.IsGroup ? child.NoteCount : 1;
                return count;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>Рекурсивно уведомляет об изменении отображаемых свойств.</summary>
        public void RefreshDisplay()
        {
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(NoteCount));
            foreach (var child in Children)
                child.RefreshDisplay();
        }
    }
}
