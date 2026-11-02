# Installs IronRuby with the installer build-installer.sh made, checks it from a fresh
# environment, as a new terminal would see it, and uninstalls it again:
#
#   powershell -File Util\installer\test-installer.ps1 dist\ironruby-4.0.3-win-x64-setup.exe
#   powershell -File Util\installer\test-installer.ps1 SETUP.exe -AllUsers    # as admin
#
# It installs silently into the default directory, C:\IronRuby40-x64, which must not
# exist yet. Then the install directory must be first on the user's PATH (the system's
# with -AllUsers), and from there `ironruby -v`, `ir`, `irb` and `igem install rack` must
# work. The uninstaller must remove the directory, gems included, and leave PATH exactly
# as it was before. Needs rubygems.org for rack.
param(
  [Parameter(Mandatory = $true)][string]$Installer,
  [switch]$AllUsers,
  [string]$Dir = "$env:SystemDrive\IronRuby40-x64"
)
$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
  Write-Host "test-installer: FAIL: $message"
  exit 1
}

if ($AllUsers) {
  $scope = 'Machine'
  $environmentKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  $mode = '/ALLUSERS'
} else {
  $scope = 'User'
  $environmentKey = 'HKCU:\Environment'
  $mode = '/CURRENTUSER'
}

# PATH as the registry holds it, %VARIABLES% unexpanded.
function Get-RawPath {
  $key = Get-Item $environmentKey
  $key.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
}

# The PATH a terminal opened now would get.
function Get-FreshPath {
  [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

# Runs a command line in cmd.exe with the fresh PATH, as typed at a new prompt, in the
# user's home: cmd looks in the current directory first, and a checkout has launchers of
# the same names. Without quotes in it: PowerShell 7 and Windows PowerShell pass them on
# to cmd differently.
function Invoke-Fresh([string]$commandLine, [string]$stdin = $null) {
  $saved = $env:Path
  # Not Stop: Windows PowerShell turns a native command's redirected stderr into errors.
  $ErrorActionPreference = 'Continue'
  Push-Location $env:USERPROFILE
  try {
    $env:Path = Get-FreshPath
    if ($stdin) { $output = $stdin | cmd /d /c $commandLine 2>&1 } else { $output = cmd /d /c $commandLine 2>&1 }
    $code = $LASTEXITCODE
  } finally {
    $env:Path = $saved
    Pop-Location
  }
  $text = ($output | Out-String).Trim()
  Write-Host "> $commandLine"
  Write-Host $text
  if ($code -ne 0) { Fail "$commandLine exited with $code" }
  $text
}

if (Test-Path $Dir) { Fail "$Dir exists already; uninstall IronRuby from there first" }
$installer = (Resolve-Path $Installer).Path
$log = Join-Path (Split-Path $installer) 'install.log'
$pathBefore = Get-RawPath

Write-Host "test-installer: installing $installer ($mode)"
$setup = Start-Process -FilePath $installer -Wait -PassThru -ArgumentList @(
  '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', $mode, "/LOG=`"$log`"")
if ($setup.ExitCode -ne 0) { Get-Content $log -Tail 30; Fail "setup exited with $($setup.ExitCode)" }

foreach ($launcher in 'ironruby.cmd', 'ir.cmd', 'iirb.cmd', 'igem.cmd', 'unins000.exe') {
  if (-not (Test-Path (Join-Path $Dir $launcher))) { Fail "no $launcher in $Dir" }
}
# First on the PATH, these would shadow a CRuby's irb and gem.
foreach ($launcher in 'irb.cmd', 'gem.cmd') {
  if (Test-Path (Join-Path $Dir $launcher)) { Fail "$launcher in $Dir would shadow CRuby's" }
}
$first = ((Get-RawPath) -split ';')[0]
if ($first -ne $Dir) { Fail "$Dir is not first on the $scope PATH, $first is" }

$description = Invoke-Fresh 'ironruby -v'
if ($description -notmatch '^ironruby 4\.') { Fail "ironruby -v: $description" }
$where = Invoke-Fresh 'where ironruby'
if (($where -split "`r?`n")[0] -ne "$Dir\ironruby.cmd") { Fail "ironruby is not $Dir\ironruby.cmd" }
$engine = Invoke-Fresh 'ir -e puts(RUBY_ENGINE)'
if ($engine -ne 'ironruby') { Fail "ir: $engine" }
$irb = Invoke-Fresh 'iirb' "puts 6 * 7`n"
if ($irb -notmatch '42') { Fail 'iirb did not run the line it was given' }
Invoke-Fresh 'igem install rack --no-document' | Out-Null
$gems = Join-Path $Dir 'Src\StdLib\ruby\gems\4.0.0\gems'
if (-not (Get-ChildItem $gems -Filter 'rack-*' -Directory)) { Fail "rack is not in $gems" }
$rack = Invoke-Fresh 'ironruby -rrack -e puts(Rack.release)'
if ($rack -notmatch '^\d') { Fail "require 'rack': $rack" }

Write-Host 'test-installer: uninstalling'
$uninstall = Start-Process -FilePath (Join-Path $Dir 'unins000.exe') -Wait -PassThru -ArgumentList @(
  '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
if ($uninstall.ExitCode -ne 0) { Fail "the uninstaller exited with $($uninstall.ExitCode)" }
# The uninstaller runs on from a copy of itself, which removes the directory last.
for ($i = 0; $i -lt 120 -and (Test-Path $Dir); $i++) { Start-Sleep -Seconds 1 }
if (Test-Path $Dir) {
  Get-ChildItem $Dir -Recurse | Select-Object -First 20 | ForEach-Object { Write-Host "  left: $($_.FullName)" }
  Fail "$Dir is still there"
}
$pathAfter = Get-RawPath
if ($pathAfter -ne $pathBefore) { Fail "the $scope PATH changed: was`n  $pathBefore`nis`n  $pathAfter" }
Write-Host "test-installer: OK: installed, ran, installed rack, and uninstalled without a trace"
exit 0
