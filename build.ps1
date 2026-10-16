# Builds Replay OBS with the C# compiler that ships with Windows (.NET Framework 4.x).
# No Visual Studio or .NET SDK required.
#   .\build.ps1                 -> bin\ReplayOverlay.exe and bin\ReplayOBS-Setup.exe
#   .\build.ps1 -Test           -> plus automated tests (WebSocket client, installer in a sandbox folder)
#   .\build.ps1 -Test -Desktop  -> plus the window-visibility test (briefly opens Character Map)
# This file is intentionally ASCII-only: Windows PowerShell 5.1 misreads non-ASCII scripts without a BOM.
param([switch]$Test, [switch]$Desktop)
$ErrorActionPreference = 'Stop'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw '.NET Framework 4.x compiler (csc.exe) not found' }
Push-Location $PSScriptRoot
try {
 $bin = 'bin'; New-Item -ItemType Directory -Force $bin, "$bin\tests" | Out-Null
 $sys = 'System.dll','System.Core.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll' | ForEach-Object { '/reference:' + (Join-Path $fw $_) }
 $wpf = 'WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll' | ForEach-Object { '/reference:' + (Join-Path $fw "WPF\$_") }
 function Compile([string]$out, [string]$target, [string[]]$options, [string[]]$sources) {
  & $csc /nologo /utf8output /optimize+ /platform:x64 "/target:$target" "/out:$out" @options @sources
  if ($LASTEXITCODE) { throw "Build failed: $out" }
  Write-Host ('{0,-32} {1,10:N0} bytes' -f $out, (Get-Item $out).Length)
 }

 if (-not (Test-Path 'assets\icon.ico')) {
  Compile "$bin\tests\MakeIcon.exe" exe @("/reference:$fw\System.Drawing.dll") @('tools\MakeIcon.cs')
  & "$bin\tests\MakeIcon.exe" assets
 }

 $ov = 'src\Overlay'
 $common = "$ov\ObsConfig.cs", "$ov\ObsClient.cs"
 $vb = "/reference:$fw\Microsoft.VisualBasic.dll"
 Compile "$bin\ReplayOverlay.exe" winexe (@("/win32manifest:$ov\app.manifest", '/win32icon:assets\icon.ico', "/resource:$ov\Overlay.xaml,Overlay.xaml", $vb) + $sys + $wpf) `
  (@("$ov\AssemblyInfo.cs") + $common + @("$ov\Privacy.cs", "$ov\Hotkeys.cs", "$ov\Diagnostics.cs", "$ov\Engine.cs", "$ov\Games.cs", "$ov\Audio.cs", "$ov\Clips.cs", "$ov\Indicator.cs", "$ov\Chrome.cs", "$ov\Encoders.cs", "$ov\Storage.cs", "$ov\Viewer.cs", "$ov\Picker.cs", "$ov\Gallery.cs", "$ov\Shell.cs", "$ov\VideoSettings.cs", "$ov\SystemSettings.cs", "$ov\ReplaySR.cs", "$ov\Program.cs"))
 Compile "$bin\ReplayOBS-Setup.exe" winexe (@('/win32manifest:src\Setup\setup.manifest', '/win32icon:assets\icon.ico', '/resource:src\Setup\Setup.xaml,Setup.xaml', '/resource:assets\icon.png,icon.png', "/resource:$bin\ReplayOverlay.exe,payload.ReplayOverlay.exe") + $sys + $wpf) `
  (@('src\Setup\AssemblyInfo.cs', 'src\Setup\Setup.cs') + $common)

 Compile "$bin\AvSyncCheck.exe" exe @("/reference:$fw\System.Windows.Forms.dll", "/reference:$fw\System.Drawing.dll", "/reference:$fw\System.Core.dll") @('tools\AvSyncCheck.cs')
 Compile "$bin\PrivacyLeakTest.exe" exe @("/reference:$fw\System.Windows.Forms.dll", "/reference:$fw\System.Drawing.dll", "/reference:$fw\System.Core.dll") @('tools\PrivacyLeakTest.cs')

 if ($Test) {
  [Console]::OutputEncoding = [Text.Encoding]::UTF8
  Compile "$bin\tests\FakeObsTest.exe" exe @("/reference:$fw\System.Web.Extensions.dll", "/reference:$fw\System.Core.dll") (@('tests\FakeObsTest.cs') + $common)
  & "$bin\tests\FakeObsTest.exe"; if ($LASTEXITCODE) { throw 'FakeObsTest failed' }
  Compile "$bin\tests\DiagnosticsTest.exe" exe @() @('tests\DiagnosticsTest.cs', "$ov\Diagnostics.cs")
  & "$bin\tests\DiagnosticsTest.exe"; if ($LASTEXITCODE) { throw 'DiagnosticsTest failed' }
  Compile "$bin\tests\GamesTest.exe" exe @("/reference:$fw\System.Web.Extensions.dll", "/reference:$fw\System.Core.dll", "/reference:$fw\WPF\WindowsBase.dll") (@('tests\GamesTest.cs', "$ov\Games.cs") + $common)
  & "$bin\tests\GamesTest.exe"; if ($LASTEXITCODE) { throw 'GamesTest failed' }
  Compile "$bin\tests\AudioTest.exe" exe @("/reference:$fw\System.Web.Extensions.dll", "/reference:$fw\System.Core.dll", "/reference:$fw\WPF\WindowsBase.dll") (@('tests\AudioTest.cs', "$ov\Audio.cs", "$ov\Games.cs") + $common)
  & "$bin\tests\AudioTest.exe"; if ($LASTEXITCODE) { throw 'AudioTest failed' }
  Compile "$bin\tests\PrivacyRulesTest.exe" exe @("/reference:$fw\System.Web.Extensions.dll", "/reference:$fw\System.Core.dll", "/reference:$fw\WPF\WindowsBase.dll", "/reference:$fw\System.Windows.Forms.dll") (@('tests\PrivacyRulesTest.cs', "$ov\Privacy.cs") + $common)
  & "$bin\tests\PrivacyRulesTest.exe"; if ($LASTEXITCODE) { throw 'PrivacyRulesTest failed' }
  Compile "$bin\tests\ClipsTest.exe" exe (@($vb) + $sys + $wpf) @('tests\ClipsTest.cs', "$ov\Clips.cs", "$ov\Indicator.cs")
  & "$bin\tests\ClipsTest.exe"; if ($LASTEXITCODE) { throw 'ClipsTest failed' }
  Compile "$bin\tests\EncodersTest.exe" exe @("/reference:$fw\System.Web.Extensions.dll", "/reference:$fw\System.Core.dll") (@('tests\EncodersTest.cs', "$ov\Encoders.cs", "$ov\Storage.cs") + $common)
  & "$bin\tests\EncodersTest.exe"; if ($LASTEXITCODE) { throw 'EncodersTest failed' }
  # Recording engine: which obs-websocket requests stand behind menu actions, and how OBS events are translated.
  Copy-Item "$bin\ReplayOverlay.exe" "$bin\tests\" -Force
  Compile "$bin\tests\EngineTest.exe" exe (@("/reference:$bin\ReplayOverlay.exe") + $sys + $wpf) @('tests\EngineTest.cs')
  & "$bin\tests\EngineTest.exe"; if ($LASTEXITCODE) { throw 'EngineTest failed' }
  & powershell -NoProfile -ExecutionPolicy Bypass -File 'tests\SetupTest.ps1' -Setup "$bin\ReplayOBS-Setup.exe"; if ($LASTEXITCODE) { throw 'SetupTest failed' }
  if ($Desktop) {
   Compile "$bin\tests\PrivacyTest.exe" exe @("/reference:$fw\System.Windows.Forms.dll", "/reference:$fw\System.Drawing.dll", "/reference:$fw\WPF\WindowsBase.dll", "/reference:$fw\System.Web.Extensions.dll", "/reference:$fw\System.Core.dll") @('tests\PrivacyTest.cs', "$ov\Privacy.cs", "$ov\ObsClient.cs", "$ov\ObsConfig.cs")
   & "$bin\tests\PrivacyTest.exe"; if ($LASTEXITCODE) { throw 'PrivacyTest failed' }
   Compile "$bin\tests\PrivacyLatencyTest.exe" exe @("/reference:$fw\System.Windows.Forms.dll", "/reference:$fw\System.Drawing.dll", "/reference:$fw\WPF\WindowsBase.dll", "/reference:$fw\System.Web.Extensions.dll", "/reference:$fw\System.Core.dll") @('tests\PrivacyLatencyTest.cs', "$ov\Privacy.cs", "$ov\ObsClient.cs", "$ov\ObsConfig.cs")
   & "$bin\tests\PrivacyLatencyTest.exe"; if ($LASTEXITCODE) { throw 'PrivacyLatencyTest failed' }
   # Clips page in a real overlay window, kept off-screen and inactive; no OBS connection, no game detection.
   Copy-Item "$bin\ReplayOverlay.exe" "$bin\tests\" -Force
   Compile "$bin\tests\GalleryUiTest.exe" exe (@("/reference:$bin\ReplayOverlay.exe", $vb) + $sys + $wpf) @('tests\GalleryUiTest.cs')
   & "$bin\tests\GalleryUiTest.exe"; if ($LASTEXITCODE) { throw 'GalleryUiTest failed' }
   # Acrylic backdrop + capture exclusion: a small window near the bottom-left corner for ~1.5 s.
   Compile "$bin\tests\BackdropTest.exe" exe (@("/reference:$bin\ReplayOverlay.exe") + $sys + $wpf) @('tests\BackdropTest.cs')
   & "$bin\tests\BackdropTest.exe"; if ($LASTEXITCODE) { throw 'BackdropTest failed' }
  }
 }
} finally { Pop-Location }
