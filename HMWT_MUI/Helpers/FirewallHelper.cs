using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Правило брандмауэра Windows для комнаты.
    ///
    /// Ведущий принимает подключения, значит его порт должен быть открыт на входящие.
    /// Windows обычно спрашивает об этом сама — но только когда соединение приходит
    /// извне, а в закрытом профиле сети (например «общественная») правило молча
    /// отклоняется, и игроки не подключаются. Поэтому правило добавляем заранее,
    /// с обычным запросом прав администратора — так же делают другие программы.
    /// </summary>
    public static class FirewallHelper
    {
        /// <summary>Имя правила в брандмауэре: по нему проверяем и удаляем.</summary>
        public const string RuleName = "HMWT Гримуар (отряд)";

        /// <summary>Есть ли уже правило с таким именем.</summary>
        public static bool RuleExists()
        {
            try
            {
                var result = Run("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"", elevate: false, timeoutMs: 8000);
                // Если правила нет, netsh пишет «No rules match the specified criteria.»
                return result.ExitCode == 0 && result.Output.IndexOf("No rules", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Firewall] проверка правила: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Добавляет правило для порта комнаты. Требует прав администратора:
        /// Windows покажет обычный запрос повышения прав.
        /// </summary>
        public static bool AddRule(int port, out string message)
        {
            message = "";
            if (port <= 0 || port > 65535)
            {
                message = "Некорректный порт комнаты.";
                return false;
            }

            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var arguments = new StringBuilder();
                arguments.Append("advfirewall firewall add rule ");
                arguments.Append($"name=\"{RuleName}\" ");
                arguments.Append("dir=in action=allow protocol=TCP ");
                arguments.Append($"localport={port} ");
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    arguments.Append($"program=\"{exe}\" ");
                arguments.Append("profile=any enable=yes");

                var result = Run("netsh", arguments.ToString(), elevate: true, timeoutMs: 60000);

                if (result.ExitCode == 0)
                {
                    message = $"Правило брандмауэра добавлено: входящие TCP на порт {port}.";
                    return true;
                }

                message = result.Canceled
                    ? "Запрос прав администратора отменён — правило не добавлено. " +
                      "Игроки из интернета не смогут подключиться, пока порт закрыт."
                    : "Не удалось добавить правило брандмауэра: " + FirstLine(result.Output);
                return false;
            }
            catch (Exception ex)
            {
                message = "Ошибка при добавлении правила: " + ex.Message;
                Debug.WriteLine($"[Firewall] добавление правила: {ex}");
                return false;
            }
        }

        /// <summary>Что показать пользователю перед запросом прав.</summary>
        public static string ExplainText(int port)
            => "Чтобы друзья могли подключиться, Windows должна разрешить программе " +
               $"принимать входящие подключения (TCP, порт {port}).\n\n" +
               "Сейчас откроется запрос прав администратора — подтвердите его, " +
               "и правило добавится автоматически. Если отказаться, играть можно " +
               "только в одной сети, и то не всегда: в «общественном» профиле сети " +
               "Windows блокирует входящие.\n\n" +
               "Добавить правило брандмауэра?";

        private sealed class RunResult
        {
            public int ExitCode;
            public string Output = "";
            public bool Canceled;
        }

        private static RunResult Run(string fileName, string arguments, bool elevate, int timeoutMs)
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = elevate,
                CreateNoWindow = true,
                RedirectStandardOutput = !elevate,
                RedirectStandardError = !elevate,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (elevate) info.Verb = "runas";   // обычный запрос прав администратора

            var result = new RunResult();

            try
            {
                using (var process = Process.Start(info))
                {
                    if (process == null)
                    {
                        result.ExitCode = -1;
                        return result;
                    }

                    if (elevate)
                    {
                        process.WaitForExit(timeoutMs);
                        result.ExitCode = process.HasExited ? process.ExitCode : -1;
                        return result;
                    }

                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit(timeoutMs);
                    result.Output = output ?? "";
                    result.ExitCode = process.HasExited ? process.ExitCode : -1;
                    return result;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // Пользователь отказался от запроса прав — это не ошибка программы
                result.Canceled = true;
                result.ExitCode = -1;
                return result;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "нет подробностей";

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return "нет подробностей";
        }
    }
}
