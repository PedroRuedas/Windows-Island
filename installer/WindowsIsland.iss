; Windows Island installer (Inno Setup 6). Built by installer\build.ps1, which passes:
;   AppVersion, AppDir (published app), Msix (signed sparse package), Cer (its public certificate),
;   CertThumbprint (to remove it again on uninstall), IconFile.

#ifndef AppVersion
  #error Run installer\build.ps1 instead of compiling this script directly.
#endif

[Setup]
AppId={{8C3B6C2E-5B7A-4E7E-9C35-2E4E5B1D9A11}
AppName=Windows Island
AppVersion={#AppVersion}
AppVerName=Windows Island {#AppVersion}
AppPublisher=Pedro Ruedas
AppPublisherURL=https://github.com/PedroRuedas/Windows-Island
AppSupportURL=https://github.com/PedroRuedas/Windows-Island/issues
DefaultDirName={autopf}\Windows Island
DisableProgramGroupPage=yes
; Admin is needed to trust the package certificate machine-wide (required to read Windows notifications).
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Sparse packages ("package with external location") need Windows 10 2004+.
MinVersion=10.0.19041
OutputBaseFilename=WindowsIsland-Setup-{#AppVersion}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\WindowsIsland.exe
UninstallDisplayName=Windows Island
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pt.StartupTask=Iniciar o Windows Island junto com o Windows
en.StartupTask=Start Windows Island with Windows
pt.Options=Opções:
en.Options=Options:
pt.TrustingCert=Instalando o certificado do pacote...
en.TrustingCert=Installing the package certificate...
pt.RegisteringPackage=Ativando as notificações do Windows...
en.RegisteringPackage=Enabling Windows notifications...

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:Options}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Msix}"; DestDir: "{app}\package"; DestName: "WindowsIsland.msix"; Flags: ignoreversion
Source: "{#Cer}"; DestDir: "{app}\package"; DestName: "WindowsIsland.cer"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Windows Island"; Filename: "{app}\WindowsIsland.exe"
Name: "{autodesktop}\Windows Island"; Filename: "{app}\WindowsIsland.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WindowsIsland"; \
    ValueData: """{app}\WindowsIsland.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; 1. Trust the self-signed package certificate (machine-wide TrustedPeople store).
Filename: "{sys}\certutil.exe"; Parameters: "-f -addstore TrustedPeople ""{app}\package\WindowsIsland.cer"""; \
    Flags: runhidden; StatusMsg: "{cm:TrustingCert}"
; 2. Register the sparse package for the user who ran setup: gives WindowsIsland.exe its identity,
;    which Windows requires before an app may read notifications (WhatsApp, Teams...).
;    A Developer Mode registration (from running a dev build) blocks a signed one (0x80073CFB): drop it first.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name WindowsIsland | Where-Object IsDevelopmentMode | Remove-AppxPackage; Add-AppxPackage -Path '{app}\package\WindowsIsland.msix' -ExternalLocation '{app}' -ForceUpdateFromAnyVersion"""; \
    Flags: runhidden runasoriginaluser; StatusMsg: "{cm:RegisteringPackage}"
Filename: "{app}\WindowsIsland.exe"; Description: "{cm:LaunchProgram,Windows Island}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM WindowsIsland.exe /F"; Flags: runhidden; RunOnceId: "StopIsland"
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name WindowsIsland | Remove-AppxPackage"""; \
    Flags: runhidden; RunOnceId: "RemovePackage"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople {#CertThumbprint}"; Flags: runhidden; RunOnceId: "RemoveCert"
