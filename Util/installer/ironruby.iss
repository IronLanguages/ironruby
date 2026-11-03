; IronRuby's Windows installer, in the style of RubyInstaller, for Inno Setup 6.
;
; Util/installer/build-installer.sh builds it from the win-x64 release archive that
; Util/package-release.sh makes, and installs the archive's tree unchanged:
;
;   Util/installer/build-installer.sh dist/ironruby-4.0.3-win-x64.zip
;     -> dist/ironruby-4.0.3-win-x64-setup.exe
;
; It installs into C:\IronRuby40-x64 by default, as RubyInstaller installs into
; C:\Ruby40-x64: `igem install` writes the gems into the install tree
; (Src\StdLib\ruby\gems) and their binstubs next to ir.exe, so the directory must be one
; the user can write to without admin rights, and a path with a space in it breaks
; Bundler's RUBYOPT, Rails' templates and mspec. Setup asks whether to install for the
; current user or for all users; either way it can add the install directory, where
; ironruby.cmd, ir.cmd, iirb.cmd and igem.cmd are, to the front of that user's or the
; system's PATH. There is no irb.cmd or gem.cmd: those names stay CRuby's. The
; uninstaller removes the files, whatever IronRuby wrote under Src since (installed gems
; included), and the PATH entry.
;
; build-installer.sh passes these; to run ISCC by hand, pass them the same way:
;   /DAppVersion=4.0.3           the version, as in the archive's name
;   /DNumericVersion=4.0.3       its leading numbers, for the setup program's file version
;   /DSourceDir=C:\...\ironruby-4.0.3-win-x64   the unpacked archive

#ifndef AppVersion
  #error Pass /DAppVersion=<version>, as Util/installer/build-installer.sh does
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<the unpacked win-x64 archive>, as Util/installer/build-installer.sh does
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif

; "40" for 4.0.x, as in RubyInstaller's C:\Ruby40-x64: one install per Ruby series,
; side by side with the others, upgraded in place within it.
#define VersionMajor Copy(AppVersion, 1, Pos(".", AppVersion) - 1)
#define AfterMajor Copy(AppVersion, Pos(".", AppVersion) + 1)
#define VersionMinor Copy(AfterMajor, 1, Pos(".", AfterMajor) - 1)
#define Series VersionMajor + VersionMinor

