; ═══════════════════════════════════════════════════════════════════════════
;  HMWT Гримуар — установщик (Inno Setup 6 / 7)
;
;  ── КАК СОБРАТЬ ─────────────────────────────────────────────────────────────
;  Из корня репозитория:
;      powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
;
;  Скрипт сам: соберёт Release|x64, подготовит staging, возьмёт версию из
;  Properties\AssemblyInfo.cs и вызовет этот файл. Готовый setup.exe появится
;  в installer\Output и на рабочем столе.
;
;  ── ЧТО МЕНЯТЬ ЗДЕСЬ ────────────────────────────────────────────────────────
;  1) Версия. НЕ правьте её в этом файле: она берётся из AssemblyInfo.cs
;     и передаётся сюда ключом /DMyAppVersion. Меняйте версию там (и заодно
;     в Net\NetVersions.cs — эти два значения должны совпадать, иначе игроки
;     не смогут подключиться к комнате друг друга).
;  2) Названия, издатель, иконка, лицензия — константы ниже.
;  3) Набор файлов — раздел [Files].
;  4) Ярлыки — раздел [Icons].
;  5) Действия после установки — раздел [Run].
;
;  ── ЧТО НЕЛЬЗЯ МЕНЯТЬ БЕЗ ОСТОРОЖНОСТИ ──────────────────────────────────────
;  AppId: по нему Windows понимает, что это та же программа. Если его изменить,
;  новая версия встанет рядом со старой, а не обновит её.
;
;  ── ДАННЫЕ ПОЛЬЗОВАТЕЛЯ ─────────────────────────────────────────────────────
;  База и фотографии лежат в %APPDATA%\HMWT — это данные игрока, а не программы:
;  персонажи, инвентарь, заметки, комнаты. Поэтому установщик их НЕ перезаписывает,
;  а только делает резервную копию базы: схему приводит в порядок само приложение
;  при запуске. При удалении программа спрашивает, удалять ли данные и фотографии.
; ═══════════════════════════════════════════════════════════════════════════

; ── Основные данные о программе ──────────────────────────────────────────────
#define MyAppName "HMWT Гримуар"
#define MyAppShortName "HMWT"
#define MyAppPublisher "HMWT"
#define MyAppExeName "HMWT_MUI.exe"

; ── Папки: задаются из build-installer.ps1, здесь значения по умолчанию ──────
#ifndef StagingDir
  #define StagingDir "staging"
#endif
#ifndef OutputDir
  #define OutputDir "Output"
#endif
; Версия приходит ключом /DMyAppVersion=... из AssemblyInfo.cs
#ifndef MyAppVersion
  #define MyAppVersion "1.2.0.5"
#endif

; Имя файла базы и папка данных игрока
#define DbFileName "HMWT_app.db"
#define DataFolderName "HMWT"

