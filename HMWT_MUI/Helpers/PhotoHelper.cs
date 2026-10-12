using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HMWT_MUI
{
    internal class PhotoHelper
    {

        private static string _photosRoot;
        private static string _charactersPath;
        private static string _itemsPath;
        private static bool _isInitialized = false;
        private static string _spellsPath;
        public static string CharactersPhotoPath
        {
            get
            {
                if (!_isInitialized) Initialize();
                return _charactersPath;
            }
        }

        public static string ItemsPhotoPath
        {
            get
            {
                if (!_isInitialized) Initialize();
                return _itemsPath;
            }
        }

        public static string SpellsPhotoPath
        {
            get
            {
                if (!_isInitialized) Initialize();
                return Path.Combine(_photosRoot, "Spells");
            }
        }

        /// <summary>
        /// Сменился аватар персонажа. На это событие подписаны окна, которые
        /// показывают аватары: главное окно (карточки персонажей) и отряд.
        ///
        /// Без него карточка в главном окне оставалась со старым фото до
        /// перезапуска: окно не знало, что базу изменили из другого окна.
        /// </summary>
        public static event Action<int> CharacterPhotoChanged;

        /// <summary>Сообщить, что у персонажа сменилось фото.</summary>
        public static void RaiseCharacterPhotoChanged(int characterId)
        {
            try { CharacterPhotoChanged?.Invoke(characterId); }
            catch (Exception ex) { Debug.WriteLine($"[PhotoHelper] подписчик смены фото: {ex.Message}"); }
        }
        public static void Initialize()
        {
            if (_isInitialized) return;
            try
            {
                // ФОТО В APPDATA
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string appDir = Path.Combine(appDataPath, "HMWT");

                _photosRoot = Path.Combine(appDir, "CharacterPhoto");
                _charactersPath = Path.Combine(_photosRoot, "Characters");
                _itemsPath = Path.Combine(_photosRoot, "Items");
                _spellsPath = Path.Combine(_photosRoot, "Spells");
                // Копируем из Program Files если есть
                string installedPhotoDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CharacterPhoto");
                if (Directory.Exists(installedPhotoDir))
                {
                    CopyDirectory(installedPhotoDir, _photosRoot);
                }

                if (!Directory.Exists(_charactersPath))
                    Directory.CreateDirectory(_charactersPath);
                if (!Directory.Exists(_itemsPath))
                    Directory.CreateDirectory(_itemsPath);

                string spellsPath = Path.Combine(_photosRoot, "Spells");
                if (!Directory.Exists(spellsPath))
                    Directory.CreateDirectory(spellsPath);

                _isInitialized = true;

                Debug.WriteLine($"[PhotoHelper] AppData: {appDataPath}");
                Debug.WriteLine($"[PhotoHelper] _photosRoot: {_photosRoot}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhotoHelper] Ошибка: {ex.Message}");
                throw;
            }
        }
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            // Копируем все файлы рекурсивно
            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                // Получаем относительный путь вручную
                string relativePath = file.Substring(sourceDir.Length).TrimStart('\\', '/');
                string destFile = Path.Combine(destDir, relativePath);
                string destFileDir = Path.GetDirectoryName(destFile);

                if (!Directory.Exists(destFileDir))
                    Directory.CreateDirectory(destFileDir);

                if (!File.Exists(destFile))
                {
                    File.Copy(file, destFile, true);
                    Debug.WriteLine($"[Photo] Скопировано: {relativePath}");
                }
            }
        }
        public static string SaveSpellPhoto(int charSpellId, string sourceFilePath)
        {
            Debug.WriteLine($"[PhotoHelper] >>> SaveSpellPhoto вызван");
            Debug.WriteLine($"[PhotoHelper] charSpellId: {charSpellId}");
            Debug.WriteLine($"[PhotoHelper] sourceFilePath: {sourceFilePath}");

            if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                Debug.WriteLine("[PhotoHelper] Ошибка: файл не существует или путь пустой");
                return string.Empty;
            }

            Initialize();

            string extension = Path.GetExtension(sourceFilePath);
            string fileName = $"spell_char_{charSpellId}_{DateTime.Now:yyyyMMddHHmmss}{extension}";
            string destPath = Path.Combine(_spellsPath, fileName);

            Debug.WriteLine($"[PhotoHelper] Путь назначения: {destPath}");

            try
            {
                File.Copy(sourceFilePath, destPath, true);
                Debug.WriteLine($"[PhotoHelper] Фото скопировано");

                string relativePath = $"Spells/{fileName}";
                Debug.WriteLine($"[PhotoHelper] Относительный путь: {relativePath}");

                return relativePath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhotoHelper]  Ошибка копирования: {ex.Message}");
                return string.Empty;
            }
        }

        public static bool DeleteSpellPhoto(string relativePath)
        {
            Debug.WriteLine($"[PhotoHelper] >>> DeleteSpellPhoto вызван с: {relativePath}");

            if (string.IsNullOrEmpty(relativePath))
                return false;

            string fullPath = GetAbsolutePath(relativePath);
            Debug.WriteLine($"[PhotoHelper] Полный путь: {fullPath}");

            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                    Debug.WriteLine($"[PhotoHelper] Фото удалено");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PhotoHelper] Ошибка удаления: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine($"[PhotoHelper] Файл не найден");
            }
            return false;
        }
        public static string GetAbsolutePath(string relativePath)
        {
            Debug.WriteLine($"[Photo] >>> GetAbsolutePath вызван с: '{relativePath}'");
            Debug.WriteLine($"[Photo] _isInitialized = {_isInitialized}");
            Debug.WriteLine($"[Photo] _photosRoot = '{_photosRoot}'");

            if (string.IsNullOrEmpty(relativePath))
            {
                Debug.WriteLine("[Photo] ПУСТОЙ ПУТЬ!");
                return string.Empty;
            }

            if (Path.IsPathRooted(relativePath))
            {
                Debug.WriteLine($"[Photo] Абсолютный путь: {relativePath}");
                Debug.WriteLine($"[Photo] Существует: {File.Exists(relativePath)}");
                return relativePath;
            }

            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            Debug.WriteLine($"[Photo] После нормализации: '{normalized}'");

            if (normalized.StartsWith("CharacterPhoto" + Path.DirectorySeparatorChar))
            {
                normalized = normalized.Substring("CharacterPhoto".Length + 1);
                Debug.WriteLine($"[Photo] После обрезки CharacterPhoto: '{normalized}'");
            }

            string fullPath = Path.Combine(_photosRoot, normalized);
            Debug.WriteLine($"[Photo] Итоговый путь: '{fullPath}'");
            Debug.WriteLine($"[Photo] Файл существует: {File.Exists(fullPath)}");

            // Проверим содержимое папки
            if (Directory.Exists(_photosRoot))
            {
                Debug.WriteLine($"[Photo] Содержимое {_photosRoot}:");
                foreach (var dir in Directory.GetDirectories(_photosRoot))
                    Debug.WriteLine($"  [DIR] {Path.GetFileName(dir)}");
                foreach (var file in Directory.GetFiles(_photosRoot))
                    Debug.WriteLine($"  [FILE] {Path.GetFileName(file)}");
            }
            else
            {
                Debug.WriteLine($"[Photo] ВНИМАНИЕ: папка {_photosRoot} НЕ СУЩЕСТВУЕТ!");
            }

            return fullPath;
        }

        public static string SaveCharacterPhoto(int characterId, string sourceFilePath)
        {
            if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
                return string.Empty;

            if (!_isInitialized) Initialize();

            string extension = Path.GetExtension(sourceFilePath);
            string fileName = $"char_{characterId}_{DateTime.Now:yyyyMMddHHmmss}{extension}";
            string destPath = Path.Combine(_charactersPath, fileName);

            File.Copy(sourceFilePath, destPath, true);
            Debug.WriteLine($"[PhotoHelper] Фото сохранено: {destPath}");

            return $"Characters/{fileName}";
        }
        public static string SaveIndividualSpellPhoto(int idCharSpell, string sourceFilePath)
        {
            if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
                return string.Empty;

            Initialize();

            string extension = Path.GetExtension(sourceFilePath);
            string fileName = $"spell_char_{idCharSpell}_{DateTime.Now:yyyyMMddHHmmss}{extension}";
            string destPath = Path.Combine(SpellsPhotoPath, fileName);

            File.Copy(sourceFilePath, destPath, true);
            Debug.WriteLine($"[PhotoHelper] Индивидуальное фото заклинания сохранено: {destPath}");

            return $"Spells/{fileName}";
        }

        // Для общего фото (сохраняем с id_spell)
        public static string SaveDefaultSpellPhoto(int spellId, string sourceFilePath)
        {
            if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
                return string.Empty;

            Initialize();

            string extension = Path.GetExtension(sourceFilePath);
            string fileName = $"spell_default_{spellId}_{DateTime.Now:yyyyMMddHHmmss}{extension}";
            string destPath = Path.Combine(SpellsPhotoPath, fileName);

            File.Copy(sourceFilePath, destPath, true);
            Debug.WriteLine($"[PhotoHelper] Общее фото заклинания сохранено: {destPath}");

            return $"Spells/{fileName}";
        }
        /// <summary>
        /// Сохраняет фото предмета инвентаря.
        /// ВАЖНО: кладём в GetWritableRoot()\Items — ровно туда, откуда его находит
        /// ResolvePhotoPath. Прежний путь (CharacterPhoto\Items) не читался никем,
        /// из-за чего фото выглядело потерянным.
        /// </summary>
        public static string SaveItemPhoto(int itemId, string sourceFilePath)
        {
            if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
                return string.Empty;

            if (!_isInitialized) Initialize();

            string extension = Path.GetExtension(sourceFilePath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

            string dir = Path.Combine(GetWritableRoot(), "Items");
            Directory.CreateDirectory(dir);

            // Убираем прежние файлы этого предмета (расширение могло измениться)
            try
            {
                foreach (var old in Directory.GetFiles(dir, $"item_{itemId}_*"))
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }

            string fileName = $"item_{itemId}_{DateTime.Now:yyyyMMddHHmmss}{extension.ToLowerInvariant()}";
            string destPath = Path.Combine(dir, fileName);

            File.Copy(sourceFilePath, destPath, true);
            Debug.WriteLine($"[PhotoHelper] Фото предмета сохранено: {destPath}");

            return $"Items/{fileName}";
        }

        public static bool DeletePhoto(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return false;

            if (!_isInitialized) Initialize();

            string fullPath = GetAbsolutePath(relativePath);

            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                    Debug.WriteLine($"[PhotoHelper] Удалено: {fullPath}");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PhotoHelper] Ошибка удаления: {ex.Message}");
                }
            }
            return false;
        }

        public static void CleanupUnusedCharacterPhotos(string dbPath)
        {
            if (!_isInitialized) Initialize();

            if (!Directory.Exists(_charactersPath)) return;

            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var conn = new SqliteConnection(dbPath))
                {
                    conn.Open();
                    var cmd = new SqliteCommand("SELECT PhotoPath FROM CharacterInfo WHERE PhotoPath IS NOT NULL AND PhotoPath != ''", conn);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string path = reader["PhotoPath"].ToString();
                            if (!string.IsNullOrEmpty(path))
                            {
                                string fullPath = GetAbsolutePath(path);
                                usedPaths.Add(fullPath);
                            }
                        }
                    }
                }

                int deletedCount = 0;
                foreach (var file in Directory.GetFiles(_charactersPath, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (!usedPaths.Contains(file))
                    {
                        if (File.GetLastWriteTime(file) > DateTime.Now.AddMinutes(-1))
                            continue;

                        try
                        {
                            File.Delete(file);
                            deletedCount++;
                            Debug.WriteLine($"[PhotoHelper] Удалено неиспользуемое фото: {file}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[PhotoHelper] Ошибка удаления {file}: {ex.Message}");
                        }
                    }
                }

                Debug.WriteLine($"[PhotoHelper] Очистка фото персонажей завершена. Удалено: {deletedCount}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhotoHelper] Ошибка очистки: {ex.Message}");
            }
        }

        public static void CleanupUnusedItemPhotos(string dbPath)
        {
            if (!_isInitialized) Initialize();

            if (!Directory.Exists(_itemsPath)) return;

            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var conn = new SqliteConnection(dbPath))
                {
                    conn.Open();

                    bool hasPhotoColumn = false;
                    var checkColumnCmd = new SqliteCommand("PRAGMA table_info(Inventory)", conn);
                    using (var reader = checkColumnCmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader["name"].ToString() == "PhotoPath")
                            {
                                hasPhotoColumn = true;
                                break;
                            }
                        }
                    }

                    if (!hasPhotoColumn)
                    {
                        Debug.WriteLine("[PhotoHelper] Колонка PhotoPath не найдена в Inventory. Пропускаем очистку.");
                        return;
                    }

                    var cmd = new SqliteCommand(
                        "SELECT PhotoPath, COALESCE(list_photo_path,'') AS list_path FROM Inventory " +
                        "WHERE COALESCE(PhotoPath,'') <> '' OR COALESCE(list_photo_path,'') <> ''", conn);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            // У предмета теперь два фото: основное и отдельное для
                            // строки списка. Учитываем оба, иначе уборка удалит
                            // картинку, которая ещё используется
                            string path = reader["PhotoPath"]?.ToString();
                            string listPath = reader["list_path"]?.ToString();

                            if (!string.IsNullOrEmpty(path)) usedPaths.Add(GetAbsolutePath(path));
                            if (!string.IsNullOrEmpty(listPath)) usedPaths.Add(GetAbsolutePath(listPath));
                        }
                    }
                }

                int deletedCount = 0;
                foreach (var file in Directory.GetFiles(_itemsPath, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (!usedPaths.Contains(file))
                    {
                        try
                        {
                            File.Delete(file);
                            deletedCount++;
                            Debug.WriteLine($"[PhotoHelper] Удалено неиспользуемое фото предмета: {file}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[PhotoHelper] Ошибка удаления {file}: {ex.Message}");
                        }
                    }
                }

                Debug.WriteLine($"[PhotoHelper] Очистка фото предметов завершена. Удалено: {deletedCount}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhotoHelper] Ошибка очистки: {ex.Message}");
            }
        }

        public static void CleanupOldPhotos()
        {
            if (!_isInitialized) Initialize();

            if (!Directory.Exists(_charactersPath)) return;

            var files = Directory.GetFiles(_charactersPath, "char_*.*");
            var grouped = files.GroupBy(f =>
            {
                string name = Path.GetFileName(f);
                int underscoreIndex = name.IndexOf('_', 5);
                if (underscoreIndex > 0)
                {
                    string idPart = name.Substring(5, underscoreIndex - 5);
                    if (int.TryParse(idPart, out int id))
                        return id;
                }
                return -1;
            });

            foreach (var group in grouped)
            {
                if (group.Key == -1) continue;

                var sorted = group.OrderByDescending(f => File.GetLastWriteTime(f));
                var latest = sorted.First();

                // Удаляем только файлы, которые старше 1 минуты
                foreach (var oldFile in sorted.Skip(1))
                {
                    try
                    {
                        // Не удаляем свежие файлы (моложе 1 минуты)
                        if (File.GetLastWriteTime(oldFile) > DateTime.Now.AddMinutes(-1))
                            continue;

                        File.Delete(oldFile);
                        Debug.WriteLine($"[PhotoHelper] Удалено старое фото: {oldFile}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PhotoHelper] Ошибка удаления {oldFile}: {ex.Message}");
                    }
                }
            }
        }

        public static List<string> GetAllCharacterPhotos(string dbPath)
        {
            var photos = new List<string>();

            try
            {
                using (var conn = new SqliteConnection(dbPath))
                {
                    conn.Open();
                    var cmd = new SqliteCommand("SELECT PhotoPath FROM CharacterInfo WHERE PhotoPath IS NOT NULL AND PhotoPath != ''", conn);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string path = reader["PhotoPath"].ToString();
                            if (!string.IsNullOrEmpty(path))
                                photos.Add(path);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhotoHelper] Ошибка: {ex.Message}");
            }

            return photos;
        }

        public static string GetWritableRoot()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HMWT");
            try { Directory.CreateDirectory(root); }
            catch
            {
                root = Path.Combine(Path.GetTempPath(), "HMWT");   // последний фолбэк
                Directory.CreateDirectory(root);
            }
            return root;
        }

        /// <summary>
        /// Папка для аватаров участников комнаты.
        ///
        /// Полученные по сети фото хранятся отдельно от личных: они принадлежат
        /// не нам, а другому игроку, поэтому лежат внутри папки конкретной комнаты.
        /// </summary>
        public static string GetRoomPhotoFolder(string roomId)
        {
            string safeRoom = SanitizeFolderName(roomId);
            if (string.IsNullOrEmpty(safeRoom)) safeRoom = "room";

            string dir = Path.Combine(GetWritableRoot(), "Rooms", safeRoom, "photos");
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PhotoHelper] папка комнаты: {ex.Message}");
            }
            return dir;
        }

        /// <summary>Убирает из имени всё, что нельзя использовать в пути.</summary>
        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            }
            return sb.ToString();
        }

        public static string GetCharacterPhotoDir(int characterId)
        {
            var dir = Path.Combine(GetWritableRoot(), "Characters", characterId.ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// Ищем по относительному пути: сначала AppData, потом легаси BaseDirectory (старые установки)
        /// <summary>
        /// Загрузить картинку так, чтобы файл не оставался занятым.
        ///
        /// Обычная загрузка по UriSource держит файл открытым, пока картинка
        /// живёт в интерфейсе. Из-за этого смена аватара падала с «файл
        /// используется другим процессом»: старый файл нельзя было ни удалить,
        /// ни перезаписать. Читаем байты в память и декодируем уже из памяти.
        /// </summary>
        public static System.Windows.Media.Imaging.BitmapImage LoadUnlocked(string fullPath)
            => LoadUnlocked(fullPath, 0);

        /// <summary>
        /// То же, но с ограничением размера разбора.
        ///
        /// decodeWidth — ширина, в которой картинка будет показана: кадр читается
        /// сразу в этом размере. Нужно для карточек: фото персонажа бывает
        /// 4608×3072 (5 МБ), а показывается в плитке 250 точек — без ограничения
        /// каждая карточка занимала бы десятки мегабайт памяти. 0 — исходный размер.
        /// </summary>
        public static System.Windows.Media.Imaging.BitmapImage LoadUnlocked(string fullPath, int decodeWidth)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;

            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            // IgnoreImageCache здесь ставить НЕЛЬЗЯ: вместе с чтением из потока
            // WPF ищет картинку в своём кэше по адресу, адреса нет — падает
            // «Key cannot be null», и изображение не показывается вовсе.
            // От кэша всё равно ничего не зависит: кадр читается из файла.
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;

            // Поток закрываем ТОЛЬКО после EndInit: картинка читается именно там,
            // из закрытого потока получается недогруженный кадр, и Freeze() падает
            // с «This Freezable cannot be frozen». Из-за этого большой портрет
            // (клик по аватару) не открывался вовсе.
            using (var stream = File.OpenRead(fullPath))
            {
                bmp.StreamSource = stream;
                bmp.EndInit();
            }

            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// Забыть картинку в кэше кадров: файл по этому пути заменён.
        ///
        /// Нужно потому, что у аватара персонажа имя файла не меняется
        /// (Characters/&lt;id&gt;/Avatar.png). Кэш узнаёт картинку по пути, поэтому
        /// возвращал прежний кадр, и аватар в открытом окне оставался старым.
        /// </summary>
        public static void ForgetPhotoCache(string relativeOrAbsolute)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolute)) return;

            try
            {
                string full = ResolvePhotoPath(relativeOrAbsolute);
                if (string.IsNullOrEmpty(full)) return;
                Services.GifFrameCache.Invalidate(full);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhotoHelper] сброс кэша '{relativeOrAbsolute}': {ex.Message}");
            }
        }

        /// <summary>
        /// Сохранить аватар персонажа. Пишем во временный файл рядом и переносим
        /// на место: если картинка ещё держится интерфейсом, повторяем попытку,
        /// а не показываем ошибку «файл занят».
        /// </summary>
        public static string SaveCharacterAvatar(int characterId, string sourceFile, string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
            string dir = GetCharacterPhotoDir(characterId);
            Directory.CreateDirectory(dir);

            string name = "Avatar" + extension.ToLowerInvariant();
            string dest = Path.Combine(dir, name);
            string temp = Path.Combine(dir, name + ".new");

            File.Copy(sourceFile, temp, true);

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    // сначала убираем прежние аватары с другим расширением
                    foreach (var old in Directory.GetFiles(dir, "Avatar.*"))
                    {
                        if (string.Equals(old, temp, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(old, dest, StringComparison.OrdinalIgnoreCase)) continue;
                        try { File.Delete(old); } catch { }
                    }

                    File.Copy(temp, dest, true);
                    try { File.Delete(temp); } catch { }

                    // Файл заменён по тому же пути — кэш кадров обязан об этом узнать,
                    // иначе окно персонажа покажет прежнюю картинку
                    Services.GifFrameCache.Invalidate(dest);

                    return $"Characters/{characterId}/{name}";
                }
                catch (IOException) when (attempt < 4)
                {
                    // Своя же картинка могла ещё не отпустить файл — даём ей мгновение
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    System.Threading.Thread.Sleep(80);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    System.Threading.Thread.Sleep(80);
                }
            }

            try { File.Delete(temp); } catch { }
            throw new IOException("не удалось записать файл аватара: он занят другим процессом");
        }



        public static string ResolvePhotoPath(string relativeOrAbsolute)
        {
            if (string.IsNullOrEmpty(relativeOrAbsolute)) return null;
            if (Path.IsPathRooted(relativeOrAbsolute))
                return File.Exists(relativeOrAbsolute) ? relativeOrAbsolute : null;
            var full = Path.Combine(GetWritableRoot(), relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? full : null;
        }

        /// Одноразовый перенос старых фото из BaseDirectory в AppData (best effort)
        public static void MigrateLegacyPhotos()
        {
            try
            {
                var legacyRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Characters");
                if (!Directory.Exists(legacyRoot)) return;
                var newRoot = Path.Combine(GetWritableRoot(), "Characters");
                Directory.CreateDirectory(newRoot);
                foreach (var dir in Directory.GetDirectories(legacyRoot))
                {
                    var target = Path.Combine(newRoot, Path.GetFileName(dir));
                    if (Directory.Exists(target)) continue;
                    DirectoryCopy(dir, target);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PhotoMigrate] {ex.Message}"); }
        }

        private static void DirectoryCopy(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                DirectoryCopy(dir, Path.Combine(target, Path.GetFileName(dir)));
        }


    }
}
