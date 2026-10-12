using System.IO;

namespace HMWT_MUI
{
    /// <summary>
    /// Единые пути хранилища заметок: картинки и вложения персонажа.
    /// Картинка хранится в Markdown как NoteImages/&lt;id персонажа&gt;/&lt;файл&gt;.
    /// </summary>
    internal static class NotePaths
    {
        /// <summary>Корень картинок персонажа (абсолютный, при необходимости создаётся).</summary>
        public static string GetImagesFolder(int characterId)
        {
            var path = Path.Combine(DatabaseHelper.GetPhotoBasePath(), "NoteImages", characterId.ToString());
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Корень вложений персонажа (абсолютный, при необходимости создаётся).</summary>
        public static string GetAttachmentsFolder(int characterId)
        {
            var path = Path.Combine(DatabaseHelper.GetPhotoBasePath(), "NoteAttachments", characterId.ToString());
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Относительная ссылка для картинки.</summary>
        public static string ToRelativeImage(int characterId, string storedName)
        {
            return "NoteImages/" + characterId + "/" + storedName;
        }

        /// <summary>Относительная ссылка для вложения.</summary>
        public static string ToRelativeAttachment(int characterId, string storedName)
        {
            return "NoteAttachments/" + characterId + "/" + storedName;
        }

        /// <summary>Абсолютный путь по относительной ссылке из Markdown.</summary>
        public static string Resolve(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            if (Path.IsPathRooted(relativePath)) return relativePath;

            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            const string prefix = "CharacterPhoto";
            if (normalized.StartsWith(prefix + Path.DirectorySeparatorChar))
                normalized = normalized.Substring(prefix.Length + 1);

            return Path.Combine(DatabaseHelper.GetPhotoBasePath(), normalized);
        }
    }
}
