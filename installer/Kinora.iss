; Inno Setup installer for Kinora (Windows x64, .NET runtime included).
; Build with build.ps1, which publishes to ..\publish\win-x64 and passes /DAppVersion=<version>.

#ifndef AppVersion
  #error AppVersion is required (pass /DAppVersion=x.y.z)
#endif

#define AppName "Kinora"
#define AppExe "Kinora.exe"
#define SourceDir AddBackslash(SourcePath) + "..\publish\win-x64"

[Setup]
; Never change the AppId: every update would become a second program
AppId={{5B23EC43-5EA0-4BE7-B0E7-037191B0B489}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Phate
AppPublisherURL=https://github.com/FirekeeperPhate/Kinora
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Kinora - movie and TV show library
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin rights); all users (with UAC) can be chosen in the first dialog
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=Kinora-Setup-{#AppVersion}
SetupIconFile=..\src\Kinora\Assets\kinora.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern dynamic
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
; Held by the running app (Program.cs): setup and uninstall ask to close Kinora first
AppMutex=Kinora.Running

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Kinora's own data (library, settings, picture cache) is in %LOCALAPPDATA%\Kinora: neither
; setup nor uninstall touches it
Source: "{#SourceDir}\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Movie and TV show library"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
{ On an update, remove the previous program files first, so no library left over from an older
  version stays next to the new ones. Only files this setup put there: never a folder the user
  chose that holds other things. }
procedure CleanProgramFolder;
var
  App, Name: String;
  FindRec: TFindRec;
begin
  App := ExpandConstant('{app}\');
  if (WizardForm.PrevAppDir = '') or
     (CompareText(AddBackslash(WizardForm.PrevAppDir), App) <> 0) or
     not FileExists(App + '{#AppExe}') then
    Exit;
  { The program first: if it can't be deleted Kinora is still running, and nothing else may go }
  if not DeleteFile(App + '{#AppExe}') then
    Exit;
  if FindFirst(App + '*', FindRec) then
  begin
    try
      repeat
        Name := FindRec.Name;
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and
           (CompareText(Copy(Name, 1, 5), 'unins') <> 0) then
          DeleteFile(App + Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanProgramFolder;
end;
