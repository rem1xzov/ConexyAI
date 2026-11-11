# Требуется PowerShell 7+ (или Windows PowerShell 5.1) и .NET 9 SDK.
# Собирает клиент под несколько RID, подписывает exe (если задан сертификат),
# собирает установщик Inno Setup и подписанный манифест обновлений latest.json.
#
# Пример:
#   pwsh scripts/publish.ps1 -Version 0.9.29 -PfxPath C:\keys\conexy.pfx -PfxPassword (Read-Host -AsSecureString)
#   pwsh scripts/publish.ps1 -Version 0.9.29 -SkipManifest   # без подписи (только для теста)

[CmdletBinding()]
param(
    [string]$Version,
    [string[]]$Rids = @("win-x64", "linux-x64", "osx-x64", "osx-arm64"),
    [string]$Output = "dist",
    [string]$UrlBase = "https://conexyai.ru/downloads",
    [string]$SigningKey = ".update-signing-key.pem",
    [string]$PfxPath,
    [System.Security.SecureString]$PfxPassword,
    [string]$CertThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipManifest
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repo

if (-not $Version) {
    $csproj = Get-Content "ConexyAI.Desktop/ConexyAI.Desktop.csproj" -Raw
    if ($csproj -match "<Version>([^<]+)</Version>") { $Version = $Matches[1] }
    else { throw "Не удалось определить версию в ConexyAI.Desktop.csproj" }
}
Write-Host "Версия: $Version" -ForegroundColor Cyan

$out = Join-Path $repo $Output
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

function Find-Tool([string]$Name, [string]$FallbackPattern) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    if ($FallbackPattern) {
        $found = Get-ChildItem -Path $FallbackPattern -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$signtool = Find-Tool "signtool.exe" "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe"
$iscc = Find-Tool "iscc.exe" "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not $iscc) { $iscc = Find-Tool "iscc.exe" "${env:ProgramFiles}\Inno Setup 6\ISCC.exe" }
$onWindows = $env:OS -eq "Windows_NT"

$shouldSign = $PfxPath -or $CertThumbprint
if ($shouldSign -and -not $signtool) {
    throw "Задан сертификат, но signtool.exe не найден. Установите Windows SDK."
}
if (-not $shouldSign) {
    Write-Warning "Сертификат не задан: exe и установщик останутся неподписанными. SmartScreen и антивирусы будут ругаться."
}

function Sign-File([string]$Path) {
    if (-not $shouldSign) { return }
    $signArgs = @("sign", "/fd", "SHA256")
    if ($TimestampUrl) { $signArgs += @("/tr", $TimestampUrl, "/td", "SHA256") }
    if ($PfxPath) {
        $signArgs += @("/f", (Resolve-Path $PfxPath).Path)
        if ($PfxPassword) {
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
                [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword))
            $signArgs += @("/p", $plain)
        }
    }
    else {
        $signArgs += @("/sha1", $CertThumbprint)
    }
    $signArgs += $Path
    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool не подписал $Path (код $LASTEXITCODE)" }
    Write-Host "Подписан: $(Split-Path $Path -Leaf)" -ForegroundColor Green
}

foreach ($rid in $Rids) {
    Write-Host "`n=== $rid ===" -ForegroundColor Cyan
    $tmp = Join-Path $repo "obj\publish\$rid"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }

    $publishArgs = @(
        "publish", "ConexyAI.Desktop/ConexyAI.Desktop.csproj",
        "-c", "Release", "-r", $rid, "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:PublishTrimmed=false",
        "-p:DebugType=None", "-p:DebugSymbols=false",
        "-p:Version=$($Version)", "-p:FileVersion=$($Version).0",
        "-o", $tmp, "--nologo"
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $rid завершился с кодом $LASTEXITCODE" }

    $produced = Join-Path $tmp "conexy.exe"
    if (-not (Test-Path $produced)) { $produced = Join-Path $tmp "conexy" }
    if (-not (Test-Path $produced)) { throw "Не найден собранный клиент в $tmp" }

    $target = if ($rid -like "win-*") { Join-Path $out "conexy-$rid.exe" } else { Join-Path $out "conexy-$rid" }
    Copy-Item $produced $target -Force
    if ($rid -like "win-*") { Sign-File $target }
    Write-Host "  -> $(Split-Path $target -Leaf) ($([math]::Round((Get-Item $target).Length / 1MB, 1)) МБ)" -ForegroundColor Green
}

# Установщик: только для win-x64, и только если установлен Inno Setup.
$installer = $null
if ($Rids -contains "win-x64") {
    if ($iscc) {
        Write-Host "`n=== Inno Setup ===" -ForegroundColor Cyan
        & $iscc "installer\conexy.iss" "/DMyAppVersion=$Version" "/DMySourceDir=$out" "/O$out" "/Qp"
        if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }
        $installer = Get-ChildItem $out -Filter "conexy-setup-*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($installer) {
            Sign-File $installer.FullName
            Write-Host "Установщик: $($installer.Name)" -ForegroundColor Green
        }
    }
    else {
        Write-Warning "Inno Setup (iscc.exe) не найден — установщик не собран. Скачайте Inno Setup 6 и повторите."
    }
}

if (-not $SkipManifest) {
    if (-not (Test-Path $SigningKey)) {
        throw "Нет ключа подписи манифеста: $SigningKey. Создайте: openssl ecparam -name prime256v1 -genkey -noout -out .update-signing-key.pem"
    }
    Write-Host "`n=== Манифест обновлений ===" -ForegroundColor Cyan
    $signer = Join-Path $out "conexy-win-x64.exe"
    if ($onWindows -and (Test-Path $signer)) {
        & $signer updatesign $out $Version --url-base $UrlBase --key (Resolve-Path $SigningKey).Path
        if ($LASTEXITCODE -ne 0) { throw "updatesign завершился с кодом $LASTEXITCODE" }
        & $signer updateverify $out
        if ($LASTEXITCODE -ne 0) { throw "updateverify не подтвердил подпись манифеста" }
    }
    else {
        & dotnet run --project ConexyAI.Desktop/ConexyAI.Desktop.csproj -c Release -- `
            updatesign $out $Version --url-base $UrlBase --key (Resolve-Path $SigningKey).Path
        if ($LASTEXITCODE -ne 0) { throw "updatesign завершился с кодом $LASTEXITCODE" }
        & dotnet run --project ConexyAI.Desktop/ConexyAI.Desktop.csproj -c Release -- updateverify $out
        if ($LASTEXITCODE -ne 0) { throw "updateverify не подтвердил подпись манифеста" }
    }
}

Write-Host "`nГотово. Файлы в ${out}:" -ForegroundColor Cyan
Get-ChildItem $out | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-32} {1,12:N0}" -f $_.Name, $_.Length)
}
Write-Host @"

Как публиковать:
  1. Загрузите ВСЕ файлы из $out на $UrlBase (имена не меняйте).
  2. latest.json загружайте ПОСЛЕДНИМ: клиент не должен увидеть манифест раньше файлов.
  3. Для GitHub-релизов приложите те же файлы к тегу v$Version.
  4. Приватный ключ $SigningKey не коммитьте и храните в бэкапе: без него новые
     версии не пройдут проверку у уже установленных клиентов.
"@ -ForegroundColor DarkGray
