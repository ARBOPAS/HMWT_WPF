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
                File.AppendAllText(path,
                    $"{DateTime.Now:HH:mm:ss.fff} [{Process.GetCurrentProcess().Id}] {message}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
