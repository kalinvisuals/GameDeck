; GameDeck installer (Inno Setup 7). Build it with `./build.ps1 -Installer`, which publishes the
; self-contained app to ./publish first and passes the version from GameDeck.App.csproj.
; Output: ./dist/GameDeck-Setup-<version>.exe

#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\publish\GameDeck.exe")
#endif
#define AppExe "GameDeck.exe"

[Setup]
; Never change AppId: it is how upgrades and the uninstaller find an existing install.
AppId={{6E0B7C1A-3F55-4C1E-9B8D-2A7F4E9C5D31}
AppName=GameDeck
AppVersion={#AppVersion}
AppVerName=GameDeck {#AppVersion}
AppPublisher=kalinvisuals
AppPublisherURL=https://www.gamedeck.ro
AppContact=contact@gamedeck.ro
AppSupportURL=https://github.com/kalinvisuals/GameDeck/issues
AppUpdatesURL=https://github.com/kalinvisuals/GameDeck/releases
AppCopyright=Copyright (C) 2026 kalinvisuals. GPL-3.0-or-later.
DefaultDirName={autopf}\GameDeck
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=no
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
#ifdef Preview
; Only for looking at the wizard while developing: runs without the Windows permission prompt.
PrivilegesRequired=lowest
#else
; Admin rights are needed for Program Files and the firewall rule.
PrivilegesRequired=admin
#endif
; The autostart value goes to the installing user's HKCU on purpose.
UsedUserAreasWarning=no
; Dark wizard on a cobalt-lit background, with the logo beside the welcome text. The artwork is made by
; tools/GameDeck.IconGen in the sizes Setup needs at 100-200 % display scaling.
WizardStyle=modern dark includetitlebar hidebevels
WizardBackImageFile=art\back-596.png,art\back-796.png,art\back-994.png,art\back-1272.png
WizardImageFile=art\side-202.png,art\side-269.png,art\side-336.png,art\side-430.png
WizardSmallImageFile=art\small-58.png,art\small-77.png,art\small-97.png,art\small-124.png
SetupIconFile=..\src\GameDeck.App\GameDeck.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=GameDeck
OutputDir=..\dist
OutputBaseFilename=GameDeck-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
; A running GameDeck is closed before its files are replaced, and not restarted by Restart Manager
; (the Finish page offers to start it instead).
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=GameDeck

[Languages]
; Setup speaks the Windows display language when it is one of these, otherwise English. Romanian has
; no official Inno Setup translation, so Romanian users get the English wizard.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.TaskAutostart=Start GameDeck when I sign in to Windows
english.TaskFirewall=Allow phones and tablets on my private network to connect (Windows Firewall)
english.GroupNetwork=Network:
english.GroupStartup=Startup:
english.LaunchGameDeck=Start GameDeck now
english.DeleteUserData=Also delete your GameDeck settings, paired devices and sounds?%n%n%1%n%nChoose No to keep them for a later reinstall.

spanish.TaskAutostart=Iniciar GameDeck al entrar en Windows
spanish.TaskFirewall=Permitir teléfonos y tabletas de mi red privada (Firewall de Windows)
spanish.GroupNetwork=Red:
spanish.GroupStartup=Inicio:
spanish.LaunchGameDeck=Abrir GameDeck ahora
spanish.DeleteUserData=¿Eliminar también tus ajustes, dispositivos vinculados y sonidos de GameDeck?%n%n%1%n%nElige No para conservarlos para una futura reinstalación.

brazilianportuguese.TaskAutostart=Iniciar o GameDeck quando eu entrar no Windows
brazilianportuguese.TaskFirewall=Permitir celulares e tablets da minha rede privada (Firewall do Windows)
brazilianportuguese.GroupNetwork=Rede:
brazilianportuguese.GroupStartup=Inicialização:
brazilianportuguese.LaunchGameDeck=Abrir o GameDeck agora
brazilianportuguese.DeleteUserData=Excluir também suas configurações, dispositivos pareados e sons do GameDeck?%n%n%1%n%nEscolha Não para mantê-los para uma reinstalação futura.

german.TaskAutostart=GameDeck bei der Anmeldung in Windows starten
german.TaskFirewall=Handys und Tablets im privaten Netzwerk erlauben (Windows-Firewall)
german.GroupNetwork=Netzwerk:
german.GroupStartup=Autostart:
german.LaunchGameDeck=GameDeck jetzt starten
german.DeleteUserData=Auch deine GameDeck-Einstellungen, gekoppelten Geräte und Sounds löschen?%n%n%1%n%nWähle Nein, um sie für eine spätere Neuinstallation zu behalten.

french.TaskAutostart=Démarrer GameDeck à l’ouverture de session Windows
french.TaskFirewall=Autoriser téléphones et tablettes du réseau privé (pare-feu Windows)
french.GroupNetwork=Réseau :
french.GroupStartup=Démarrage :
french.LaunchGameDeck=Lancer GameDeck maintenant
french.DeleteUserData=Supprimer aussi tes réglages, appareils associés et sons GameDeck ?%n%n%1%n%nChoisis Non pour les garder en vue d’une réinstallation.

polish.TaskAutostart=Uruchamiaj GameDeck po zalogowaniu do Windows
polish.TaskFirewall=Zezwalaj telefonom i tabletom z sieci prywatnej (Zapora Windows)
polish.GroupNetwork=Sieć:
polish.GroupStartup=Uruchamianie:
polish.LaunchGameDeck=Uruchom teraz GameDeck
polish.DeleteUserData=Usunąć także ustawienia GameDeck, sparowane urządzenia i dźwięki?%n%n%1%n%nWybierz Nie, aby zachować je na wypadek ponownej instalacji.

russian.TaskAutostart=Запускать GameDeck при входе в Windows
russian.TaskFirewall=Разрешить телефоны и планшеты из частной сети (брандмауэр Windows)
russian.GroupNetwork=Сеть:
russian.GroupStartup=Автозапуск:
russian.LaunchGameDeck=Запустить GameDeck сейчас
russian.DeleteUserData=Удалить также настройки GameDeck, подключённые устройства и звуки?%n%n%1%n%nВыбери «Нет», чтобы сохранить их для повторной установки.

[Tasks]
Name: "firewall"; Description: "{cm:TaskFirewall}"; GroupDescription: "{cm:GroupNetwork}"
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:GroupStartup}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GameDeck"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\GameDeck"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The app's Settings switch owns this value too: an upgrade keeps it on and points it at this install.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GameDeck"; \
  ValueData: """{app}\{#AppExe}"""; Check: ShouldStartWithWindows
; Uninstalling removes it even when it was only turned on from Settings.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GameDeck"; \
  Flags: uninsdeletevalue

[Run]
; Private profile only, scoped to this exe (the same rule the Windows prompt would create, without the prompt).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""GameDeck (LAN)"""; \
  Flags: runhidden; StatusMsg: "Configuring Windows Firewall..."
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""GameDeck (LAN)"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=TCP profile=private enable=yes"; \
  Flags: runhidden; Tasks: firewall; StatusMsg: "Configuring Windows Firewall..."
Filename: "{app}\{#AppExe}"; Parameters: "--open"; Description: "{cm:LaunchGameDeck}"; \
  Flags: nowait postinstall skipifsilent runasoriginaluser
; An update started from the app runs silently and starts the new version by itself.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""GameDeck (LAN)"""; \
  Flags: runhidden; RunOnceId: "DelFirewallRule"

[Code]
// Stops the installed copy only (matched by path), so a development build elsewhere keeps running.
procedure StopInstalledGameDeck;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process GameDeck -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -eq ''' + ExpandConstant('{app}\{#AppExe}') + ''' } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// On when ticked here, or already on (from an earlier install or the app's Settings switch).
function ShouldStartWithWindows: Boolean;
begin
  Result := WizardIsTaskSelected('autostart') or
    RegValueExists(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GameDeck');
end;

// The running copy is closed before its files are replaced, also during a silent update.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopInstalledGameDeck;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopInstalledGameDeck;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{userappdata}\GameDeck');
  if not DirExists(DataDir) or UninstallSilent then Exit;
  if MsgBox(FmtMessage(CustomMessage('DeleteUserData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
