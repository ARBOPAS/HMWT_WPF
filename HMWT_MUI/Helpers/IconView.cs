using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Иконка из <see cref="IconLibrary"/>: тонкий контур в размере, который задан.
    ///
    /// Рисуется в OnRender, а не картинкой: масштаб интерфейса меняется через
    /// LayoutTransform, и вектор остаётся резким на любом увеличении. Цвет берётся
    /// кистью темы, поэтому одна и та же иконка в интерфейсе бывает золотой,
    /// стальной или красной без второй копии файла.
    /// </summary>
    public sealed class IconView : FrameworkElement
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(string), typeof(IconView),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
            nameof(IconSize), typeof(double), typeof(IconView),
            new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
            nameof(IconBrush), typeof(Brush), typeof(IconView),
            new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(IconView),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
            nameof(Filled), typeof(bool), typeof(IconView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// Заливка вместо контура — для ключевых мест: активная вкладка, главные
        /// кнопки, маркеры состояния. Работает у иконок, нарисованных замкнутым
        /// контуром; у линейных (крестик, стрелки) останется просто толще линия.
        /// </summary>
        public bool Filled
        {
            get => (bool)GetValue(FilledProperty);
            set => SetValue(FilledProperty, value);
        }

        /// <summary>Имя иконки: person, backpack, sword, dice, beast…</summary>
        public string Kind
        {
            get => (string)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        /// <summary>Размер квадрата в точках.</summary>
        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        /// <summary>Цвет линий. Кисть темы: B.Ink, B.Gold, B.Mute, B.Danger…</summary>
        public Brush IconBrush
        {
            get => (Brush)GetValue(IconBrushProperty);
            set => SetValue(IconBrushProperty, value);
        }

        /// <summary>Толщина линии в единицах сетки 24×24; 0 — толщина по умолчанию.</summary>
        public double Thickness
        {
            get => (double)GetValue(ThicknessProperty);
            set => SetValue(ThicknessProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // Размер округляем до целого: дробный размер даёт полупиксельные края,
            // и линия выглядит «рассыпавшейся» на чёрточки
            double size = IconSize > 0 ? IconSize : 20.0;
            return new Size(Math.Round(size), Math.Round(size));
        }

        protected override void OnRender(DrawingContext dc)
        {
            var geometry = IconLibrary.Get(Kind);
            if (geometry == null) return;

#if DEBUG
            // Страховка от «поехавшей» иконки. Рисование не обрезается границами
            // элемента, поэтому контур, вылезший за сетку 24×24, рисуется поверх
            // соседних кнопок и выглядит как разорванный. Так были сломаны
            // expand/upload/download: у одной геометрия доходила до (42,42).
            var b = geometry.Bounds;
            if (b.Left < -0.01 || b.Top < -0.01 || b.Right > IconLibrary.ViewBox + 0.01 || b.Bottom > IconLibrary.ViewBox + 0.01)
                Debug.WriteLine($"[IconView] иконка «{Kind}» выходит за сетку {IconLibrary.ViewBox}: {b}");
#endif

            double size = Math.Round(IconSize > 0 ? IconSize : 20.0);
            double thickness = Thickness > 0 ? Thickness : IconLibrary.DefaultThickness;

            // На мелких размерах линия в две единицы сетки становится тоньше
            // пикселя и распадается на отдельные штрихи. Поэтому вес линии
            // поднимаем тем сильнее, чем меньше значок: 16 → +25%, 12 → +50%.
            if (Thickness <= 0 && size < 22)
            {
                double boost = size <= 13 ? 1.5 : (size <= 17 ? 1.3 : 1.15);
                thickness = IconLibrary.DefaultThickness * boost;
            }

            // Фактический вес обводки. У залитой фигуры он тонкий — иначе по краю
            // появляется светлая кайма, — и отступ внутри квадрата нужен под него
            // же. Раньше здесь всегда брался номинальный вес, поэтому залитые
            // значки сжимались лишний раз: стрелка выпадающего списка при
            // IconSize=9 получала масштаб 0.21 и рисовалась точкой 2.7×1.3 px.
            double strokeWeight = Filled ? 1.0 : thickness;

            // Толщину задаём в единицах сетки 24×24, а внутри квадрата поджимаем
            // рисунок на половину линии: иначе крайние точки срезаются краем
            double scale = (size - strokeWeight - 1.0) / IconLibrary.ViewBox;
            if (scale <= 0) scale = size / IconLibrary.ViewBox;

            // Сдвиг тоже округляем — вместе с целым размером это даёт попадание
            // линии в пиксель вместо размытой полутени
            double inset = Math.Round((strokeWeight * scale) / 2.0 + 0.5, 1);

            var brush = IconBrush ?? Brushes.Gainsboro;

            var pen = new Pen(brush, strokeWeight)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            dc.PushTransform(new TranslateTransform(inset, inset));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(Filled ? brush : null, pen, geometry);
            dc.Pop();
            dc.Pop();
        }
    }
}
