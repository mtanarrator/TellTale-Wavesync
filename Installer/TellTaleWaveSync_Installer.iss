; =====================================================================
; TellTale WaveSync - Official Inno Setup Installer Script
; Targets: VEGAS Pro 2027, 22, 21, 20, 19 Application Extensions
; =====================================================================

#define AppName "TellTale WaveSync"
#define AppVersion "2.0.0"
#define AppPublisher "TellTale FX"
#define AppURL "https://www.telltale.video"
; Base directory is the folder holding this script (the repo's "Installer" folder),
; taken from the Inno preprocessor's SourcePath instead of a hardcoded D:\ path so
; the script compiles unchanged on any machine / checkout location.
#define BaseDir SourcePath

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
OutputBaseFilename=TellTale_WaveSync_Setup_v{#AppVersion}
OutputDir={#BaseDir}\Output
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
; Paths are relative to BaseDir (the "Installer" folder); ".." is the repo root.
; Main extension assembly
Source: "{#BaseDir}\..\VegasAudioSync\bin\Release\net48\TellTale_Audiosync.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace

; Native high-performance SIMD sync engine DLL
Source: "{#BaseDir}\..\vegas_sync_core\target\release\telltale_sync_core.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace

; Interactive HTML5/WebGL Visual Guide
Source: "{#BaseDir}\..\TellTale_WaveSync_Presentation.html"; DestDir: "{app}\Documentation"; Flags: ignoreversion

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

// Converts a VEGAS version folder name into a numeric, sortable key.
// Comparing the folder names as strings wrongly ranks "22.0" above "2027.0" and
// "9.0" above "10.0"; each dot-separated segment is therefore parsed as an
// integer and folded into one key. Non-numeric suffixes are ignored defensively
// (StrToIntDef falls back to 0), so e.g. "22.0-beta" still ranks like "22.0".
//   2027.0 -> 2027000   22.0 -> 22000   10.0 -> 10000   9.0 -> 9000
// Range: Int64 holds up to 6 packed segments (999 * 1000^5 fits, 1000^6 does
// not). Real VEGAS folders use 1-2 segments ("2027.0", "22.0"), so the key is
// safe here; an empty string yields 0, which keeps the first real folder winning.
function VersionFolderKey(const Name: string): Int64;
var
  i, SegStart: Integer;
  Seg: string;
  BreakHere: Boolean;
begin
  Result := 0;
  SegStart := 1;

  for i := 1 to Length(Name) + 1 do
  begin
    BreakHere := i > Length(Name);
    if not BreakHere then
      BreakHere := Name[i] = '.';

    if BreakHere then
    begin
      Seg := Trim(Copy(Name, SegStart, i - SegStart));
      Result := Result * 1000 + StrToIntDef(Seg, 0);
      SegStart := i + 1;
    end;
  end;
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
            if VersionFolderKey(FindRec.Name) > VersionFolderKey(BestVer) then
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
            if VersionFolderKey(FindRec.Name) > VersionFolderKey(BestVer) then
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

// Deletes obsolete WaveSync binaries from a directory. Used for sibling VEGAS
// version folders ("allvegas" task) and during uninstall.
// The two current binaries (TellTale_Audiosync.dll, telltale_sync_core.dll) are
// deliberately not listed here: install still has to copy them and uninstall
// removes them explicitly.
procedure RemoveLegacyBinaries(const Dir: string);
var
  Names: array[0..7] of string;
  i: Integer;
begin
  Names[0] := 'VegasAudioSync.dll';
  Names[1] := 'VegasAudioSync.dll.old';
  Names[2] := 'TellTaleWaveSync.dll';
  Names[3] := 'TellTaleWaveSync.dll.stale';
  Names[4] := 'vegas_sync_core.dll';
  Names[5] := 'VegasAudioSync.pdb';
  Names[6] := 'TellTaleWaveSync.pdb';
  Names[7] := 'vegas_sync_core.pdb';

  for i := 0 to 7 do
    DeleteFile(AddBackslash(Dir) + Names[i]);
end;

// If "Install to all detected VEGAS versions" task is checked, copy to all sibling versions
procedure CurStepChanged(CurStep: TSetupStep);
var
  BaseDirs: array[0..1] of string;
  i: Integer;
  FindRec: TFindRec;
  TargetExtDir: string;
  AppDir: string;
  SourceDll, SourceCore: string;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('allvegas') then
  begin
    BaseDirs[0] := ExpandConstant('{commonappdata}\Vegas Pro');
    BaseDirs[1] := ExpandConstant('{commonappdata}\VEGAS Pro');

    SourceDll := ExpandConstant('{app}\TellTale_Audiosync.dll');
    SourceCore := ExpandConstant('{app}\telltale_sync_core.dll');
    AppDir := RemoveBackslashUnlessRoot(WizardDirValue);

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
                if CompareText(RemoveBackslashUnlessRoot(TargetExtDir), AppDir) <> 0 then
                begin
                  ForceDirectories(TargetExtDir);
                  // Sibling folders are not covered by [InstallDelete], so drop the
                  // obsolete WaveSync binaries here before laying down the new ones.
                  RemoveLegacyBinaries(TargetExtDir);
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

// Deletes the two current binaries plus the legacy names from every VEGAS version
// folder under {commonappdata}\Vegas Pro and {commonappdata}\VEGAS Pro, except the
// selected install folder itself (those files belong to the normal [Files] entries
// and are already uninstalled by the time this runs).
// Needed because the "allvegas" task copies the binaries with CopyFile, which the
// uninstaller does not track. DeleteFile() returns False on failure instead of
// raising, so a locked DLL (VEGAS still running) never aborts the uninstall.
procedure RemoveSiblingBinaries();
var
  BaseDirs: array[0..1] of string;
  Binaries: array[0..1] of string;
  i, j: Integer;
  FindRec: TFindRec;
  TargetExtDir: string;
  AppDir: string;
begin
  AppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));

  BaseDirs[0] := ExpandConstant('{commonappdata}\Vegas Pro');
  BaseDirs[1] := ExpandConstant('{commonappdata}\VEGAS Pro');

  Binaries[0] := 'TellTale_Audiosync.dll';
  Binaries[1] := 'telltale_sync_core.dll';

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
              TargetExtDir := RemoveBackslashUnlessRoot(
                BaseDirs[i] + '\' + FindRec.Name + '\Application Extensions');
              if DirExists(TargetExtDir) and
                 (CompareText(TargetExtDir, AppDir) <> 0) then
              begin
                for j := 0 to 1 do
                  DeleteFile(TargetExtDir + '\' + Binaries[j]);
                RemoveLegacyBinaries(TargetExtDir);
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

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // usPostUninstall: the {app} files are already gone, so only the untracked
  // sibling copies from the "allvegas" task remain to be cleaned up.
  if CurUninstallStep = usPostUninstall then
    RemoveSiblingBinaries();
end;

