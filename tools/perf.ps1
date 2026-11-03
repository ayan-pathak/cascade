# Measures how much CPU, GPU and memory Cascade uses (Cascade.exe plus its WebView2 processes).
#   .\tools\perf.ps1 -Scenario apps     # whatever is on screen now, usually apps in front
#   .\tools\perf.ps1 -Scenario home     # shows the home screen (Win+D), measures, then restores
#   .\tools\perf.ps1 -Scenario water    # home screen, clicking the water twice a second
param([ValidateSet("apps", "home", "water", "fog")][string]$Scenario = "apps", [int]$Seconds = 12)

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PerfInput {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  public static void WinD() { keybd_event(0x5B,0,0,UIntPtr.Zero); keybd_event(0x44,0,0,UIntPtr.Zero); keybd_event(0x44,0,2,UIntPtr.Zero); keybd_event(0x5B,0,2,UIntPtr.Zero); }
  public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
}
'@
[PerfInput]::SetProcessDPIAware() | Out-Null

$host_ = Get-Process Cascade -ErrorAction Stop | Select-Object -First 1
$all = Get-CimInstance Win32_Process
$ids = @($host_.Id); $grew = $true
while ($grew) { $grew = $false; foreach ($p in $all) { if ($ids -contains $p.ParentProcessId -and -not ($ids -contains $p.ProcessId)) { $ids += $p.ProcessId; $grew = $true } } }
$procs = { $ids | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue } }

if ($Scenario -ne "apps") { [PerfInput]::WinD(); Start-Sleep -Seconds 2 }

$cores = [Environment]::ProcessorCount
$t0 = @{}; & $procs | ForEach-Object { $t0[$_.Id] = $_.TotalProcessorTime.TotalSeconds }
$sw = [Diagnostics.Stopwatch]::StartNew()
$gpu = @()
$job = $null
if ($Scenario -eq "water") { $job = { $t = 0; while ($sw.Elapsed.TotalSeconds -lt $Seconds) { [PerfInput]::Click(860 + ($t % 5) * 60, 640 + ($t % 3) * 50); $t++; Start-Sleep -Milliseconds 500 } } }
if ($Scenario -eq "fog") { $job = { $a = 0; while ($sw.Elapsed.TotalSeconds -lt $Seconds) { [PerfInput]::SetCursorPos([int](960 + 300 * [Math]::Cos($a)), [int](600 + 150 * [Math]::Sin($a))) | Out-Null; $a += .15; Start-Sleep -Milliseconds 16 } } }

# GPU: the 3D engine use of our processes, sampled every second while the scenario runs
$gpuJob = Start-Job -ArgumentList ($ids -join ","), $Seconds -ScriptBlock { param($idList, $n)
  $keys = $idList.Split(",") | ForEach-Object { "pid_$($_)_" }
  for ($i = 0; $i -lt $n; $i++) {
    $c = (Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples
    ($c | Where-Object { $nm = $_.InstanceName; @($keys | Where-Object { $nm -like "$_*" }).Count -gt 0 } | Measure-Object CookedValue -Sum).Sum
  } }
if ($job) { & $job } else { Start-Sleep -Seconds $Seconds }
$elapsed = $sw.Elapsed.TotalSeconds
$t1 = @{}; & $procs | ForEach-Object { $t1[$_.Id] = $_.TotalProcessorTime.TotalSeconds }
$used = 0; $per = foreach ($k in $t1.Keys) { $d = $t1[$k] - $(if ($t0.ContainsKey($k)) { $t0[$k] } else { 0 }); $used += $d
  $cl = ($all | Where-Object ProcessId -eq $k).CommandLine; $kind = if ($k -eq $host_.Id) { "host" } elseif ($cl -match "--type=([\w-]+)") { $matches[1] } else { "browser" }
  [pscustomobject]@{ kind = $kind; pct = [Math]::Round($d / $elapsed * 100, 1) } }
$gpuSamples = Receive-Job $gpuJob -Wait -AutoRemoveJob
$gpuPct = if ($gpuSamples) { [Math]::Round(($gpuSamples | Measure-Object -Average).Average, 1) } else { "n/a" }
$mem = [Math]::Round((& $procs | Measure-Object WorkingSet64 -Sum).Sum / 1MB)
if ($Scenario -ne "apps") { [PerfInput]::WinD() }

$cpuPct = [Math]::Round($used / $elapsed / $cores * 100, 2)
$oneCore = [Math]::Round($used / $elapsed * 100, 1)
"{0,-6} CPU {1,5}% of machine ({2}% of one core)  GPU 3D {3}%  memory {4} MB  ({5} processes)" -f $Scenario, $cpuPct, $oneCore, $gpuPct, $mem, $ids.Count
"         busiest: " + (($per | Sort-Object pct -Descending | Select-Object -First 4 | ForEach-Object { "$($_.kind) $($_.pct)%" }) -join ", ")
