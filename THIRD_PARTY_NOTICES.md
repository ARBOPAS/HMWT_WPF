# Сторонние компоненты и лицензии

HMWT Гримуар использует сторонние библиотеки и материалы. Их лицензии
распространяются на соответствующие части и не меняют лицензию самого
проекта (AGPL-3.0-only).

## NuGet-пакеты

| Пакет | Назначение | Лицензия |
|---|---|---|
| EntityFramework 6.5.2 | доступ к SQLite через EF6 | Apache-2.0 |
| gong-wpf-dragdrop 4.0.0 | drag & drop в WPF | MIT |
| Microsoft.Bcl.AsyncInterfaces | асинхронные интерфейсы | MIT |
| Microsoft.Web.WebView2 1.0.4191.47 | WebView2 для заметок и чата | условия Microsoft |
| Microsoft.Data.Sqlite / .Core | SQLite | MIT |
| Newtonsoft.Json | JSON | MIT |
| SQLitePCLRaw.* / SourceGear.sqlite3 | SQLite и обвязка | Apache-2.0 / Public Domain |
| System.* | стандартные библиотеки .NET | MIT |

Точные тексты лицензий поставляются вместе с пакетами NuGet и доступны на
nuget.org. При распространении сборки сохраняйте уведомления, которые
поставляются с пакетами.

## WebView2

`Microsoft.Web.WebView2` и распространяемый `WebView2Loader.dll` используются
по условиям Microsoft. WebView2 Runtime устанавливается отдельно и не входит
в исходники проекта; при распространении установщика соблюдайте условия
Microsoft для WebView2 Runtime.

## Иконки

Набор векторных иконок — Lucide, лицензия ISC.
Текст уведомления: `HMWT_MUI/Resources/ICONS-LICENSE.txt`.

## SRD и справочные тексты

Справочные тексты SRD 5.1 / 5.2 принадлежат Wizards of the Coast и
распространяются по CC-BY-4.0:
https://creativecommons.org/licenses/by/4.0/

База `5e-database` (5e-bits/5e-srd-api) распространяется по MIT
(Adrian Padua, Christopher Ward):
https://github.com/5e-bits/5e-srd-api

Тексты SRD в поставку не включены: пользователь импортирует их отдельно.
Импортированные тексты остаются под CC-BY-4.0 и не становятся частью
AGPL-кода. При публикации материалов указывайте авторство Wizards of the Coast.

## Ассеты проекта

Логотип, изображения и звуковые файлы проекта (`HmwtLogo`, `CursedBro`,
`Assets/notify*.wav`) являются ассетами проекта. Права на название и логотип
описаны в `TRADEMARKS.md`.

## Совместимость

Перечисленные зависимости совместимы с AGPL-3.0. Если вы добавляете новую
зависимость, проверьте её лицензию до включения в сборку.
