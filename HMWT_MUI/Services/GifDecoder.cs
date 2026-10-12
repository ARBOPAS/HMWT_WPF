using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Декодер GIF-анимаций с композитингом кадров.
    /// </summary>
    public static class GifDecoder
    {
        /// <summary>
        /// Композитит кадры GIF в полноразмерные буферы Pbgra32.
        /// Возвращает массив кадров + массив задержек (мс).
        /// </summary>
        public static (BitmapSource[] Frames, int[] DelaysMs) ComposeFrames(string fullPath)
            => ComposeFrames(fullPath, 0, 0);

        /// <summary>
        /// Композитит кадры GIF с уменьшением до нужного размера.
        ///
        /// Зачем уменьшать: кадр стоит ширина × высота × 4 байта. Гифка 800×450 из
        /// 242 кадров — это 330 МБ и несколько секунд на разбор, хотя показывается
        /// она в квадрате 40×40. Если рисовать кадры сразу в размере показа,
        /// получается 40×22×4 × 242 ≈ 0,8 МБ — в четыреста раз меньше.
        ///
        /// maxWidth = 0 означает «не уменьшать» (для полноэкранного портрета).
        /// maxFrames ограничивает число кадров: остальные отбрасываются, а
        /// задержки сохраняются пропорционально — анимация остаётся плавной по времени.
        /// </summary>
        public static (BitmapSource[] Frames, int[] DelaysMs) ComposeFrames(
            string fullPath, int maxWidth, int maxFrames)
        {
            try
            {
                // Путь передаём в Uri напрямую: ручная сборка "file:///..." ломает
                // путь (query-строка попадает внутрь пути), и декодер падает.
                var decoder = new GifBitmapDecoder(
                    new Uri(fullPath, UriKind.Absolute),
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                var frames = decoder.Frames;
                if (frames == null || frames.Count == 0)
                    return (null, null);

                // Задержки читаем из самого файла: WPF-метаданные для части GIF
                // отдают неверное значение (например, 100 мс вместо 500),
                // из-за чего анимация «летит» быстрее задуманного.
                var fileDelays = ReadGraphicControlDelays(fullPath);
                if (fileDelays != null && fileDelays.Count != frames.Count)
                {
                    Debug.WriteLine($"[GifDecoder] кадров {frames.Count}, задержек {fileDelays.Count} — беру метаданные кадров");
                    fileDelays = null;
                }

                // Логический размер = максимум по кадрам
                int srcWidth = 0, srcHeight = 0;
                foreach (var f in frames)
                {
                    if (f.PixelWidth > srcWidth) srcWidth = f.PixelWidth;
                    if (f.PixelHeight > srcHeight) srcHeight = f.PixelHeight;
                }

                if (srcWidth <= 0 || srcHeight <= 0)
                    return (null, null);

                // Размер полотна: уменьшаем, если попросили и если это имеет смысл
                int width = srcWidth;
                int height = srcHeight;
                double shrink = 1.0;

                if (maxWidth > 0 && maxWidth < srcWidth)
                {
                    shrink = (double)maxWidth / srcWidth;
                    width = Math.Max(1, maxWidth);
                    height = Math.Max(1, (int)Math.Round(srcHeight * shrink));
                }

                // Сколько кадров берём. Задержки объединяем, чтобы общая
                // длительность анимации не изменилась.
                int step = 1;
                if (maxFrames > 0 && frames.Count > maxFrames)
                {
                    step = (int)Math.Ceiling((double)frames.Count / maxFrames);
                    Debug.WriteLine($"[GifDecoder] кадров {frames.Count}, беру каждый {step}-й (потолок {maxFrames})");
                }

                var result = new List<BitmapSource>(Math.Min(frames.Count, maxFrames > 0 ? maxFrames : frames.Count));
                var delays = new List<int>(result.Capacity);

                var canvas = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                int pendingDelay = 0;

                for (int i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];
                    var meta = frame.Metadata as BitmapMetadata;

                    int left = 0, top = 0;
                    int delayCs = 10; // 100 мс по умолчанию

                    try
                    {
                        if (meta != null)
                        {
                            var o = meta.GetQuery("/imgdesc/Left");
                            if (o != null) left = Convert.ToInt32(o, CultureInfo.InvariantCulture);

                            o = meta.GetQuery("/imgdesc/Top");
                            if (o != null) top = Convert.ToInt32(o, CultureInfo.InvariantCulture);

                            if (fileDelays == null)
                            {
                                o = meta.GetQuery("/grctlext/Delay");
                                if (o != null) delayCs = Convert.ToInt32(o, CultureInfo.InvariantCulture);
                            }
                        }
                    }
                    catch { }

                    // Приоритет у задержки из файла — она достоверная
                    if (fileDelays != null) delayCs = fileDelays[i];

                    int ms = delayCs * 10;
                    if (ms < 20) ms = 100;

                    // Кадр не берём, но его время копим: иначе анимация ускорится
                    if (i % step != 0)
                    {
                        pendingDelay += ms;
                        continue;
                    }

                    // Конвертим кадр в Pbgra32
                    BitmapSource src;
                    try
                    {
                        if (frame.Format != PixelFormats.Pbgra32)
                        {
                            var conv = new FormatConvertedBitmap();
                            conv.BeginInit();
                            conv.Source = frame;
                            conv.DestinationFormat = PixelFormats.Pbgra32;
                            conv.EndInit();
                            src = conv;
                        }
                        else
                        {
                            src = frame;
                        }
                    }
                    catch
                    {
                        src = frame;
                    }

                    // Рисуем дельту поверх canvas. Координаты кадра — в исходном
                    // масштабе, поэтому переводим их в уменьшенный.
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawImage(canvas, new System.Windows.Rect(0, 0, width, height));

                        int fw = (int)Math.Round(Math.Min(src.PixelWidth, srcWidth - left) * shrink);
                        int fh = (int)Math.Round(Math.Min(src.PixelHeight, srcHeight - top) * shrink);
                        if (fw > 0 && fh > 0)
                        {
                            dc.DrawImage(src, new System.Windows.Rect(
                                left * shrink, top * shrink, fw, fh));
                        }
                    }

                    // Один рендер на кадр: считаем новый кадр в snap и делаем его
                    // текущим полотном (раньше на каждый кадр приходилось два рендера)
                    var snap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    snap.Render(dv);
                    snap.Freeze();
                    result.Add(snap);

                    delays.Add(ms + pendingDelay);
                    pendingDelay = 0;

                    canvas = snap;
                }

                return (result.ToArray(), delays.ToArray());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GifDecoder] compose failed for '{fullPath}': {ex.GetType().Name}: {ex.Message}");
                return (null, null);
            }
        }

        /// <summary>
        /// Читает задержки кадров из Graphic Control Extension (блок 0x21 0xF9)
        /// напрямую из файла. Метаданные WPF для части GIF возвращают неверную
        /// задержку, из-за чего анимация идёт быстрее задуманного.
        /// Возвращает список в сотых долях секунды либо null, если разобрать не удалось.
        /// </summary>
        private static List<int> ReadGraphicControlDelays(string fullPath)
        {
            try
            {
                var bytes = File.ReadAllBytes(fullPath);
                if (bytes.Length < 13) return null;

                // Заголовок: "GIF87a" или "GIF89a"
                if (bytes[0] != 'G' || bytes[1] != 'I' || bytes[2] != 'F') return null;

                int pos = 6; // после заголовка
                int packed = bytes[10];
                pos = 13;
                if ((packed & 0x80) != 0)
                    pos += 3 * (1 << ((packed & 0x07) + 1)); // таблица глобальных цветов

                var delays = new List<int>();

                while (pos < bytes.Length)
                {
                    byte block = bytes[pos];

                    if (block == 0x3B) break; // trailer

                    if (block == 0x21) // extension
                    {
                        if (pos + 1 >= bytes.Length) break;
                        byte label = bytes[pos + 1];

                        if (label == 0xF9) // Graphic Control Extension
                        {
                            // 0x21 0xF9 blockSize packed delayLo delayHi ...
                            if (pos + 4 < bytes.Length)
                            {
                                int delay = bytes[pos + 4] | (bytes[pos + 5] << 8);
                                delays.Add(delay);
                            }
                        }

                        // Пропускаем все подблоки до терминатора 0x00
                        pos += 2;
                        while (pos < bytes.Length && bytes[pos] != 0x00)
                        {
                            int len = bytes[pos];
                            pos += 1 + len;
                        }
                        pos += 1;
                        continue;
                    }

                    if (block == 0x2C) // image descriptor
                    {
                        if (pos + 9 >= bytes.Length) break;
                        int localPacked = bytes[pos + 9];
                        pos += 10;
                        if ((localPacked & 0x80) != 0)
                            pos += 3 * (1 << ((localPacked & 0x07) + 1)); // локальная палитра

                        // LZW: минимальный размер кода + подблоки
                        pos += 1;
                        while (pos < bytes.Length && bytes[pos] != 0x00)
                        {
                            int len = bytes[pos];
                            pos += 1 + len;
                        }
                        pos += 1;
                        continue;
                    }

                    pos++; // неизвестный блок — идём дальше
                }

                return delays.Count > 0 ? delays : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GifDecoder] delays parse failed for '{fullPath}': {ex.Message}");
                return null;
            }
        }
    }
}