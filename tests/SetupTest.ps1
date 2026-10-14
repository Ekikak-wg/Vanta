# Installer test in a sandbox: files go to a temp folder, registry to HKCU\Software\ReplayOBS-Test.
# Real OBS settings, Start menu, Run key and Desktop are not touched (--test-root).
# ASCII-only on purpose (Windows PowerShell 5.1 + no BOM).
param([Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path $Setup).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('replay-obs-setup-test-' + [guid]::NewGuid().ToString('N'))
$reg = 'HKCU:\Software\ReplayOBS-Test'
$script:failures = 0
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "OK    $what" } else { Write-Host "FAIL  $what"; $script:failures++ } }
function Run([string[]]$extra) {
 $p = Start-Process -FilePath $Setup -ArgumentList (@('--silent', '--no-launch', '--test-root', "`"$root`"") + $extra) -Wait -PassThru -WindowStyle Hidden
 return $p.ExitCode
}
function Ws { Get-Content (Join-Path $root 'obs-studio\plugin_config\obs-websocket\config.json') -Raw | ConvertFrom-Json }
$install = Join-Path $root 'Programs\Replay OBS'
$wsDir = Join-Path $root 'obs-studio\plugin_config\obs-websocket'
try {
 # 1. Existing but disabled WebSocket config: the password must be kept, a backup made.
 New-Item -ItemType Directory -Force $wsDir | Out-Null
 '{"alerts_enabled":false,"auth_required":false,"first_load":false,"server_enabled":false,"server_password":"keep-this-pass","server_port":4460}' | Set-Content -Encoding ASCII (Join-Path $wsDir 'config.json')
 Check ((Run @()) -eq 0) 'install: exit code 0'
 Check (Test-Path (Join-Path $install 'ReplayOverlay.exe')) 'install: ReplayOverlay.exe copied'
 Check (Test-Path (Join-Path $install 'uninstall.exe')) 'install: uninstall.exe copied'
 Check (Test-Path (Join-Path $root 'StartMenu\Replay OBS\Replay Overlay.lnk')) 'install: Start menu shortcut'
 Check (-not (Test-Path (Join-Path $root 'Desktop\Replay Overlay.lnk'))) 'install: no desktop shortcut by default'
 $run = (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Replay Overlay'
 Check ($run -like '*ReplayOverlay.exe" --hidden --from=run') 'install: autostart entry with --hidden'
 Check (Test-Path (Join-Path $root 'Startup\Replay Overlay.lnk')) 'install: Startup folder shortcut (second autostart path)'
 $un = Get-ItemProperty "$reg\Uninstall\ReplayOBS" -ErrorAction SilentlyContinue
 Check ($un.DisplayName -eq 'Replay OBS' -and $un.DisplayVersion -match '^\d+\.\d+\.\d+$' -and $un.UninstallString -like '*uninstall.exe" --uninstall') 'install: Apps & features entry'
 $ws = Ws
 Check ($ws.server_enabled -eq $true -and $ws.auth_required -eq $true) 'websocket: enabled with auth'
 Check ($ws.server_password -eq 'keep-this-pass' -and $ws.server_port -eq 4460) 'websocket: existing password and port kept'
 Check (Test-Path (Join-Path $wsDir 'config.json.replay-obs.bak')) 'websocket: backup of previous config'
 $hash = (Get-FileHash (Join-Path $install 'ReplayOverlay.exe')).Hash
 $prefsFile = Join-Path $install 'overlay-settings.json'
 Check ((Get-Content $prefsFile -Raw | ConvertFrom-Json).launchObs -eq $true) 'install: overlay will launch OBS by default'
 '{"launchObs":true,"excluded":["Telegram"]}' | Set-Content -Encoding ASCII $prefsFile

 # 2. Update over the existing install with other options.
 Check ((Run @('--no-autostart', '--desktop', '--no-obs-launch')) -eq 0) 'update: exit code 0'
 $prefs = Get-Content $prefsFile -Raw | ConvertFrom-Json
 Check ($prefs.launchObs -eq $false -and $prefs.excluded[0] -eq 'Telegram') 'update: launchObs switched off, other overlay settings kept'
 Check ($null -eq (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Replay Overlay') 'update: autostart removed'
 Check (-not (Test-Path (Join-Path $root 'Startup\Replay Overlay.lnk'))) 'update: Startup folder shortcut removed'
 Check (Test-Path (Join-Path $root 'Desktop\Replay Overlay.lnk')) 'update: desktop shortcut created'
 Check ((Get-FileHash (Join-Path $install 'ReplayOverlay.exe')).Hash -eq $hash) 'update: same overlay binary'
 Check ((Ws).server_password -eq 'keep-this-pass') 'update: enabled WebSocket left untouched'

 # 3. No WebSocket config at all: it is created with a random password.
 Remove-Item $wsDir -Recurse -Force
 Check ((Run @()) -eq 0) 'fresh websocket: exit code 0'
 $ws = Ws
 Check ($ws.server_enabled -eq $true -and $ws.auth_required -eq $true -and $ws.server_port -eq 4455) 'fresh websocket: enabled, auth, port 4455'
 Check ($ws.server_password.Length -eq 20 -and $ws.server_password -match '^[A-Za-z0-9]+$') 'fresh websocket: random 20-char password'

 # 4. --no-websocket leaves a disabled config alone.
 '{"server_enabled":false,"server_password":"","server_port":4455}' | Set-Content -Encoding ASCII (Join-Path $wsDir 'config.json')
 Check ((Run @('--no-websocket')) -eq 0) 'no-websocket: exit code 0'
 Check ((Ws).server_enabled -eq $false) 'no-websocket: config not changed'

 # 5. Uninstall.
 Check ((Run @('--uninstall')) -eq 0) 'uninstall: exit code 0'
 Check (-not (Test-Path $install)) 'uninstall: program folder removed'
 Check (-not (Test-Path (Join-Path $root 'StartMenu\Replay OBS'))) 'uninstall: Start menu folder removed'
 Check (-not (Test-Path (Join-Path $root 'Desktop\Replay Overlay.lnk'))) 'uninstall: desktop shortcut removed'
 Check (-not (Test-Path "$reg\Uninstall\ReplayOBS")) 'uninstall: Apps & features entry removed'
 Check (Test-Path (Join-Path $wsDir 'config.json')) 'uninstall: OBS WebSocket config kept'
} finally {
 Remove-Item $reg -Recurse -Force -ErrorAction SilentlyContinue
 Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
if ($script:failures) { Write-Host "RESULT: $script:failures failed"; exit 1 } else { Write-Host 'RESULT: all installer checks passed'; exit 0 }
