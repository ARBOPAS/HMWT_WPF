using System;
using System.Diagnostics;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Качество анимации гифок.
    ///
    /// Зачем режим: гифка 800×450 из 242 кадров стоит около 330 МБ и разбирается
    /// несколько секунд, даже если показывается в квадрате 40×40. Поэтому кадры
    /// рисуются сразу в размере показа и берутся не все — а сколько именно,
    /// и решает этот режим.
    ///
    /// Три уровня — это обмен «плавность и чёткость» на «скорость и память».
    /// </summary>
    public enum GifQuality
    {
        /// <summary>Мало кадров и мелкий размер: быстро и дешево, но грубо.</summary>
        Low = 0,

        /// <summary>Разумный компромисс — режим по умолчанию.</summary>
        Medium = 1,

        /// <summary>Больше кадров: плавно, но на тяжёлых гифках заметны задержки.</summary>
        High = 2
    }

    /// <summary>Настройки анимации гифок.</summary>
    public static class GifQualityService
    {
        private const string QualityKey = "GifQuality";

        /// <summary>
        /// Сколько кадров максимум берём для каждого режима.
        ///
        /// Progressия заметная: низкое качество годится только для быстрого
        /// просмотра, среднее должно быть уже вполне приличным, высокое —
        /// плавным. Разницу между режимами видно глазом, иначе переключатель
        /// не имеет смысла.
        /// </summary>
        public static int MaxFrames => Current switch
        {
            GifQuality.Low => 24,
            GifQuality.High => 160,
            _ => 80
        };

        /// <summary>Сколько кадров держим в памяти одновременно (разные картинки).</summary>
        public static int MaxCachedAnimations => Current switch
        {
            GifQuality.Low => 1,
            GifQuality.High => 3,
            _ => 2
        };

        /// <summary>
        /// Разрешение при разборе статичного первого кадра.
        /// 0 — не ограничивать (нужно для крупных изображений вроде портрета).
        /// </summary>
        public static int StaticDecodeWidth => Current switch
        {
            GifQuality.Low => 240,
            GifQuality.High => 0,
            _ => 640
        };

        /// <summary>Текущий режим. Читается из настроек.</summary>
        public static GifQuality Current
        {
            get
            {
                try
                {
                    string raw = DatabaseHelper.GetSetting(QualityKey, "");
                    if (int.TryParse(raw, out int value) &&
                        value >= (int)GifQuality.Low && value <= (int)GifQuality.High)
                    {
                        return (GifQuality)value;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GifQuality] чтение: {ex.Message}");
                }

                return GifQuality.Medium;
            }
        }

        /// <summary>Сохраняет режим и сбрасывает кэш: старые кадры больше не подходят.</summary>
        public static void Set(GifQuality quality)
        {
            try
            {
                DatabaseHelper.SaveSetting(QualityKey, ((int)quality).ToString());
                GifFrameCache.Clear();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GifQuality] сохранение: {ex.Message}");
            }
        }

        /// <summary>Название режима для интерфейса.</summary>
        public static string Describe(GifQuality quality) => quality switch
        {
            GifQuality.Low => "Низкое — быстро, но грубо",
            GifQuality.High => "Высокое — плавно, но медленно",
            _ => "Среднее — обычная работа"
        };
    }
}
