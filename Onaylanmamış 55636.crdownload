# Enex POS otomatik guncelleme (baslatici programi acmadan once calistirir)
# Internetteki surum.json dosyasini okur; imza ureticinin anahtariyla dogruysa ve surum yeniyse
# kullaniciya sorar, dosyalari indirir, her dosyanin SHA-256 ozetini kontrol eder, eskileri yedekleyip degistirir.
# Imzasiz / degistirilmis / eski surum paketleri asla yuklenmez. Internet yoksa sessizce gecer.

$script:EnexPubX = "0CeZei5ETu-ug5Az01LlzB-bH2NH-r0Ft4zDmUy8tZQ"   # programdaki LIC_PUB ile ayni (uretici imza anahtari)
$script:EnexPubY = "VHlbdEizmTtGkEejWgOQf0uyD-aSvYYcHUioz55l2Nc"
$script:EnexVarsayilanUrl = "https://raw.githubusercontent.com/emrecancengiz/enex-guncelleme/main/surum.json"   # ornek: https://raw.githubusercontent.com/<kullanici>/enex-guncelleme/main/surum.json
# Guncellemeyle degistirilebilecek dosyalar (baska dosya adi gelirse paket reddedilir)
$script:EnexIzinli = @("index.html", "enex-desktop.js", "enex-baslat.ps1", "enex-hizmet.ps1", "EnexHizmet.cs", "enex-guncelleme.ps1", "enex.ico", "enex-32.png", "enex-192.png")

