# Installer test in a sandbox: files go to a temp folder, registry to HKCU\Software\ReplayOBS-Test.
# Real Start menu, Run key, Desktop and scheduled tasks are not touched (--test-root).
# The recording engine comes from small fake zips (--engine-zip + test-only --engine-sha256/--engine-version): no download.
# ASCII-only on purpose (Windows PowerShell 5.1 + no BOM).
param([Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Setup = (Resolve-Path $Setup).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('vanta-setup-test-' + [guid]::NewGuid().ToString('N'))
$reg = 'HKCU:\Software\ReplayOBS-Test'
$script:failures = 0
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "OK    $what" } else { Write-Host "FAIL  $what"; $script:failures++ } }
function Run([string[]]$extra) {
 $p = Start-Process -FilePath $Setup -ArgumentList (@('--silent', '--no-launch', '--test-root', "`"$root`"") + $extra) -Wait -PassThru -WindowStyle Hidden
 return $p.ExitCode
}
# Fake engine archive: bin\64bit\obs64.exe with given content + a data file.
function FakeZip([string]$name, [string]$content) {
 $src = Join-Path $root "fake-$name"; New-Item -ItemType Directory -Force (Join-Path $src 'bin\64bit'), (Join-Path $src 'data') | Out-Null
 Set-Content -Encoding ASCII (Join-Path $src 'bin\64bit\obs64.exe') $content
 Set-Content -Encoding ASCII (Join-Path $src 'data\readme.txt') 'fake engine'
 $zip = Join-Path $root "$name.zip"; [IO.Compression.ZipFile]::CreateFromDirectory($src, $zip)
 return @{ Zip = $zip; Sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(); Dir = $src }
}
function EngineArgs($z, [string]$version) { return @('--engine-zip', "`"$($z.Zip)`"", '--engine-sha256', $z.Sha, '--engine-version', $version) }
function Text([string]$path) { return (Get-Content $path -Raw).Trim() }
$install = Join-Path $root 'Programs\Vanta'
$engine = Join-Path $install 'engine'
$marker = Join-Path $engine 'replay-engine.json'
$cfg = Join-Path $engine 'config\obs-studio'
$prefsFile = Join-Path $install 'vanta-settings.json'
$legacy = Join-Path $root 'Programs\Replay OBS'
try {
 New-Item -ItemType Directory -Force $root | Out-Null
 $z1 = FakeZip 'engine1' 'engine-one'
 $z2 = FakeZip 'engine2' 'engine-two'

 # 1. Update from the previous name (Replay OBS 1.6): everything moves to Vanta, old traces go away, engine is not downloaded again.
 New-Item -ItemType Directory -Force (Join-Path $legacy 'logs'), (Join-Path $root 'StartMenu\Replay OBS'), (Join-Path $root 'Startup'), (Join-Path $root 'Desktop') | Out-Null
 'old menu' | Set-Content -Encoding ASCII (Join-Path $legacy 'ReplayOverlay.exe')
 Copy-Item $z1.Dir (Join-Path $legacy 'engine') -Recurse
 '{"version":"99.0.1"}' | Set-Content -Encoding ASCII (Join-Path $legacy 'engine\replay-engine.json')
 New-Item -ItemType Directory -Force (Join-Path $legacy 'engine\config\obs-studio') | Out-Null
 'keep-me' | Set-Content -Encoding ASCII (Join-Path $legacy 'engine\config\obs-studio\user.ini')
 '{"excluded":["Telegram"]}' | Set-Content -Encoding ASCII (Join-Path $legacy 'overlay-settings.json')
 'old log' | Set-Content -Encoding ASCII (Join-Path $legacy 'logs\replay-overlay.log')
 foreach ($lnk in @('StartMenu\Replay OBS\Replay Overlay.lnk', 'Startup\Replay Overlay.lnk', 'Desktop\Replay Overlay.lnk')) { 'lnk' | Set-Content -Encoding ASCII (Join-Path $root $lnk) }
 New-Item -Force "$reg\Uninstall\ReplayOBS" | Out-Null; Set-ItemProperty "$reg\Uninstall\ReplayOBS" DisplayVersion '1.6.0'
 New-Item -Force "$reg\Run" | Out-Null; Set-ItemProperty "$reg\Run" 'Replay Overlay' 'old'
 Check ((Run @('--engine-version', '99.0.1')) -eq 0) 'migration: exit code 0 (no download: same engine version)'
 Check (Test-Path (Join-Path $install 'Vanta.exe')) 'migration: Vanta.exe installed'
 Check ((Text (Join-Path $engine 'bin\64bit\obs64.exe')) -eq 'engine-one' -and (Text (Join-Path $cfg 'user.ini')) -eq 'keep-me') 'migration: engine moved with its settings'
 Check ((Get-Content $prefsFile -Raw | ConvertFrom-Json).excluded[0] -eq 'Telegram') 'migration: menu settings moved to vanta-settings.json'
 Check ((Text (Join-Path $install 'logs\replay-overlay.log')) -eq 'old log') 'migration: logs moved'
 Check (-not (Test-Path $legacy)) 'migration: old program folder removed'
 Check (-not (Test-Path "$reg\Uninstall\ReplayOBS")) 'migration: old Apps & features entry removed'
 Check ($null -eq (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Replay Overlay') 'migration: old autostart entry removed'
 Check (-not (Test-Path (Join-Path $root 'StartMenu\Replay OBS')) -and -not (Test-Path (Join-Path $root 'Startup\Replay Overlay.lnk')) -and -not (Test-Path (Join-Path $root 'Desktop\Replay Overlay.lnk'))) 'migration: old shortcuts removed'
 Check ((Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Vanta' -like '*Vanta.exe" --hidden --from=run') 'migration: autostart kept under the new name'
 Check (Test-Path (Join-Path $root 'Desktop\Vanta.lnk')) 'migration: desktop shortcut kept under the new name'
 Check (Test-Path (Join-Path $root 'StartMenu\Vanta\Vanta.lnk')) 'migration: Start menu shortcut'
 Check (Test-Path (Join-Path $root 'Startup\Vanta.lnk')) 'migration: Startup folder shortcut (second autostart path)'
 $un = Get-ItemProperty "$reg\Uninstall\Vanta" -ErrorAction SilentlyContinue
 Check ($un.DisplayName -eq 'Vanta' -and $un.DisplayVersion -eq (Get-Item $Setup).VersionInfo.ProductVersion -and $un.UninstallString -like '*uninstall.exe" --uninstall') ('migration: Apps & features entry, version ' + $un.DisplayVersion)
 Check ($un.EstimatedSize -gt 0) ('migration: size recorded (' + $un.EstimatedSize + ' KB)')
 $hash = (Get-FileHash (Join-Path $install 'Vanta.exe')).Hash

 # 2. Damaged or substituted archive: refused before anything changes.
 $bad = @('--engine-zip', "`"$($z2.Zip)`"", '--engine-sha256', $z1.Sha, '--engine-version', '99.0.2')
 Check ((Run $bad) -ne 0) 'bad checksum: installer fails'
 Check ((Text $marker | ConvertFrom-Json).version -eq '99.0.1' -and (Text (Join-Path $engine 'bin\64bit\obs64.exe')) -eq 'engine-one') 'bad checksum: installed engine untouched'
 Check ((Text (Join-Path $cfg 'user.ini')) -eq 'keep-me') 'bad checksum: engine settings untouched'

 # 3. Update with the same engine version: nothing to download; other options applied.
 Check ((Run @('--engine-version', '99.0.1', '--no-autostart', '--no-desktop')) -eq 0) 'update same engine: exit code 0'
 Check ((Text (Join-Path $engine 'bin\64bit\obs64.exe')) -eq 'engine-one' -and (Text (Join-Path $cfg 'user.ini')) -eq 'keep-me') 'update same engine: engine and its settings kept'
 Check ((Get-Content $prefsFile -Raw | ConvertFrom-Json).excluded[0] -eq 'Telegram') 'update: menu settings kept'
 Check ($null -eq (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Vanta') 'update: autostart removed'
 Check (-not (Test-Path (Join-Path $root 'Startup\Vanta.lnk'))) 'update: Startup folder shortcut removed'
 Check (-not (Test-Path (Join-Path $root 'Desktop\Vanta.lnk'))) 'update: desktop shortcut removed'
 Check ((Get-FileHash (Join-Path $install 'Vanta.exe')).Hash -eq $hash) 'update: same menu binary'

 # 4. Update without options keeps what the user chose before.
 Check ((Run @('--engine-version', '99.0.1')) -eq 0) 'plain update: exit code 0'
 Check ($null -eq (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Vanta') 'plain update: autostart stays off'
 Check (-not (Test-Path (Join-Path $root 'Desktop\Vanta.lnk'))) 'plain update: desktop shortcut stays off'

 # 5. New engine version: files replaced, engine settings moved over.
 Check ((Run (EngineArgs $z2 '99.0.2')) -eq 0) 'engine update: exit code 0'
 Check ((Text (Join-Path $engine 'bin\64bit\obs64.exe')) -eq 'engine-two' -and (Text $marker | ConvertFrom-Json).version -eq '99.0.2') 'engine update: new engine files'
 Check ((Text (Join-Path $cfg 'user.ini')) -eq 'keep-me') 'engine update: engine settings kept'
 Check (-not (Test-Path "$engine.new") -and -not (Test-Path "$engine.old")) 'engine update: no leftover temporary engine folders'

 # 6. Uninstall removes the menu and the engine.
 Check ((Run @('--uninstall')) -eq 0) 'uninstall: exit code 0'
 Check (-not (Test-Path $install)) 'uninstall: program folder with the engine removed'
 Check (-not (Test-Path (Join-Path $root 'StartMenu\Vanta'))) 'uninstall: Start menu folder removed'
 Check (-not (Test-Path "$reg\Uninstall\Vanta")) 'uninstall: Apps & features entry removed'

 # 7. Fresh install (nothing before) with the engine from a zip.
 Check ((Run (EngineArgs $z1 '99.0.1')) -eq 0) 'fresh install: exit code 0'
 Check ((Text (Join-Path $engine 'bin\64bit\obs64.exe')) -eq 'engine-one' -and (Text $marker | ConvertFrom-Json).version -eq '99.0.1') 'fresh install: engine unpacked next to the menu'
 Check (-not (Test-Path (Join-Path $root 'Desktop\Vanta.lnk')) -and (Test-Path (Join-Path $root 'Startup\Vanta.lnk'))) 'fresh install: autostart on, no desktop shortcut by default'
 Check ((Run @('--uninstall')) -eq 0 -and -not (Test-Path $install)) 'fresh install: uninstalled again'
} finally {
 Remove-Item $reg -Recurse -Force -ErrorAction SilentlyContinue
 Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
if ($script:failures) { Write-Host "RESULT: $script:failures failed"; exit 1 } else { Write-Host 'RESULT: all installer checks passed'; exit 0 }
