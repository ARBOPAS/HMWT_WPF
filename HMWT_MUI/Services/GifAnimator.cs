using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HMWT_MUI.Services
{
    /// <summary>Разобранные кадры GIF: кадры + задержки (мс) + метка файла.</summary>
    public sealed class GifFrames
    {
        public BitmapSource[] Frames;
        public int[] Delays;
        public DateTime Stamp;
        public string Path;

        public bool IsAnimated => Frames != null && Frames.Length > 1;
    }

    /// <summary>
    /// Кэш разобранных GIF. Ключ — полный путь + время изменения файла,
    /// поэтому один и тот же файл не декодируется повторно для каждого элемента.
    ///
    /// ВАЖНО ПРО ПАМЯТЬ: полный набор кадров занимает очень много — декодер делает
    /// по одному полному буферу Pbgra32 на кадр. Для гифки 400×400 из 60 кадров это
    /// около 38 МБ на один файл. Держать такие наборы в кэше нельзя: приложение
    /// раздувалось до полутора гигабайт.
    ///
    /// Поэтому здесь кэшируется ТОЛЬКО первый кадр (он нужен всегда — как статичная
    /// картинка в списках), а полный набор берётся на время анимации и освобождается.
    /// </summary>
    public static class GifFrameCache
    {
        private static readonly Dictionary<string, GifFrames> Cache =
            new Dictionary<string, GifFrames>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Статичные кадры: один BitmapSource на файл, память небольшая.</summary>
        private static readonly Dictionary<string, BitmapSource> FirstFrameCache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        private const int MaxFirstFrames = 64;

        /// <summary>
        /// Ширина и высота анимации по заголовкам кадров, без декодирования.
        ///
        /// Нужно, чтобы сразу знать, в каком размере разбирать кадры: иначе
        /// приходится сначала прочитать картинку целиком в исходном размере
        /// (для гифки 800×450 из 22 кадров это секунда и сотни мегабайт), а
        /// потом всё равно разбирать кадры в размере показа.
        /// </summary>
        public static bool TryGetPixelSize(string fullPath, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath)) return false;

                var decoder = new GifBitmapDecoder(
                    new Uri(fullPath, UriKind.Absolute),
                    BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

                foreach (var frame in decoder.Frames)
                {
                    if (frame.PixelWidth > width) width = frame.PixelWidth;
                    if (frame.PixelHeight > height) height = frame.PixelHeight;
                }

                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GifFrameCache] размер '{fullPath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Первый кадр файла — для статичного показа в списках и плитках.
        /// Если это не GIF или кадр один, вернётся обычное изображение.
        /// </summary>
        public static BitmapSource GetFirstFrame(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;

            if (FirstFrameCache.TryGetValue(fullPath, out var cached))
                return cached;

            BitmapSource frame = null;
            try
            {
                // DecodePixelWidth ограничивает размер: крупные картинки в списках
                // всё равно показываются маленькими, а память экономит заметно
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(fullPath, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;

                int limit = GifQualityService.StaticDecodeWidth;
                if (limit > 0) image.DecodePixelWidth = limit;

                image.EndInit();
                image.Freeze();
                frame = image;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GifFrameCache] первый кадр '{fullPath}': {ex.Message}");
                return null;
            }

            if (FirstFrameCache.Count >= MaxFirstFrames) FirstFrameCache.Clear();
            FirstFrameCache[fullPath] = frame;
            return frame;
        }

        /// <summary>Полные наборы кадров, загруженные для анимации. Ключ — путь.</summary>
        public static GifFrames Get(string fullPath) => Get(fullPath, 0);

        /// <summary>
        /// Набор кадров для анимации.
        ///
        /// displayWidth — ширина, в которой картинка будет показана. Кадры рисуются
        /// сразу в этом размере: гифка 800×450, показанная в квадрате 40×40, даёт
        /// 40×22 пикселя на кадр вместо 800×450 — это в четыреста раз меньше памяти
        /// и во столько же быстрее разбор.
        ///
        /// displayWidth = 0 означает «в исходном размере»: нужно для крупного
        /// изображения, например портрета на весь экран.
        /// </summary>
        public static GifFrames Get(string fullPath, int displayWidth)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;

            DateTime stamp;
            try { stamp = System.IO.File.GetLastWriteTimeUtc(fullPath); }
            catch { return null; }

            // Размер округляем до 32 пикселей: иначе каждый элемент списка с чуть
            // другой шириной разбирал бы файл заново
            int target = displayWidth > 0 ? Math.Max(32, ((displayWidth + 31) / 32) * 32) : 0;
            string key = target > 0 ? fullPath + "|" + target : fullPath;

            if (Cache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
                return cached;

            var composed = GifDecoder.ComposeFrames(fullPath, target, GifQualityService.MaxFrames);
            if (composed.Frames == null || composed.Frames.Length == 0) return null;

            cached = new GifFrames
            {
                Frames = composed.Frames,
                Delays = NormalizeDelays(composed.DelaysMs, composed.Frames.Length),
                Stamp = stamp,
                Path = fullPath
            };

            // Наборы кадров держим в очень скромном количестве: даже уменьшенный
            // набор занимает мегабайты. Число наборов задаёт режим качества.
            int maxSets = GifQualityService.MaxCachedAnimations;
            if (Cache.Count >= maxSets) Cache.Clear();
            Cache[key] = cached;
            return cached;
        }

        /// <summary>Сбрасывает кэш (например, после замены файла).</summary>
        public static void Clear()
        {
            Cache.Clear();
            FirstFrameCache.Clear();
        }

        /// <summary>
        /// Забывает один файл: статичный кадр и наборы для анимации.
        ///
        /// Зачем: у аватара персонажа имя файла постоянное (Characters/&lt;id&gt;/Avatar.png),
        /// поэтому после смены фото путь остаётся тем же, и кэш по пути отдавал
        /// старую картинку — в окне персонажа аватар не менялся, хотя файл на диске
        /// уже новый. Перед показом такого файла вызываем этот метод.
        /// </summary>
        public static void Invalidate(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;

            FirstFrameCache.Remove(fullPath);

            // Наборы кадров лежат под ключом «путь|ширина» — убираем все размеры
            var keys = new List<string>();
            foreach (string key in Cache.Keys)
            {
                if (key.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase)) keys.Add(key);
            }
            foreach (string key in keys) Cache.Remove(key);
        }

        /// <summary>
        /// Сколько кадров анимаций лежит в памяти прямо сейчас.
        ///
        /// Показывается в настройках: по этому числу видно, что именно занимает
        /// память, и понятно, поможет ли понижение качества.
        /// </summary>
        public static int CachedFrameCount
        {
            get
            {
                int total = 0;
                foreach (var gif in Cache.Values)
                {
                    if (gif?.Frames != null) total += gif.Frames.Length;
                }
                return total;
            }
        }

        /// <summary>
        /// Сколько памяти займут все кадры файла, не разбирая их.
        ///
        /// Нужно, чтобы решить, стоит ли вообще включать анимацию: набор кадров
        /// стоит width × height × 4 байта на КАЖДЫЙ кадр, поэтому большая гифка
        /// легко съедает сотни мегабайт.
        /// </summary>
        public static long EstimateFramesBytes(string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath)) return 0;

                // DelayCreation: кадры не декодируются, читаются только размеры —
                // иначе сама проверка съела бы столько же памяти, сколько экономит
                var decoder = new GifBitmapDecoder(
                    new Uri(fullPath, UriKind.Absolute),
                    BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

                int count = decoder.Frames.Count;
                if (count <= 1) return 0;

                int width = 0, height = 0;
                foreach (var f in decoder.Frames)
                {
                    if (f.PixelWidth > width) width = f.PixelWidth;
                    if (f.PixelHeight > height) height = f.PixelHeight;
                }

                return (long)width * height * 4 * count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Стоит ли анимировать файл.
        ///
        /// Лимит высокий (512 МБ) намеренно: анимация включается ТОЛЬКО при наведении
        /// и освобождает кадры, когда курсор ушёл, — в памяти бывает не больше одной
        /// такой гифки. Ограничение осталось лишь как защита от совсем тяжёлых файлов,
        /// где анимация всё равно не имела бы смысла: крупная гифка 800×450 из 242
        /// кадров стоит около 330 МБ и всё же показывается, а вот гигабайтные — нет.
        /// </summary>
        public static bool IsCheapToAnimate(string fullPath, long limitBytes = 512L * 1024 * 1024)
        {
            long estimate = EstimateFramesBytes(fullPath);
            return estimate > 0 && estimate <= limitBytes;
        }

        /// <summary>
        /// Освобождает полный набор кадров файла. Вызывается, когда анимация
        /// закончилась: без этого память остаётся занятой до выхода из приложения.
        /// </summary>
        public static void Release(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;

            // Убираем все размеры этого файла: ключ включает ширину показа
            var keys = new List<string>();
            foreach (var pair in Cache)
            {
                if (pair.Key.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase))
                    keys.Add(pair.Key);
            }

            foreach (var key in keys) Cache.Remove(key);
        }

        /// <summary>
        /// Приводит массив задержек к длине кадров и к разумным пределам.
        /// Слишком маленькие значения превращаются в 100 мс (как в браузерах),
        /// иначе анимация «сходит с ума» на битых файлах.
        /// </summary>
        private static int[] NormalizeDelays(int[] source, int frameCount)
        {
            var result = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                int ms = source != null && i < source.Length ? source[i] : 100;
                if (ms < 20) ms = 100;
                if (ms > 10_000) ms = 10_000;
                result[i] = ms;
            }
            return result;
        }
    }

    /// <summary>
    /// Проигрыватель GIF-анимации.
    ///
    /// В отличие от простого DispatcherTimer, который на каждый кадр
    /// переустанавливал Interval, здесь кадры привязаны к МОНОТОННОМУ времени:
    /// рассчитывается расписание (сумма задержек), а по тику выбирается кадр,
    /// положенный на текущий момент. Если UI-поток был занят, опоздавшие кадры
    /// пропускаются — без «догоняющего» ускорения.
    /// </summary>
    public sealed class GifAnimator
    {
        private readonly BitmapSource[] _frames;
        private readonly int[] _delays;
        private readonly long[] _starts;      // момент показа каждого кадра, мс от старта
        private readonly long _totalMs;
        private readonly DispatcherTimer _timer;
        private readonly Action<BitmapSource> _setFrame;
        private readonly Stopwatch _watch = new Stopwatch();
        private int _lastIndex = -1;

        public GifAnimator(BitmapSource[] frames, int[] delays, Action<BitmapSource> setFrame, int fpsFloor = 30)
        {
            if (frames == null || frames.Length == 0) throw new ArgumentException("нет кадров", nameof(frames));
            if (setFrame == null) throw new ArgumentNullException(nameof(setFrame));

            _frames = frames;
            _delays = delays ?? new int[frames.Length];
            _setFrame = setFrame;

            _starts = new long[frames.Length];
            long acc = 0;
            for (int i = 0; i < frames.Length; i++)
            {
                _starts[i] = acc;
                int ms = i < _delays.Length ? _delays[i] : 100;
                if (ms < 20) ms = 100;
                acc += ms;
            }
            _totalMs = acc <= 0 ? 100 : acc;

            // Частота опроса: не реже 1 мс, но и не чаще, чем нужно для самого
            // короткого кадра — иначе крутим диспетчер впустую
            int shortest = int.MaxValue;
            foreach (var d in _delays) if (d < shortest && d >= 20) shortest = d;
            if (shortest == int.MaxValue) shortest = 100;

            int interval = Math.Max(1, Math.Min(shortest, 1000 / Math.Max(1, fpsFloor)));

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(interval)
            };
            _timer.Tick += OnTick;
        }

        /// <summary>Показывает кадр, положенный по расписанию на текущий момент.</summary>
        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                long now = _watch.ElapsedMilliseconds;
                long inLoop = now % _totalMs;

                int index = 0;
                for (int i = _starts.Length - 1; i >= 0; i--)
                {
                    if (_starts[i] <= inLoop) { index = i; break; }
                }

                // Опоздавшие кадры не догоняем: просто показываем актуальный
                if (index != _lastIndex)
                {
                    _lastIndex = index;
                    _setFrame(_frames[index]);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GifAnimator] tick: {ex.Message}");
            }
        }

        public void Start()
        {
            try
            {
                _lastIndex = 0;
                _setFrame(_frames[0]);
                _watch.Restart();
                _timer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GifAnimator] start: {ex.Message}");
            }
        }

        public void Stop()
        {
            try { _timer.Stop(); } catch { }
            try { _watch.Stop(); } catch { }
        }
    }
}
