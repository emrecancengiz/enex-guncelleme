# Enex POS baslatici (gizli pencere): programi acar, kapaninca "yedek alinsin mi?" diye sorar
$base = "C:\dev\enex_pos_web"
# Tek ornek: baslatici zaten calisiyorsa (cift tiklama) calisan kasayi kapatma; mevcut pencereyi one getir
$log = "$base\baslat.log"
function Kayit($m) { try { Add-Content -Path $log -Value ("{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $PID, $m) } catch {} }
if (Test-Path $log) { if ((Get-Item $log).Length -gt 200KB) { Remove-Item $log -Force } }
Kayit "Kisayol tiklandi"
#  - Program zaten aciksa: pencereyi one getir ve cik.
#  - Acik degilse onceki baslatici kapanisini bitiriyordur: en cok 20 sn bekle, sonra programi ac (eskiden sessizce cikiyordu).
$mtx = New-Object System.Threading.Mutex($false, "Local\EnexPOSBaslatici")
function Kilit-Al($ms) { try { return $mtx.WaitOne($ms) } catch [System.Threading.AbandonedMutexException] { return $true } }
if (-not (Kilit-Al 0)) {
  try { Add-Type -AssemblyName Microsoft.VisualBasic } catch {}
  $al = $false
  for ($i = 0; $i -lt 40 -and -not $al; $i++) {
    try { [Microsoft.VisualBasic.Interaction]::AppActivate("Enex POS"); Kayit "Program zaten acik, one getirildi"; exit 0 } catch {}
    $al = Kilit-Al 500
  }
  if (-not $al) { Kayit "Baslatici mesgul (20 sn), cikildi"; exit 0 }
  Kayit "Onceki baslatici bitti, devam"
}
$adaylar = @(
  "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
  "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
  "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
  "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
)
$edge = $adaylar | Where-Object { Test-Path $_ } | Select-Object -First 1
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
try { Add-Type -Namespace EnexW -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
[DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
'@
  [EnexW.Win]::SetProcessDPIAware() | Out-Null
} catch {}
if (-not $edge) {
  [System.Windows.Forms.MessageBox]::Show("Microsoft Edge veya Chrome bulunamadi.", "Enex POS", "OK", "Error") | Out-Null
  exit 1
}
# Acilis penceresi: Edge penceresi gelene kadar "baslatiliyor" yazar (ekran bos kalmasin)
$script:splash = New-Object System.Windows.Forms.Form
$splash.FormBorderStyle = "None"; $splash.StartPosition = "CenterScreen"; $splash.Size = New-Object System.Drawing.Size(380, 130)
$splash.BackColor = [System.Drawing.Color]::FromArgb(233, 237, 242); $splash.TopMost = $true; $splash.ShowInTaskbar = $true; $splash.Text = "Enex POS"
try { $splash.Icon = New-Object System.Drawing.Icon("$base\enex.ico") } catch {}
$lb1 = New-Object System.Windows.Forms.Label; $lb1.Text = "enex POS"; $lb1.Font = New-Object System.Drawing.Font("Segoe UI", 20, [System.Drawing.FontStyle]::Bold)
$lb1.ForeColor = [System.Drawing.Color]::FromArgb(28, 36, 48); $lb1.AutoSize = $false; $lb1.TextAlign = "MiddleCenter"; $lb1.SetBounds(0, 18, 380, 44)
$lb2 = New-Object System.Windows.Forms.Label; $lb2.Text = "Program baslatiliyor, lutfen bekleyin..."; $lb2.Font = New-Object System.Drawing.Font("Segoe UI", 11)
$lb2.ForeColor = [System.Drawing.Color]::FromArgb(102, 113, 127); $lb2.AutoSize = $false; $lb2.TextAlign = "MiddleCenter"; $lb2.SetBounds(0, 70, 380, 30)
$splash.Controls.Add($lb1); $splash.Controls.Add($lb2)
function Splash-Kapat { if ($script:splash) { try { $script:splash.Close(); $script:splash.Dispose() } catch {}; $script:splash = $null } }
$splash.Show(); [System.Windows.Forms.Application]::DoEvents()

if (-not (Test-Path "$base\profil")) { New-Item -ItemType Directory -Path "$base\profil" | Out-Null }
$ortak = "--user-data-dir=`"$base\profil`" --no-first-run --no-default-browser-check --disable-background-mode --disable-features=msEdgeSidebarV2,msHubApps,msStartupBoost"

Kayit "Baslatici calisti. Tarayici: $edge"
# Otomatik guncelleme: yeni ve uretici imzali surum varsa sorar, indirir, yedekleyip yukler (internet yoksa sessizce gecer)
if (Test-Path "$base\enex-guncelleme.ps1") {
  try {
    . "$base\enex-guncelleme.ps1"
    $gsonuc = Enex-Guncelle -Base $base -Kayit { param($t) Kayit $t } -Durum { param($t) if ($script:splash) { $lb2.Text = $t; [System.Windows.Forms.Application]::DoEvents() } } -Sor {
      param($yeni, $notlar, $eski)
      $metin = "Enex POS'un yeni surumu hazir.`n`nYuklu surum: $eski`nYeni surum: $yeni"
      if ($notlar) { $metin += "`n`nYenilikler:`n" + $(if ($notlar.Length -gt 900) { $notlar.Substring(0, 900) + "..." } else { $notlar }) }
      $metin += "`n`nSimdi guncellensin mi? (Verileriniz, ayarlariniz ve lisansiniz aynen kalir.)"
      if ($script:splash) { $script:splash.TopMost = $false }
      $c = [System.Windows.Forms.MessageBox]::Show($metin, "Enex POS guncelleme", "YesNo", "Question")
      if ($script:splash) { $script:splash.TopMost = $true; [System.Windows.Forms.Application]::DoEvents() }
      return ($c -eq "Yes")
    }
    Kayit "Guncelleme sonucu: $gsonuc"
  } catch { Kayit "Guncelleme atlandi: $($_.Exception.Message)" }
  if ($script:splash) { $lb2.Text = "Program baslatiliyor, lutfen bekleyin..."; [System.Windows.Forms.Application]::DoEvents() }
}
# Enex Hizmet (OKC koprusu + ag ortak verisi) arka planda calissin; zaten calisiyorsa kendisi hemen kapanir
if (Test-Path "$base\enex-hizmet.ps1") {
  try { Start-Process -FilePath "powershell.exe" -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$base\enex-hizmet.ps1`"" } catch { Kayit "Enex Hizmet baslatilamadi: $($_.Exception.Message)" }
}

# Bu profili kullanan tarayici surecleri (pencere kapansa da arka planda kalabilir)
function Enex-Surecler { Get-CimInstance Win32_Process -Filter "Name='msedge.exe' OR Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*enex_pos_web\profil*" } }
# Nazik kapanis: once pencereye kapan de (veriler diske yazilsin), 5 sn sonra kalan varsa zorla
function Enex-Kapat {
  Enex-Surecler | ForEach-Object { try { (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).CloseMainWindow() | Out-Null } catch {} }
  $t = 0; while ((@(Enex-Surecler).Count -gt 0) -and $t -lt 10) { Start-Sleep -Milliseconds 500; $t++ }
  Enex-Surecler | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
# Bu profile ait GORUNUR pencere sayisi (surec arka planda kalsa bile pencere kapaninca 0 olur)
function Enex-PencereSayisi {
  $ids = @(Enex-Surecler | ForEach-Object { $_.ProcessId })
  if ($ids.Count -eq 0) { return 0 }
  return @(Get-Process -Id $ids -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }).Count
}
# Program "ENEX-KUCULT" basligi koyunca pencereyi simge durumuna kucult (tam ekranda Windows dugmeleri gizli oldugu icin)
function Enex-Kucult {
  $ids = @(Enex-Surecler | ForEach-Object { $_.ProcessId })
  Get-Process -Id $ids -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "ENEX-KUCULT*" } | ForEach-Object { try { [EnexW.Win]::ShowWindowAsync($_.MainWindowHandle, 6) | Out-Null } catch {} }
}
# Program "ENEX-CERCEVE:<px>" basligi koyunca: pencereyi ekranin calisma alanina (gorev cubugu haric) yerlestir,
# ustteki Windows cubugu (<px> yuksekliginde) ekranin disinda kalsin -> kapat (X) dugmesi gorunmez, gorev cubugu acik kalir
function Enex-Yerlestir {
  $ids = @(Enex-Surecler | ForEach-Object { $_.ProcessId })
  Get-Process -Id $ids -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "ENEX-CERCEVE:*" } | ForEach-Object {
    try {
      $cap = [int]($_.MainWindowTitle -replace '^ENEX-CERCEVE:', ''); if ($cap -lt 10 -or $cap -gt 150) { return }
      $h = $_.MainWindowHandle
      $mi = New-Object EnexW.Win+MONITORINFO; $mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($mi)
      [EnexW.Win]::ShowWindow($h, 9) | Out-Null   # buyutulmusse normale al
      if ([EnexW.Win]::GetMonitorInfo([EnexW.Win]::MonitorFromWindow($h, 2), [ref]$mi)) {
        $w = $mi.rcWork
        [EnexW.Win]::SetWindowPos($h, [IntPtr]::Zero, $w.Left, $w.Top - $cap, $w.Right - $w.Left, ($w.Bottom - $w.Top) + $cap, 0x0004) | Out-Null
        Kayit "Kasa penceresi yerlestirildi (cubuk $cap px)"
      }
    } catch { Kayit "Yerlestirme hatasi: $($_.Exception.Message)" }
  }
}
function Enex-Basliklar {
  $ids = @(Enex-Surecler | ForEach-Object { $_.ProcessId })
  if ($ids.Count -eq 0) { return @() }
  return @(Get-Process -Id $ids -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { $_.MainWindowTitle })
}
# Pencere acilana kadar bekle; sonra ya program "ENEX-KAPAT" basligiyla kapanmak istedi (duzgun kapanis) ya da pencere X ile kapatildi
function Enex-Bekle {
  $t = 0
  while ((Enex-PencereSayisi) -eq 0 -and $t -lt 120) { if ($script:splash) { [System.Windows.Forms.Application]::DoEvents() }; Start-Sleep -Milliseconds 500; $t++ }
  Splash-Kapat
  if ((Enex-PencereSayisi) -eq 0) { Kayit "Pencere 60 sn icinde acilmadi"; return "yok" }
  Kayit "Pencere acildi (bekleme: $t sn)"
  while ($true) {
    $b = @(Enex-Basliklar)
    if ($b.Count -eq 0) { Kayit "Pencere X ile kapatildi"; return "x" }
    if (@($b | Where-Object { $_ -like "ENEX-KUCULT*" }).Count -gt 0) { Enex-Kucult }
    if (@($b | Where-Object { $_ -like "ENEX-CERCEVE:*" }).Count -gt 0) { Enex-Yerlestir; Start-Sleep -Milliseconds 1500 }
    if (@($b | Where-Object { $_ -like "ENEX-KAPAT*" }).Count -gt 0) {
      Kayit "Program duzgun kapanis istedi"
      Enex-Kapat
      return "kapat"
    }
    Start-Sleep -Milliseconds 700
  }
}
[System.Windows.Forms.Application]::DoEvents()
# Onceki acilistan arka planda kalmis surec varsa kapat
Enex-Surecler | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

# 1) Programi ac ve kapanana kadar bekle
Start-Process -FilePath $edge -ArgumentList "--app=`"file:///C:/dev/enex_pos_web/index.html`" --start-maximized $ortak"
$sonuc = Enex-Bekle

# 2) X ile kapatildiysa programi yeniden ac; yedek penceresi (Kapat dugmesindekiyle ayni) kendiliginden gelir.
#    Program "ENEX-KAPAT" ile kapanana kadar tekrar eder; Windows penceresi gosterilmez.
$deneme = 0
while ($sonuc -eq "x" -and $deneme -lt 3) {
  $deneme++
  Kayit "Yedek penceresiyle yeniden aciliyor ($deneme)"
  Start-Process -FilePath $edge -ArgumentList "--app=`"file:///C:/dev/enex_pos_web/index.html?yedek=1`" --start-maximized $ortak"
  $sonuc = Enex-Bekle
}
Start-Sleep -Seconds 3
Enex-Kapat
Kayit "Bitti"
$mtx.ReleaseMutex()
