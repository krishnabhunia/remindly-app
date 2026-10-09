$setup = (Get-Item "out/installer/Remindly_$env:PACKAGE_VERSION.exe").FullName
$dir = "$env:RUNNER_TEMP\RemindlyInstalled"
$data = "$env:RUNNER_TEMP\installer-data"
# Isolate installer tests from live GitHub updates. Setup relaunches with the default
# data directory, so seed both disposable runner profiles before starting either copy.
foreach ($profile in @($data, (Join-Path $env:LOCALAPPDATA 'Remindly'))) {
  New-Item -ItemType Directory -Path $profile -Force | Out-Null
  '{"settings":{"updateAutoCheck":false}}' | Set-Content (Join-Path $profile 'remindly-data.json') -Encoding utf8
}
function Running { @(Get-Process -Name Remindly -ErrorAction SilentlyContinue).Count -gt 0 }
# Waits for THIS process only (Start-Process -Wait would also wait for the Remindly that Setup restarts).
function RunAndWait([string]$file, [string[]]$argv, [int]$seconds = 300) {
  $proc = Start-Process $file -ArgumentList $argv -WindowStyle Hidden -PassThru
  if (-not $proc.WaitForExit($seconds * 1000)) { $proc.Kill(); throw "$([IO.Path]::GetFileName($file)) timed out after $seconds s" }
  return $proc.ExitCode
}

# 1. Fresh per-user install
$code = RunAndWait $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', '/NORESTART', "/DIR=$dir", '/TASKS=', "/LOG=$env:RUNNER_TEMP\setup1.log")
if ($code -ne 0) { Get-Content "$env:RUNNER_TEMP\setup1.log" -Tail 60; throw "install failed ($code)" }
if (-not (Test-Path "$dir\Remindly.exe")) { throw "Remindly.exe not installed" }
$marker = (Get-ItemProperty 'HKCU:\Software\Remindly').InstallDir
if ($marker.TrimEnd('\') -ne $dir.TrimEnd('\')) { throw "InstallDir marker '$marker' <> '$dir'" }
Write-Host "installed to $dir"

# 2. Start it (hidden in the tray) and run Setup again: it must detect the running copy,
#    close it, update in the background and start it again.
Start-Process "$dir\Remindly.exe" -ArgumentList @('--tray', '--data-dir', $data) -WindowStyle Hidden | Out-Null
$t = 0; while (-not (Running) -and $t -lt 60) { Start-Sleep 1; $t++ }
if (-not (Running)) { throw "installed Remindly did not start" }
Start-Sleep 5
$before = (Get-Process -Name Remindly | Select-Object -First 1).Id
$code = RunAndWait $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', '/NORESTART', "/DIR=$dir", '/TASKS=', '/REMINDLYUPDATE=1', '/RELAUNCH=tray', "/LOG=$env:RUNNER_TEMP\setup2.log")
Get-Content "$env:RUNNER_TEMP\setup2.log" | Select-String -Pattern 'Remindly' | ForEach-Object { $_.Line }
if ($code -ne 0) { throw "update-while-running failed ($code)" }
if (-not (Select-String -Path "$env:RUNNER_TEMP\setup2.log" -Pattern 'previous Remindly is running' -Quiet)) { throw "Setup did not detect the running copy" }
$t = 0; while (-not (Running) -and $t -lt 60) { Start-Sleep 1; $t++ }
if (-not (Running)) { throw "Setup did not restart Remindly after the update" }
$after = (Get-Process -Name Remindly | Select-Object -First 1).Id
if ($after -eq $before) { throw "Remindly was not restarted (same process $before)" }
Write-Host "update while running: closed pid $before, restarted as pid $after"

# 3. Uninstall (closes the running copy first)
$unins = Get-ChildItem $dir -Filter 'unins*.exe' | Select-Object -First 1
# The uninstaller copies itself to %TEMP% and returns at once; wait until the files are gone.
$code = RunAndWait $unins.FullName @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
$t = 0; while ((Test-Path "$dir\Remindly.exe") -and $t -lt 60) { Start-Sleep 1; $t++ }
Start-Sleep 3
if (Test-Path "$dir\Remindly.exe") { throw "uninstall left Remindly.exe behind" }
if (Running) { throw "uninstall left Remindly running" }
Write-Host "uninstall OK"
