# HMWT Гримуар

Редактор персонажа и инструменты ведущего для настольных ролевых игр.
Windows, WPF, .NET Framework 4.8.

## Возможности

- **Лист персонажа** — характеристики, навыки, спасброски, владения, предыстории,
  опыт и уровень, здоровье и стабилизация.
- **Инвентарь** — предметы, экипировка, вес и деньги, торговля, свои предметы,
  фотографии для карточки и для списка.
- **Заклинания** — ячейки и подготовка, школы, урон с добавкой за уровень выше,
  несколько типов урона в одном заклинании, свои заклинания.
- **Атаки** — попадание и урон, несколько частей урона, приёмы и мультиатака.
- **Заметки** — дерево папок, гиперссылки, картинки и гифки.
- **Отряд** — общая комната до 8 человек: состояние персонажей, броски кубиков
  и лента событий.
- **Инструменты ведущего** — трекер инициативы и бестиарий со статблоками,
  живые формулы броска прямо в тексте карточки.

## Установка

1. Скачайте установщик из раздела **Releases** и запустите его.
2. Нужен Windows 10/11 и .NET Framework 4.8.

Данные и фотографии хранятся в `%APPDATA%\HMWT` и при обновлении не теряются.

## Сборка

Путь к MSBuild зависит от выпуска Visual Studio: у 18 это `Insiders`, у 17 —
`Community`/`Professional`/`Enterprise`. Надёжнее спросить у системы:

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

& $msbuild HMWT_MUI\HMWT_MUI.csproj /p:Configuration=Release /p:Platform=x64
```

Если vswhere недоступен, подойдёт путь напрямую, например
`C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe`.

Собираются обе конфигурации: `Release` и `Debug` (в `Debug` включаются
дополнительные проверки отрисовки иконок).

Проект старого формата: `dotnet build` его не собирает. Новый файл исходника
нужно добавить в `HMWT_MUI\HMWT_MUI.csproj` тегом `<Compile Include="..." />`.

Установщик:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Нужен Inno Setup. Готовый файл появится в `installer\Output`.

## Лицензия

MIT — см. [LICENSE](LICENSE).
