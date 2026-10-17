# Installer test in a sandbox: files go to a temp folder, registry to HKCU\Software\ReplayOBS-Test.
# Real Start menu, Run key, Desktop and scheduled tasks are not touched (--test-root).
# The recording engine comes from small fake zips (--engine-zip + test-only --engine-sha256/--engine-version): no download.
# ASCII-only on purpose (Windows PowerShell 5.1 + no BOM).
param([Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Setup = (Resolve-Path $Setup).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('replay-obs-setup-test-' + [guid]::NewGuid().ToString('N'))
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
 return @{ Zip = $zip; Sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower() }
}
function EngineArgs($z, [string]$version) { return @('--engine-zip', "`"$($z.Zip)`"", '--engine-sha256', $z.Sha, '--engine-version', $version) }
$install = Join-Path $root 'Programs\Replay OBS'
$engine = Join-Path $install 'engine'
$marker = Join-Path $engine 'replay-engine.json'
try {
 New-Item -ItemType Directory -Force $root | Out-Null
 $z1 = FakeZip 'engine1' 'engine-one'
 $z2 = FakeZip 'engine2' 'engine-two'

 # 1. Fresh install with the engine.
 Check ((Run (EngineArgs $z1 '99.0.1')) -eq 0) 'install: exit code 0'
 Check (Test-Path (Join-Path $install 'ReplayOverlay.exe')) 'install: ReplayOverlay.exe copied'
 Check (Test-Path (Join-Path $install 'uninstall.exe')) 'install: uninstall.exe copied'
 Check ((Get-Content (Join-Path $engine 'bin\64bit\obs64.exe') -Raw).Trim() -eq 'engine-one') 'install: engine unpacked next to the menu'
 Check ((Get-Content $marker -Raw | ConvertFrom-Json).version -eq '99.0.1') 'install: engine version recorded'
 Check (-not (Test-Path "$engine.new") -and -not (Test-Path "$engine.old")) 'install: no leftover temporary engine folders'
 Check (Test-Path (Join-Path $root 'StartMenu\Replay OBS\Replay Overlay.lnk')) 'install: Start menu shortcut'
 Check (-not (Test-Path (Join-Path $root 'Desktop\Replay Overlay.lnk'))) 'install: no desktop shortcut by default'
 $run = (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Replay Overlay'
 Check ($run -like '*ReplayOverlay.exe" --hidden --from=run') 'install: autostart entry with --hidden'
 Check (Test-Path (Join-Path $root 'Startup\Replay Overlay.lnk')) 'install: Startup folder shortcut (second autostart path)'
 $un = Get-ItemProperty "$reg\Uninstall\ReplayOBS" -ErrorAction SilentlyContinue
 Check ($un.DisplayName -eq 'Replay OBS' -and $un.DisplayVersion -eq (Get-Item $Setup).VersionInfo.ProductVersion -and $un.UninstallString -like '*uninstall.exe" --uninstall') ('install: Apps & features entry, version ' + $un.DisplayVersion)
 Check ($un.EstimatedSize -gt 1000) ('install: size includes menu and engine (' + $un.EstimatedSize + ' KB)')
 $hash = (Get-FileHash (Join-Path $install 'ReplayOverlay.exe')).Hash
 # Engine settings and menu settings that must survive updates.
 $cfg = Join-Path $engine 'config\obs-studio'; New-Item -ItemType Directory -Force $cfg | Out-Null
 'keep-me' | Set-Content -Encoding ASCII (Join-Path $cfg 'user.ini')
 $prefsFile = Join-Path $install 'overlay-settings.json'
 '{"excluded":["Telegram"]}' | Set-Content -Encoding ASCII $prefsFile

 # 2. Damaged or substituted archive: refused before anything changes.
 $bad = @('--engine-zip', "`"$($z2.Zip)`"", '--engine-sha256', $z1.Sha, '--engine-version', '99.0.2')
 Check ((Run $bad) -ne 0) 'bad checksum: installer fails'
 Check ((Get-Content $marker -Raw | ConvertFrom-Json).version -eq '99.0.1' -and (Get-Content (Join-Path $engine 'bin\64bit\obs64.exe') -Raw).Trim() -eq 'engine-one') 'bad checksum: installed engine untouched'
 Check ((Get-Content (Join-Path $cfg 'user.ini') -Raw).Trim() -eq 'keep-me') 'bad checksum: engine settings untouched'

 # 3. Update with the same engine version: nothing to download, engine and its settings kept; other options applied.
 Check ((Run @('--engine-version', '99.0.1', '--no-autostart', '--desktop')) -eq 0) 'update same engine: exit code 0'
 Check ((Get-Content (Join-Path $engine 'bin\64bit\obs64.exe') -Raw).Trim() -eq 'engine-one' -and (Get-Content (Join-Path $cfg 'user.ini') -Raw).Trim() -eq 'keep-me') 'update same engine: engine and its settings kept'
 Check ((Get-Content $prefsFile -Raw | ConvertFrom-Json).excluded[0] -eq 'Telegram') 'update: menu settings kept'
 Check ($null -eq (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Replay Overlay') 'update: autostart removed'
 Check (-not (Test-Path (Join-Path $root 'Startup\Replay Overlay.lnk'))) 'update: Startup folder shortcut removed'
 Check (Test-Path (Join-Path $root 'Desktop\Replay Overlay.lnk')) 'update: desktop shortcut created'
 Check ((Get-FileHash (Join-Path $install 'ReplayOverlay.exe')).Hash -eq $hash) 'update: same overlay binary'

 # 4. Update without options keeps what the user chose before.
 Check ((Run @('--engine-version', '99.0.1')) -eq 0) 'plain update: exit code 0'
 Check ($null -eq (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).'Replay Overlay') 'plain update: autostart stays off'
 Check (Test-Path (Join-Path $root 'Desktop\Replay Overlay.lnk')) 'plain update: desktop shortcut kept'

 # 5. New engine version: files replaced, engine settings moved over.
 Check ((Run (EngineArgs $z2 '99.0.2')) -eq 0) 'engine update: exit code 0'
 Check ((Get-Content (Join-Path $engine 'bin\64bit\obs64.exe') -Raw).Trim() -eq 'engine-two' -and (Get-Content $marker -Raw | ConvertFrom-Json).version -eq '99.0.2') 'engine update: new engine files'
 Check ((Get-Content (Join-Path $cfg 'user.ini') -Raw).Trim() -eq 'keep-me') 'engine update: engine settings kept'
 Check (-not (Test-Path "$engine.new") -and -not (Test-Path "$engine.old")) 'engine update: no leftover temporary engine folders'

 # 6. Uninstall removes the menu and the engine.
 Check ((Run @('--uninstall')) -eq 0) 'uninstall: exit code 0'
 Check (-not (Test-Path $install)) 'uninstall: program folder with the engine removed'
 Check (-not (Test-Path (Join-Path $root 'StartMenu\Replay OBS'))) 'uninstall: Start menu folder removed'
 Check (-not (Test-Path (Join-Path $root 'Desktop\Replay Overlay.lnk'))) 'uninstall: desktop shortcut removed'
 Check (-not (Test-Path "$reg\Uninstall\ReplayOBS")) 'uninstall: Apps & features entry removed'
} finally {
 Remove-Item $reg -Recurse -Force -ErrorAction SilentlyContinue
 Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
if ($script:failures) { Write-Host "RESULT: $script:failures failed"; exit 1 } else { Write-Host 'RESULT: all installer checks passed'; exit 0 }