[Setup]
; ВАЖНО: идентификатор приложения. Двойная фигурная скобка — это экранирование
; одной, так требует Inno. Менять нельзя, иначе обновление встанет рядом.
AppId={{8F3C1D42-7A55-4E19-9B2C-6D1E5A7C3B90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}

; Версия, которую видно в свойствах setup.exe и в «Программы и компоненты»
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Установщик {#MyAppName}

; Куда ставится. {autopf} = Program Files (с учётом разрядности).
; Имя папки — короткое, без пробелов и слэшей: Inno не принимает слэш в пути.
DefaultDirName={autopf}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
; Не подставлять папку прошлой установки: путь мог измениться между версиями
UsePreviousAppDir=no
DisableProgramGroupPage=yes

UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Имя готового файла: HMWT_Setup_1.2.0.0.exe
OutputDir={#OutputDir}
OutputBaseFilename=HMWT_Setup_{#MyAppVersion}

; Сжатие. lzma2/max даёт минимальный размер, сборка идёт дольше
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Только 64-битные Windows 10 и новее
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Установка в Program Files требует прав администратора.
; Если когда-нибудь захотите ставить без UAC — поставьте PrivilegesRequired=lowest
; и DefaultDirName={localappdata}\{#MyAppShortName}
PrivilegesRequired=admin

; Иконка установщика и лицензия, которую показывают на первом экране
SetupIconFile=assets\app.ico
LicenseFile=..\LICENSE

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Галочка «создать ярлык на рабочем столе» отмечена по умолчанию
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; ── Файлы программы ──────────────────────────────────────────────────────────
; Source — папка staging, которую готовит build-installer.ps1 из bin\x64\Release.
; Оттуда берётся всё, кроме отладочных файлов и данных справочников.
;
; ВАЖНО ПРО ДАННЫЕ: файлы справочников (Data\*) в установщик НЕ входят.
; Их тексты принадлежат издателям, и раздавать их вместе с программой нельзя.
; Приложение работает и без них: предметы и заклинания можно создавать вручную,
; а у кого есть свой файл — импортирует его.
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; ── Ярлыки ──────────────────────────────────────────────────────────────────
; {autoprograms} = меню «Пуск», {autodesktop} = рабочий стол
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Запустить программу сразу после установки (галочка на последнем экране)
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
{ ═══════════════════════════════════════════════════════════════════════════
  Работа с данными игрока

  База и фото лежат в %APPDATA%\HMWT. Установщик их не перезаписывает:
  там персонажи, инвентарь и заметки, и потерять их при обновлении нельзя.
  Вместо этого делается резервная копия, а схему базы приводит в порядок
  само приложение при запуске (оно умеет добавлять недостающие столбцы).

  ВАЖНО: в Inno нельзя объявлять функцию с именем PathIsValid — оно занято.
  ═══════════════════════════════════════════════════════════════════════════ }

var
  DeleteUserDataOnUninstall: Boolean;

{ Папка данных игрока: %APPDATA%\HMWT }
function GetUserDataDir(): String;
begin
  Result := ExpandConstant('{userappdata}\{#DataFolderName}');
end;

function UserDataDirExists(): Boolean;
begin
  Result := DirExists(GetUserDataDir());
end;

{ Человекочитаемый размер, чтобы в вопросе было понятно, о каком объёме речь }
function FormatSize(Bytes: Int64): String;
begin
  if Bytes >= 1073741824 then
    Result := Format('%.1f ГБ', [Bytes / 1073741824.0])
  else if Bytes >= 1048576 then
    Result := Format('%.1f МБ', [Bytes / 1048576.0])
  else if Bytes >= 1024 then
    Result := Format('%.1f КБ', [Bytes / 1024.0])
  else
    Result := Format('%d байт', [Bytes]);
end;

{ Складывает размер файлов в одной папке (без подпапок) }
function SizeOfFolder(Folder: String): Int64;
var
  FindRec: TFindRec;
  Total: Int64;
begin
  Total := 0;
  if FindFirst(Folder + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
          Total := Total + FindRec.SizeLow + (FindRec.SizeHigh * 4294967296);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  Result := Total;
end;

{ Считает размер данных игрока: файлы в корне и в подпапках с фотографиями.
  Для внешнего и внутреннего обхода используем РАЗНЫЕ записи поиска:
  одна переменная на оба цикла ломает обход. }
function GetUserDataSize(): Int64;
var
  Outer: TFindRec;
  Total: Int64;
  SubDir: String;
begin
  Total := 0;
  if not DirExists(GetUserDataDir()) then
  begin
    Result := 0;
    Exit;
  end;

  { Файлы в корне папки данных: база }
  Total := Total + SizeOfFolder(GetUserDataDir());

  { Подпапки: фотографии персонажей, предметов, заклинаний, профиль ведущего }
  if FindFirst(GetUserDataDir() + '\*', Outer) then
  begin
    try
      repeat
        if ((Outer.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (Outer.Name <> '.') and (Outer.Name <> '..') then
        begin
          SubDir := GetUserDataDir() + '\' + Outer.Name;
          Total := Total + SizeOfFolder(SubDir);
        end;
      until not FindNext(Outer);
    finally
      FindClose(Outer);
    end;
  end;

  Result := Total;
end;

{ Резервная копия базы перед обновлением: если что-то пойдёт не так,
  старая база останется рядом и её можно будет вернуть }
procedure BackupDatabase();
var
  Src, Dst: String;
begin
  Src := GetUserDataDir() + '\{#DbFileName}';
  if not FileExists(Src) then
    Exit;

  Dst := Src + '.backup';
  if FileCopy(Src, Dst, False) then
    Log('Резервная копия базы: ' + Dst)
  else
    Log('Не удалось сделать резервную копию базы');
end;

procedure InitializeWizard();
begin
  DeleteUserDataOnUninstall := False;
end;

{ После распаковки файлов: делаем копию базы, ничего не удаляя }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if UserDataDirExists() then
      BackupDatabase();
  end;
end;

{ ── Удаление ─────────────────────────────────────────────────────────────── }

function InitializeUninstall(): Boolean;
var
  Size: Int64;
  Answer: Integer;
begin
  Result := True;

  if not UserDataDirExists() then
    Exit;

  Size := GetUserDataSize();

  Answer := MsgBox('Удалить данные игрока?' + #13#10 + #13#10 +
                   'В папке' + #13#10 +
                   '  ' + GetUserDataDir() + #13#10 +
                   'хранятся персонажи, инвентарь, заметки и фотографии (' +
                   FormatSize(Size) + ').' + #13#10 + #13#10 +
                   'Да — удалить всё вместе с программой.' + #13#10 +
                   'Нет — оставить данные: база и фотографии не будут тронуты.' + #13#10 + #13#10 +
                   'ВАЖНО: папка Rooms с сохранёнными комнатами не удаляется ни в каком случае.',
                   mbConfirmation, MB_YESNO);

  DeleteUserDataOnUninstall := (Answer = IDYES);

  { Отдельно спрашиваем про фотографии: их потеря заметнее всего,
    потому что игроки загружают их вручную }
  if DeleteUserDataOnUninstall then
  begin
    Answer := MsgBox('Удалить также фотографии и аватары?' + #13#10 + #13#10 +
                     'Они лежат в подпапках Characters, Items, Spells и DmProfile.' + #13#10 + #13#10 +
                     'Да — удалить.' + #13#10 +
                     'Нет — оставить фотографии, удалив только базу.',
                     mbConfirmation, MB_YESNO);

    if Answer = IDNO then
    begin
      DeleteFile(GetUserDataDir() + '\{#DbFileName}');
      DeleteFile(GetUserDataDir() + '\{#DbFileName}-journal');
      DeleteFile(GetUserDataDir() + '\{#DbFileName}-wal');
      DeleteFile(GetUserDataDir() + '\{#DbFileName}-shm');
      DeleteUserDataOnUninstall := False;
    end;
  end;

  Result := True;
end;

/// <summary>
/// Удаляет данные игрока, НЕ трогая папку Rooms.
///
/// В комнатах лежит история игры: участники, переписка, броски. Потерять её
/// из-за переустановки программы куда обиднее, чем настройки, поэтому комнаты
/// остаются всегда — при следующем запуске приложение их найдёт и предложит
/// продолжить.
/// </summary>
procedure DeleteUserDataExceptRooms();
var
  FindRec: TFindRec;
  ItemPath: String;
begin
  if not DirExists(GetUserDataDir()) then
    Exit;

  if FindFirst(GetUserDataDir() + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          ItemPath := GetUserDataDir() + '\' + FindRec.Name;

          { Папку комнат сохраняем — в ней история игры }
          if (FindRec.Name = 'Rooms') and ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) then
            Log('Комнаты сохранены: ' + ItemPath)
          else
            DelTree(ItemPath, True, True, True);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if DeleteUserDataOnUninstall then
      DeleteUserDataExceptRooms();
  end;
end;
