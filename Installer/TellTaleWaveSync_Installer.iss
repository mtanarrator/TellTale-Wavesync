; =====================================================================
; TellTale WaveSync - Official Inno Setup Installer Script
; Targets: VEGAS Pro 2027, 22, 21, 20, 19 Application Extensions
; =====================================================================

#define AppName "TellTale WaveSync"
#define AppVersion "1.3.1"
#define AppPublisher "TellTale Audio Tools"
#define AppURL "https://github.com"
#define BaseDir "D:\VEGAS Extensions\VEGAS Audio Sync"

[Setup]
AppId={{D37E6081-3E2A-4D78-9B90-1DEB0703C14F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={code:GetDefaultVegasDir}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DirExistsWarning=no
EnableDirDoesntExistWarning=no
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
OutputBaseFilename=TellTale_WaveSync_Setup_v1.3.1
OutputDir={#BaseDir}\Installer\Output
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\TellTale_Audiosync.dll
UninstallDisplayName={#AppName} (VEGAS Pro Extension)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "allvegas"; Description: "Install to all detected VEGAS Pro versions on this computer"; Flags: unchecked
Name: "docshortcut"; Description: "Create Desktop shortcut to the Interactive Visual Guide"; Flags: unchecked

[Files]
; Main extension assembly
Source: "{#BaseDir}\VegasAudioSync\bin\Release\net48\TellTale_Audiosync.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace

; Native high-performance SIMD sync engine DLL
Source: "{#BaseDir}\vegas_sync_core\target\release\telltale_sync_core.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace

; Interactive HTML5/WebGL Visual Guide
Source: "{#BaseDir}\TellTale_WaveSync_Presentation.html"; DestDir: "{app}\Documentation"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\TellTale WaveSync - Visual Guide"; Filename: "{app}\Documentation\TellTale_WaveSync_Presentation.html"; Tasks: docshortcut

[InstallDelete]
; Clean up any obsolete/legacy DLL names from previous versions
Type: files; Name: "{app}\VegasAudioSync.dll"
Type: files; Name: "{app}\VegasAudioSync.dll.old"
Type: files; Name: "{app}\TellTaleWaveSync.dll"
Type: files; Name: "{app}\TellTaleWaveSync.dll.stale"
Type: files; Name: "{app}\TellTaleWaveSync.pdb"
Type: files; Name: "{app}\vegas_sync_core.dll"
Type: files; Name: "{app}\vegas_sync_core.pdb"

[Code]
// Returns true only for real version folders such as "2027.0" or "22.0".
// Without this, the "Application Extensions" folder itself (and any other
// non-version sibling) was treated as a VEGAS version, producing the malformed
// path "...\Application Extensions\Application Extensions".
function IsVersionFolder(const Name: string): Boolean;
var
  i: Integer;
  c: Char;
begin
  Result := False;
  if Name = '' then Exit;

  c := Name[1];
  if (c < '0') or (c > '9') then Exit;

  for i := 1 to Length(Name) do
  begin
    c := Name[i];
    if ((c < '0') or (c > '9')) and (c <> '.') then Exit;
  end;

  Result := True;
end;

// Auto-detect the newest installed VEGAS Pro version for default destination path
function GetDefaultVegasDir(Param: string): string;
var
  BaseDir1, BaseDir2: string;
  FindRec: TFindRec;
  BestPath: string;
  BestVer: string;
begin
  BestPath := '';
  BestVer := '';

  // 1. Check BorisFX / Modern VEGAS Pro (C:\ProgramData\Vegas Pro\2027.0\...)
  BaseDir1 := ExpandConstant('{commonappdata}\Vegas Pro');
  if DirExists(BaseDir1) then
  begin
    if FindFirst(BaseDir1 + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
             IsVersionFolder(FindRec.Name) then
          begin
            if FindRec.Name > BestVer then
            begin
              BestVer := FindRec.Name;
              BestPath := BaseDir1 + '\' + FindRec.Name + '\Application Extensions';
            end;
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;

  // 2. Check MAGIX VEGAS Pro (C:\ProgramData\VEGAS Pro\22.0\...)
  BaseDir2 := ExpandConstant('{commonappdata}\VEGAS Pro');
  if DirExists(BaseDir2) then
  begin
    if FindFirst(BaseDir2 + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
             IsVersionFolder(FindRec.Name) then
          begin
            if FindRec.Name > BestVer then
            begin
              BestVer := FindRec.Name;
              BestPath := BaseDir2 + '\' + FindRec.Name + '\Application Extensions';
            end;
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;

  // Fallback if neither existed
  if BestPath = '' then
    Result := ExpandConstant('{commonappdata}\Vegas Pro\2027.0\Application Extensions')
  else
    Result := BestPath;
end;

// If "Install to all detected VEGAS versions" task is checked, copy to all sibling versions
procedure CurStepChanged(CurStep: TSetupStep);
var
  BaseDirs: array[0..1] of string;
  i: Integer;
  FindRec: TFindRec;
  TargetExtDir: string;
  SourceDll, SourceCore: string;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('allvegas') then
  begin
    BaseDirs[0] := ExpandConstant('{commonappdata}\Vegas Pro');
    BaseDirs[1] := ExpandConstant('{commonappdata}\VEGAS Pro');

    SourceDll := ExpandConstant('{app}\TellTale_Audiosync.dll');
    SourceCore := ExpandConstant('{app}\telltale_sync_core.dll');

    for i := 0 to 1 do
    begin
      if DirExists(BaseDirs[i]) then
      begin
        if FindFirst(BaseDirs[i] + '\*', FindRec) then
        begin
          try
            repeat
              if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
                 IsVersionFolder(FindRec.Name) then
              begin
                TargetExtDir := BaseDirs[i] + '\' + FindRec.Name + '\Application Extensions';
                if TargetExtDir <> WizardDirValue then
                begin
                  ForceDirectories(TargetExtDir);
                  CopyFile(SourceDll, TargetExtDir + '\TellTale_Audiosync.dll', False);
                  CopyFile(SourceCore, TargetExtDir + '\telltale_sync_core.dll', False);
                end;
              end;
            until not FindNext(FindRec);
          finally
            FindClose(FindRec);
          end;
        end;
      end;
    end;
  end;
end;
