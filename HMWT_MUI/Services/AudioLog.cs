using System;
using System.Diagnostics;
using System.IO;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Общий журнал аудио: в одну строку пишем версию, процесс и событие.
    /// По нему видно, какая сборка что отправила/приняла — без этого
    /// «звук не дошёл» приходилось угадывать.
    /// </summary>
    public static class AudioLog
    {
        public static void Write(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "hmwt-audio.log");
                string line = $"{DateTime.Now:HH:mm:ss.fff} [{Process.GetCurrentProcess().Id}] {message}{Environment.NewLine}";
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.AppendAllText(path, line, new System.Text.UTF8Encoding(false));
                        return;
                    }
                    catch (System.IO.IOException)
                    {
                        System.Threading.Thread.Sleep(15);
                    }
                }
            }
            catch { }
        }
    }
}
