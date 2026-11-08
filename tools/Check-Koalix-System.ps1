<#
  Check-Koalix-System.ps1  (KOALIX 0.3.6 companion)

  WHY: On the machine this was written for, the KOALIX log showed THREE other
  network tools resident at the same time (Lantern service, Cloudflare WARP,
  Windscribe) plus ISP DNS poisoning. Any sing-box/Xray TUN app fails the same
  way on such a system, while WireSock (different capture path) keeps working.
  This script first only REPORTS. With -Fix it applies the reversible cleanup:
  stops the foreign VPN/proxy services for this session, clears a leftover
  system proxy (Chrome uses it!), and flushes the DNS cache.
  Nothing is uninstalled or disabled permanently: a reboot (or starting the
  apps again) brings everything back.

  Run in an ELEVATED PowerShell:
      powershell -ExecutionPolicy Bypass -File .\Check-Koalix-System.ps1
      powershell -ExecutionPolicy Bypass -File .\Check-Koalix-System.ps1 -Fix
#>
param([switch]$Fix)

$ErrorActionPreference = 'SilentlyContinue'
function Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "WARNING: not running as Administrator - report is partial and -Fix will not work." -ForegroundColor Yellow }

# ---------------------------------------------------------------- processes / services
Section "Foreign VPN / proxy tools (must not run next to a TUN tunnel)"
$foreign = @(
    @{ Name = 'Lantern';        Procs = @('LanternSvc','lantern');        Svcs = @('Lantern') },
    @{ Name = 'Cloudflare WARP';Procs = @('warp-svc','warp-dotnet');      Svcs = @('Cloudflare WARP') },
    @{ Name = 'Windscribe';     Procs = @('Windscribe','WindscribeService'); Svcs = @('WindscribeService') },
    @{ Name = 'ProtonVPN';      Procs = @('ProtonVPN','ProtonVPNService');   Svcs = @('ProtonVPN Service') },
    @{ Name = 'Psiphon';        Procs = @('psiphon3','psiphon-tunnel-core'); Svcs = @() },
    @{ Name = 'v2rayN / Hiddify / Nekoray'; Procs = @('v2rayN','Hiddify','nekoray'); Svcs = @() }
)
$foundForeign = @()
foreach ($f in $foreign) {
    $p = Get-Process -Name $f.Procs -ErrorAction SilentlyContinue
    $s = Get-Service -Name $f.Svcs -ErrorAction SilentlyContinue | Where-Object Status -eq 'Running'
    if ($p -or $s) {
        Write-Host ("FOUND RUNNING: {0}  (processes: {1}, services: {2})" -f $f.Name, ($p.Name -join ','), ($s.Name -join ',')) -ForegroundColor Red
        $foundForeign += $f
    } else {
        Write-Host ("ok: {0} not running" -f $f.Name) -ForegroundColor DarkGray
    }
}
$orphans = Get-Process -Name 'sing-box','xray' -ErrorAction SilentlyContinue
if ($orphans) { Write-Host ("Orphan tunnel cores from other apps: " + (($orphans | ForEach-Object { "$($_.Name) PID $($_.Id)" }) -join ', ')) -ForegroundColor Red }

# ---------------------------------------------------------------- system proxy (Chrome uses this!)
Section "Windows system proxy (HKCU) - Chrome/Edge obey it, Firefox usually not"
$ie = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
Write-Host ("ProxyEnable = {0}   ProxyServer = {1}" -f $ie.ProxyEnable, $ie.ProxyServer)
if ($ie.ProxyEnable -eq 1) {
    Write-Host "A system proxy is SET. If it points to a dead/other app (127.0.0.1:xxxx), Chrome loads NOTHING even with a perfect TUN tunnel." -ForegroundColor Red
}

# ---------------------------------------------------------------- DNS poisoning test
Section "DNS poisoning test (blocked domains must NOT resolve to 10.10.34.x)"
foreach ($hostName in 'www.youtube.com','www.google.com') {
    $a = Resolve-DnsName $hostName -Type A -DnsOnly -ErrorAction SilentlyContinue | Where-Object IPAddress | Select-Object -First 1 -ExpandProperty IPAddress
    if ($a -like '10.10.34.*') {
        Write-Host "$hostName -> $a   POISONED by ISP DNS (this is expected on the plain local resolver; KOALIX 0.3.6 no longer uses it for tunneled apps)" -ForegroundColor Yellow
    } else {
        Write-Host "$hostName -> $a"
    }
}

# ---------------------------------------------------------------- adapters / routes / DNS servers
Section "Adapters, their DNS servers, default routes"
Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object {
    $dns = (Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4).ServerAddresses -join ', '
    Write-Host ("{0} [{1}]  DNS: {2}" -f $_.Name, $_.InterfaceDescription, $dns)
}
$defaults = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
Write-Host ("IPv4 default routes: {0} (more than 2 while no tunnel runs = another VPN owns one)" -f @($defaults).Count)

# ---------------------------------------------------------------- Wintun
Section "Wintun driver"
$wintun = Get-CimInstance Win32_SystemDriver -Filter "Name='wintun'" -ErrorAction SilentlyContinue
if ($wintun) { Write-Host ("wintun service: {0} / start mode {1}" -f $wintun.State, $wintun.StartMode) }
else { Write-Host "No standalone wintun service (normal: sing-box loads its bundled wintun.dll per app folder.)" }

# ---------------------------------------------------------------- fix
if ($Fix) {
    Section "FIX (reversible: stop foreign services, clear system proxy, flush DNS)"
    foreach ($f in $foundForeign) {
        foreach ($svc in $f.Svcs) { Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue; Write-Host "stopped service $svc" }
        foreach ($proc in $f.Procs) { Stop-Process -Name $proc -Force -ErrorAction SilentlyContinue }
        Write-Host ("stopped: {0}" -f $f.Name)
    }
    if ($orphans) { $orphans | Stop-Process -Force -ErrorAction SilentlyContinue; Write-Host "stopped orphan sing-box/xray cores (NOT KOALIX itself)" }
    if ($ie.ProxyEnable -eq 1) {
        Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name ProxyEnable -Value 0
        Write-Host "system proxy disabled (was: $($ie.ProxyServer)). Re-enable in Windows Settings > Network > Proxy if you really need it."
    }
    Clear-DnsClientCache; ipconfig /flushdns | Out-Null
    Write-Host "DNS cache flushed. Now fully exit KOALIX from the tray, start it again, connect, and test."
    Write-Host "To undo: reboot, or start Lantern/WARP/Windscribe again."
} else {
    Write-Host "`nReport only. To apply the reversible cleanup, re-run with -Fix." -ForegroundColor Cyan
}
