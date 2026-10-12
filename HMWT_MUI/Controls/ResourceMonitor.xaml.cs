using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HMWT_MUI.Controls
{
    /// <summary>
    /// Показывает, сколько ресурсов занимает приложение.
    ///
    /// Нужно, чтобы игрок сам видел, что нагружает компьютер, и решал, что делать:
    /// например, понизить качество анимации гифок. Без этого остаётся только
    /// догадываться, отчего программа стала тяжёлой.
    ///
    /// Считаем два числа: сколько памяти занято сейчас и насколько загружен
    /// процессор. Раз в секунду, в фоне — чтобы не мешать интерфейсу.
    /// </summary>
    public partial class ResourceMonitor : UserControl
    {
        private DispatcherTimer _timer;
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _privateMemoryCounter;
        private bool _privateMemoryCounterFailed;
        private DateTime _lastSample = DateTime.MinValue;
        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private bool _cpuCounterFailed;

        public ResourceMonitor()
        {
            InitializeComponent();
            Loaded += (s, e) => Start();
            Unloaded += (s, e) => Stop();
        }

        private void Start()
        {
            if (_timer != null) return;

            // Панель показываем сразу: значения сначала берём из самого процесса,
            // а счётчики производительности создаём в фоне.
            //
            // Раньше они создавались здесь же, в обработчике Loaded, и первый
            // вызов PerformanceCounter (он поднимает библиотеку счётчиков) занимал
            // заметное время — из-за этого первый вход в настройки «залипал».
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (s, e) => Update();
            _timer.Start();

            Update();

            System.Threading.Tasks.Task.Run(() => CreateCountersOnBackground());
        }

        /// <summary>
        /// Создаёт счётчики производительности в фоне: сама эта операция на
        /// UI-потоке недопустимо медленная, а до её окончания Update() берёт
        /// значения напрямую из процесса.
        /// </summary>
        private void CreateCountersOnBackground()
        {
            PerformanceCounter cpu = null;
            PerformanceCounter memory = null;
            string name = null;

            try
            {
                name = Process.GetCurrentProcess().ProcessName;

                // Счётчик может быть недоступен (например, в урезанной системе) —
                // тогда считаем загрузку сами по времени процесса
                try
                {
                    cpu = new PerformanceCounter("Process", "% Processor Time", name, true);
                    cpu.NextValue();   // первое значение всегда 0
                }
                catch (Exception ex)
                {
                    cpu = null;
                    Debug.WriteLine($"[Монитор] счётчик ЦП недоступен: {ex.Message}");
                }

                // Рабочий набор процесса (WorkingSet64) включает страницы, которые
                // процесс делит с другими: библиотеки, шрифты, общий кэш. Диспетчер
                // задач в столбце «Память» показывает только приватные страницы,
                // поэтому берём тот же счётчик, что и диспетчер.
                try
                {
                    memory = new PerformanceCounter("Process", "Working Set - Private", name, true);
                    memory.NextValue();
                }
                catch (Exception ex)
                {
                    memory = null;
                    Debug.WriteLine($"[Монитор] счётчик памяти недоступен: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Монитор] подготовка счётчиков: {ex.Message}");
            }

            // Возвращаемся в UI-поток только для того, чтобы записать готовые счётчики
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_timer == null)
                {
                    // Панель успели закрыть — счётчики больше не нужны
                    try { cpu?.Dispose(); } catch { }
                    try { memory?.Dispose(); } catch { }
                    return;
                }

                if (cpu != null) _cpuCounter = cpu; else _cpuCounterFailed = true;
                if (memory != null) _privateMemoryCounter = memory; else _privateMemoryCounterFailed = true;

                Update();
            }));
        }

        private void Stop()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Tick -= (s, e) => Update();
            _timer = null;

            try { _cpuCounter?.Dispose(); } catch { }
            _cpuCounter = null;
            try { _privateMemoryCounter?.Dispose(); } catch { }
            _privateMemoryCounter = null;
        }

        /// <summary>
        /// Память приложения в мегабайтах: приватный рабочий набор — то же
        /// число, что в столбце «Память» диспетчера задач.
        /// </summary>
        private double ReadProcessMemoryMb()
        {
            if (!_privateMemoryCounterFailed && _privateMemoryCounter != null)
            {
                try { return _privateMemoryCounter.NextValue() / 1024.0 / 1024.0; }
                catch (Exception ex)
                {
                    _privateMemoryCounterFailed = true;
                    Debug.WriteLine($"[Монитор] чтение памяти: {ex.Message}");
                }
            }

            // Запасной способ: приватные страницы по версии .NET
            return Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0;
        }

        /// <summary>Обновляет показания.</summary>
        private void Update()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                process.Refresh();

                // ── Память процесса ──
                double usedMb = ReadProcessMemoryMb();
                txtProcessMemory.Text = $"{usedMb:0} МБ";

                // ── Память компьютера ──
                double totalMb = 0, availableMb = 0;
                try
                {
                    totalMb = GetTotalMemoryMb();
                    availableMb = GetAvailableMemoryMb();
                }
                catch { }

                if (totalMb > 0)
                {
                    double usedPercent = Math.Max(0, Math.Min(100, (totalMb - availableMb) / totalMb * 100));
                    txtSystemMemory.Text = $"{totalMb - availableMb:0} из {totalMb:0} МБ ({usedPercent:0}%)";
                    barMemory.Value = usedPercent;
                }
                else
                {
                    txtSystemMemory.Text = "нет данных";
                }

                // ── Процессор ──
                double cpu = 0;
                if (!_cpuCounterFailed && _cpuCounter != null)
                {
                    try
                    {
                        // Делим на число ядер: счётчик отдаёт сумму по всем
                        float raw = _cpuCounter.NextValue();
                        cpu = raw / Math.Max(1, Environment.ProcessorCount);
                    }
                    catch { _cpuCounterFailed = true; }
                }

                if (_cpuCounterFailed) cpu = EstimateCpuByTime(process);

                cpu = Math.Max(0, Math.Min(100, cpu));
                txtCpu.Text = $"{cpu:0}%";
                barCpu.Value = cpu;

                // ── Сколько гифок в памяти: главный источник нагрузки ──
                int gifFrames = Services.GifFrameCache.CachedFrameCount;
                txtGifCache.Text = gifFrames > 0
                    ? $"{gifFrames} кадров в памяти"
                    : "нет загруженных";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Монитор] обновление: {ex.Message}");
            }
        }

        /// <summary>
        /// Оценка загрузки процессора по времени, которое процесс провёл в работе.
        ///
        /// Запасной способ: счётчик производительности доступен не всегда, а это
        /// работает везде и даёт достаточную точность.
        /// </summary>
        private double EstimateCpuByTime(Process process)
        {
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;

            if (_lastSample == DateTime.MinValue)
            {
                _lastSample = now;
                _lastCpuTime = cpuTime;
                return 0;
            }

            double elapsedMs = (now - _lastSample).TotalMilliseconds;
            double usedMs = (cpuTime - _lastCpuTime).TotalMilliseconds;

            _lastSample = now;
            _lastCpuTime = cpuTime;

            if (elapsedMs <= 0) return 0;

            // Делим на число ядер: 100% означает загрузку всех ядер
            return usedMs / elapsedMs / Math.Max(1, Environment.ProcessorCount) * 100.0;
        }

        #region === СВЕДЕНИЯ О ПАМЯТИ СИСТЕМЫ ===

        // GlobalMemoryStatusEx — самый надёжный способ узнать память без
        // сторонних библиотек. Через WMI пришлось бы ждать и подключать модуль.
        [StructLayout(LayoutKind.Sequential)]
        private class MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

        private static MemoryStatus ReadMemoryStatus()
        {
            var status = new MemoryStatus
            {
                Length = (uint)Marshal.SizeOf(typeof(MemoryStatus))
            };

            if (!GlobalMemoryStatusEx(status)) return null;
            return status;
        }

        private static double GetTotalMemoryMb()
        {
            var status = ReadMemoryStatus();
            return status == null ? 0 : status.TotalPhysical / 1024.0 / 1024.0;
        }

        private static double GetAvailableMemoryMb()
        {
            var status = ReadMemoryStatus();
            return status == null ? 0 : status.AvailablePhysical / 1024.0 / 1024.0;
        }

        #endregion
    }
}
