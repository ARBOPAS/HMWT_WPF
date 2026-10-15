using System.Windows;
using System.Windows.Media;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Даёт кисть или цвет оформления по имени токена — для тех мест, где цвет
    /// задаётся кодом, а не разметкой: карточки персонажей, плашки кубиков,
    /// подсветка формул в тексте.
    ///
    /// Зачем: раньше такие цвета были вписаны числами, и получалось два источника
    /// правды. Золото лежало и в палитре, и в полутора десятках мест числом вида
    /// 0x5F,0xD4,0xDD; когда палитра менялась, эти места оставались прежними.
    /// Теперь цвет берётся из того же словаря, что и разметка, — менять его
    /// по-прежнему нужно в одном файле.
    ///
    /// Запасное значение остаётся на случай, когда ресурсов ещё нет (например,
    /// сборка без Application): тогда цвет будет из палитры по умолчанию.
    /// </summary>
    public static class PaletteBrush
    {
        /// <summary>Кисть по имени токена. Запасной цвет — на случай отсутствия ресурсов.</summary>
        public static Brush Get(string key, Color fallback)
        {
            object found = Find(key);

            if (found is Brush brush) return brush;
            if (found is Color color) return new SolidColorBrush(color);

            return new SolidColorBrush(fallback);
        }

        /// <summary>Цвет по имени токена: нужен там, где цвет смешивают или сравнивают.</summary>
        public static Color GetColor(string key, Color fallback)
        {
            object found = Find(key);

            if (found is Color color) return color;
            if (found is SolidColorBrush brush) return brush.Color;

            return fallback;
        }

        private static object Find(string key)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return null;
                return app.TryFindResource(key);
            }
            catch
            {
                // Ресурсов может не быть вовсе — это не повод падать
                return null;
            }
        }
    }
}