function Enex-B64u([string]$s) {
  $t = $s.Replace('-', '+').Replace('_', '/'); switch ($t.Length % 4) { 2 { $t += '==' } 3 { $t += '=' } }
  return [Convert]::FromBase64String($t)
}
function Enex-Sha256Hex([byte[]]$b) {
  $h = [System.Security.Cryptography.SHA256]::Create(); try { return (($h.ComputeHash($b) | ForEach-Object { $_.ToString("x2") }) -join "") } finally { $h.Dispose() }
}
# Imzalanan metin (Lisans Yoneticisi ayni sekilde uretir)
function Enex-ImzaMetni($m) {
  $L = [string[]]@($m.files | ForEach-Object { "{0}={1}" -f [string]$_.n, ([string]$_.sha256).ToLower() }); [Array]::Sort($L, [StringComparer]::Ordinal)
  $dos = $L -join ";"
  $not = Enex-Sha256Hex ([System.Text.Encoding]::UTF8.GetBytes([string]$m.notes))
  return "ENEX-UPDATE|{0}|{1}|{2}|{3}" -f [string]$m.ver, [string]$m.date, $dos, $not
}
function Enex-ImzaDogru($m, [string]$px, [string]$py) {
  try {
    $p = New-Object System.Security.Cryptography.ECParameters
    $p.Curve = [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256
    $q = New-Object System.Security.Cryptography.ECPoint
    $q.X = Enex-B64u $px; $q.Y = Enex-B64u $py; $p.Q = $q
    $ec = [System.Security.Cryptography.ECDsa]::Create($p)
    $veri = [System.Text.Encoding]::UTF8.GetBytes((Enex-ImzaMetni $m))
    $ok = $ec.VerifyData($veri, (Enex-B64u ([string]$m.sig)), [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $ec.Dispose(); return [bool]$ok
  } catch { return $false }
}
function Enex-YerelSurum([string]$base) {
  try { $t = [IO.File]::ReadAllText((Join-Path $base "index.html"), [Text.Encoding]::UTF8); if ($t -match 'const ENEX_VER = "(\d+\.\d+\.\d+)"') { return [version]$Matches[1] } } catch {}
  return [version]"0.0.0"
}
function Enex-Indir([string]$url, [string]$hedef, [int]$sn) {
  Invoke-WebRequest -Uri $url -OutFile $hedef -UseBasicParsing -TimeoutSec $sn -Headers @{"Cache-Control" = "no-cache"} -ErrorAction Stop | Out-Null
}

# Sonuc: kapali | internet-yok | imza-hatali | guncel | ertelendi | indirme-hatasi | yuklendi | hata
function Enex-Guncelle {
  param([string]$Base, [scriptblock]$Sor, [scriptblock]$Durum, [scriptblock]$Kayit, [string]$Url = "", [string]$PubX = $script:EnexPubX, [string]$PubY = $script:EnexPubY)
  $ProgressPreference = "SilentlyContinue"   # Windows PowerShell 5.1'de ilerleme cubugu indirmeyi cok yavaslatir
  $K = { param($t) if ($Kayit) { & $Kayit "Guncelleme: $t" } }
  $D = { param($t) if ($Durum) { try { & $Durum $t } catch {} } }
  try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
  if (-not $Url) { try { $gj = Join-Path $Base "guncelleme.json"; if (Test-Path $gj) { $Url = [string]((Get-Content $gj -Raw -Encoding UTF8 | ConvertFrom-Json).url) } } catch {} }
  if (-not $Url) { $Url = $script:EnexVarsayilanUrl }
  if (-not $Url) { return "kapali" }
  $tmp = Join-Path $Base "guncelleme-tmp"
  try {
    & $D "Guncellemeler denetleniyor..."
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    $mf = Join-Path $tmp "surum.json"
    try { Enex-Indir $Url $mf 6 } catch { & $K "surum.json alinamadi ($($_.Exception.Message))"; return "internet-yok" }
    $m = [IO.File]::ReadAllText($mf, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if (-not (Enex-ImzaDogru $m $PubX $PubY)) { & $K "IMZA GECERSIZ - paket reddedildi (surum $($m.ver))"; return "imza-hatali" }
    $yeni = [version][string]$m.ver; $eski = Enex-YerelSurum $Base
    if ($yeni -le $eski) { & $K "guncel ($eski)"; return "guncel" }
    foreach ($f in @($m.files)) { if ($script:EnexIzinli -cnotcontains [string]$f.n) { & $K "izinsiz dosya adi: $($f.n)"; return "imza-hatali" } }
    if ($Sor -and -not (& $Sor ([string]$m.ver) ([string]$m.notes) ([string]$eski))) { & $K "$yeni ertelendi"; return "ertelendi" }
    $kok = [Uri]$Url; $i = 0; $n = @($m.files).Count
    foreach ($f in @($m.files)) {
      $i++; & $D "Guncelleme indiriliyor ($i/$n)..."
      $h = Join-Path $tmp ([string]$f.n)
      try { $fu = (New-Object System.Uri -ArgumentList $kok, (([string]$f.n) + "?v=" + [string]$m.ver)).AbsoluteUri; Enex-Indir $fu $h 120 } catch { & $K "$($f.n) indirilemedi ($($_.Exception.Message))"; return "indirme-hatasi" }
      $oz = Enex-Sha256Hex ([IO.File]::ReadAllBytes($h))
      if ($oz -ne ([string]$f.sha256).ToLower()) { & $K "$($f.n) ozeti tutmadi - paket reddedildi"; return "indirme-hatasi" }
    }
    & $D "Guncelleme yukleniyor..."
    $yd = Join-Path (Join-Path $Base "eski") ($eski.ToString() + "_" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Path $yd -Force | Out-Null
    foreach ($f in @($m.files)) { $hd = Join-Path $Base ([string]$f.n); if (Test-Path $hd) { Copy-Item $hd (Join-Path $yd ([string]$f.n)) -Force } }
    try {
      foreach ($f in @($m.files)) { Copy-Item (Join-Path $tmp ([string]$f.n)) (Join-Path $Base ([string]$f.n)) -Force -ErrorAction Stop }
    } catch {
      & $K "kopyalama hatasi, eski surum geri yukleniyor: $($_.Exception.Message)"
      Get-ChildItem $yd | ForEach-Object { Copy-Item $_.FullName (Join-Path $Base $_.Name) -Force -ErrorAction SilentlyContinue }
      return "hata"
    }
    & $K "$eski -> $yeni yuklendi (yedek: $yd)"
    return "yuklendi"
  } catch { & $K "hata: $($_.Exception.Message)"; return "hata" }
  finally { if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } }
}