[Setup]
AppId=IronRuby{#Series}-x64
AppName=IronRuby {#VersionMajor}.{#VersionMinor} (x64)
AppVersion={#AppVersion}
AppVerName=IronRuby {#AppVersion} (x64)
AppPublisher=IronRuby
AppPublisherURL=https://github.com/Largo/ironruby
AppSupportURL=https://github.com/Largo/ironruby/issues
AppUpdatesURL=https://github.com/Largo/ironruby/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={sd}\IronRuby{#Series}-x64
DefaultGroupName=IronRuby {#VersionMajor}.{#VersionMinor} (x64)
DisableProgramGroupPage=yes
; Install for me only (the default, no admin rights) or for all users: the dialog asks,
; and /CURRENTUSER or /ALLUSERS answers it on the command line.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#SourceDir}\License\License.Rtf
ChangesEnvironment=yes
ChangesAssociations=yes
UninstallDisplayName=IronRuby {#AppVersion} (x64)
OutputBaseFilename=ironruby-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "modifypath"; Description: "Add IronRuby to the &PATH: ironruby, ir, iirb, igem"
Name: "assocfiles"; Description: "Run .&rb files with IronRuby when they are opened"; Flags: unchecked

[Files]
; The archive's tree, unchanged.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Earlier builds of this installer put an irb.cmd here, which on the PATH shadowed
; CRuby's irb; an upgrade removes it.
Type: files; Name: "{app}\irb.cmd"

[Icons]
Name: "{group}\Interactive IronRuby"; Filename: "{app}\iirb.cmd"; WorkingDir: "{%USERPROFILE}"
Name: "{group}\Start Command Prompt with IronRuby"; Filename: "{cmd}"; Parameters: "/k ""set ""PATH={app};%PATH%"" & ironruby.cmd -v"""; WorkingDir: "{%USERPROFILE}"
Name: "{group}\IronRuby Notepad"; Filename: "{app}\Samples\Notepad\notepad.cmd"; WorkingDir: "{app}\Samples\Notepad"

[Registry]
Root: HKA; Subkey: "Software\Classes\IronRuby.File"; ValueType: string; ValueData: "Ruby program"; Flags: uninsdeletekey; Tasks: assocfiles
Root: HKA; Subkey: "Software\Classes\IronRuby.File\shell\open\command"; ValueType: string; ValueData: """{app}\ironruby.cmd"" ""%1"" %*"; Tasks: assocfiles
; .rb itself is set in [Code], AssociateRb, which keeps the program it had before.
Root: HKA; Subkey: "Software\Classes\.rb\OpenWithProgids"; ValueType: string; ValueName: "IronRuby.File"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assocfiles

[UninstallDelete]
; Everything under Src is IronRuby's, also what it wrote there after the install: the
; gems igem installed into Src\StdLib\ruby\gems and their binstubs next to ir.exe.
; Setup refuses a directory that already holds other files, so this is never someone
; else's Src.
Type: filesandordirs; Name: "{app}\Src"
Type: filesandordirs; Name: "{app}\Samples"
Type: filesandordirs; Name: "{app}\License"

[Code]
const
  SystemEnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  UserEnvironmentKey = 'Environment';

{ The PATH of the user installing, or the system's when installing for all users. }
function EnvironmentRoot: Integer;
begin
  if IsAdminInstallMode then
    Result := HKEY_LOCAL_MACHINE
  else
    Result := HKEY_CURRENT_USER;
end;

function EnvironmentKey: String;
begin
  if IsAdminInstallMode then
    Result := SystemEnvironmentKey
  else
    Result := UserEnvironmentKey;
end;

function SameDir(Entry, Dir: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(Trim(Entry)), RemoveBackslashUnlessRoot(Dir)) = 0;
end;

{ Path without its entries for Dir; every other entry, empty ones too, stays as it was. }
function PathWithout(Path, Dir: String): String;
var
  Rest, Entry: String;
  Separator: Integer;
  First, Last: Boolean;
begin
  Result := '';
  Rest := Path;
  First := True;
  repeat
    Separator := Pos(';', Rest);
    Last := Separator = 0;
    if Last then
      Entry := Rest
    else
    begin
      Entry := Copy(Rest, 1, Separator - 1);
      Rest := Copy(Rest, Separator + 1, Length(Rest));
    end;
    if (Trim(Entry) = '') or not SameDir(Entry, Dir) then
    begin
      if not First then
        Result := Result + ';';
      Result := Result + Entry;
      First := False;
    end;
  until Last;
end;

{ Read and written as REG_EXPAND_SZ, so entries such as %USERPROFILE%\bin stay unexpanded. }
function ReadPath: String;
begin
  if not RegQueryStringValue(EnvironmentRoot, EnvironmentKey, 'Path', Result) then
    Result := '';
end;

procedure WritePath(Path: String);
begin
  if not RegWriteExpandStringValue(EnvironmentRoot, EnvironmentKey, 'Path', Path) then
    SuppressibleMsgBox('Setup could not change the PATH in ' + EnvironmentKey + '.', mbError, MB_OK, IDOK)
  else
    Log('PATH is now: ' + Path);
end;

{ First, as RubyInstaller puts Ruby first. Every launcher here is i-prefixed or named
  ironruby/ir, so being first shadows none of a CRuby's: irb and gem stay CRuby's. }
procedure AddToPath(Dir: String);
var
  Path: String;
begin
  Path := PathWithout(ReadPath, Dir);
  if Path = '' then
    WritePath(Dir)
  else
    WritePath(Dir + ';' + Path);
end;

procedure RemoveFromPath(Dir: String);
var
  Path, Rest: String;
begin
  Path := ReadPath;
  Rest := PathWithout(Path, Dir);
  if Rest <> Path then
    WritePath(Rest);
end;

function DirIsEmpty(Dir: String): Boolean;
var
  Find: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', Find) then
  try
    repeat
      if (Find.Name <> '.') and (Find.Name <> '..') then
      begin
        Result := False;
        Exit;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ A new or empty directory, or an IronRuby installed there before: the uninstaller removes
  Src, Samples and License from it whole. And a warning for a path with a space in it. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dir := WizardDirValue;
    if DirExists(Dir) and not DirIsEmpty(Dir) and not FileExists(AddBackslash(Dir) + 'ironruby.cmd') then
    begin
      SuppressibleMsgBox(Dir + ' already holds other files. Choose a new or empty directory for IronRuby.',
        mbError, MB_OK, IDOK);
      Result := False;
    end
    else if Pos(' ', Dir) > 0 then
      Result := SuppressibleMsgBox(Dir + ' has a space in it. Bundler (its RUBYOPT), the Rails templates and mspec ' +
        'do not work from such a path. Install there anyway?', mbConfirmation, MB_YESNO, IDYES) = IDYES;
  end;
end;

const
  RbKey = 'Software\Classes\.rb';
  ProgIdKey = 'Software\Classes\IronRuby.File';

{ .rb to IronRuby.File. The program .rb had, such as RubyInstaller's RubyFile, is kept in
  IronRuby.File's PreviousProgId, for the uninstaller to give .rb back to. }
procedure AssociateRb;
var
  Previous: String;
begin
  if RegQueryStringValue(HKA, RbKey, '', Previous) and (Previous <> '') and (Previous <> 'IronRuby.File') then
    RegWriteStringValue(HKA, ProgIdKey, 'PreviousProgId', Previous);
  RegWriteStringValue(HKA, RbKey, '', 'IronRuby.File');
end;

{ Unless another program has taken .rb over since. }
procedure UnassociateRb;
var
  Current, Previous: String;
begin
  if RegQueryStringValue(HKA, RbKey, '', Current) and (Current = 'IronRuby.File') then
  begin
    if RegQueryStringValue(HKA, ProgIdKey, 'PreviousProgId', Previous) then
      RegWriteStringValue(HKA, RbKey, '', Previous)
    else
      RegDeleteValue(HKA, RbKey, '');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('modifypath') then
      AddToPath(ExpandConstant('{app}'));
    if WizardIsTaskSelected('assocfiles') then
      AssociateRb;
  end;
end;

{ Before the uninstaller deletes IronRuby.File, which holds PreviousProgId. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}'));
    UnassociateRb;
  end;
end;
