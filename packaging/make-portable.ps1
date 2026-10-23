<#
  Сборка portable-архива DpiShell (без установщика и подписи — portable-zip, docs/dev/building.md).

  Что делает:
    1. dotnet publish окна: Release, win-x64, self-contained, single-file (как в DpiShell.App.csproj);
       папка data\ ложится рядом с exe (цель KeepDataNextToExe в csproj).
    2. Проверяет data\ по data\data.json (sha256 каждого файла) — в архив не попадёт то, что не проверено.
    3. Кладёт рядом README.txt, THIRD-PARTY-NOTICES.txt (тексты лицензий полностью) и LICENSE.txt, если он есть в корне репозитория.
    4. Упаковывает папку DpiShell\ в publish\DpiShell-<версия>-portable.zip и печатает sha256 архива.
       Архива ядра zapret в нём нет (и это проверяется): на такой архив Защитник срабатывает ещё в скачанном zip.
    5. Кладёт рядом publish\zapret-core-offline.zip — копию vendor\zapret-<версия ядра>.zip, сверенную с zip_sha256 из data\core.json:
       отдельный файл выпуска для тех, у кого не открывается GitHub (кнопка «Указать архив ядра…» в окне).

  Запуск из корня репозитория:  powershell -ExecutionPolicy Bypass -File packaging\make-portable.ps1
  Сам архив в git не кладётся (publish\ в .gitignore).
#>
param(
    [string]$Configuration = 'Release',
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo 'publish' }
$work = Join-Path $OutDir 'portable-work'
$stage = Join-Path $work 'DpiShell'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

# 1. Публикация окна.
$proj = Join-Path $repo 'src\DpiShell.App\DpiShell.App.csproj'
& dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $stage -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

$exe = Join-Path $stage 'DpiShell.exe'
if (-not (Test-Path $exe)) { throw 'Нет DpiShell.exe в выходе публикации' }

# 2. Данные: всё, что перечислено в data.json, есть и совпадает по sha256 (текст — по LF, как в DataManifest.Sha256).
$data = Join-Path $stage 'data'
$manifest = Get-Content (Join-Path $data 'data.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$sha = [Security.Cryptography.SHA256]::Create()
$bad = @()
foreach ($p in $manifest.files.PSObject.Properties) {
    $file = Join-Path $data ($p.Name -replace '/', '\')
    if (-not (Test-Path $file)) { $bad += "$($p.Name): нет файла"; continue }
    $bytes = [IO.File]::ReadAllBytes($file)
    if ([IO.Path]::GetExtension($file).ToLowerInvariant() -in '.txt', '.json', '.sha256') {
        $lf = New-Object 'System.Collections.Generic.List[byte]' $bytes.Length
        for ($i = 0; $i -lt $bytes.Length; $i++) {
            if (-not ($bytes[$i] -eq 13 -and $i + 1 -lt $bytes.Length -and $bytes[$i + 1] -eq 10)) { $lf.Add($bytes[$i]) }
        }
        $bytes = $lf.ToArray()
    }
    $h = -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    if ($h -ne $p.Value.sha256) { $bad += "$($p.Name): sha256 не совпал" }
}
if ($bad.Count -gt 0) { throw "Данные не совпали с data.json:`n" + ($bad -join "`n") }
Write-Host "data\ проверена: файлов $(@($manifest.files.PSObject.Properties).Count), версия данных $($manifest.version)"

# Лишнее из публикации (отладочные символы встроены в exe).
Get-ChildItem $stage -Filter '*.pdb' -Recurse | Remove-Item -Force

# 3. Тексты рядом с exe.
Copy-Item (Join-Path $PSScriptRoot 'README.txt') $stage
Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') $stage
$license = Join-Path $repo 'LICENSE'
if (Test-Path $license) { Copy-Item $license (Join-Path $stage 'LICENSE.txt') }
else { Write-Warning 'В корне репозитория нет LICENSE: лицензия DpiShell (MIT) должна лежать там — архив без неё.' }

# Архива ядра в поставке быть не должно: на него Защитник срабатывает (Tecabans) ещё в скачанном zip программы.
$coreZips = @(Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -like 'zapret-*.zip' -or $_.FullName.Substring($stage.Length) -match '\\vendor\\' })
if ($coreZips.Count -gt 0) { throw "В поставку попал архив ядра: $($coreZips.Name -join ', ')" }

# 4. Архив: внутри — папка DpiShell\.
$version = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $version) { $version = (Get-Item $exe).VersionInfo.FileVersion }
$version = ($version -split '\+')[0]
$zip = Join-Path $OutDir "DpiShell-$version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Имена записей — через «/»: CreateFromDirectory в Windows PowerShell 5.1 пишет «\», и не всякий распаковщик это понимает.
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in Get-ChildItem $stage -Recurse -File) {
        $name = 'DpiShell/' + $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
Remove-Item $work -Recurse -Force

# 5. Офлайн-копия ядра — отдельным файлом выпуска: тот же архив bol-van, что в core.json (окно сверит тот же sha256).
$core = Get-Content (Join-Path $data 'core.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$vendorZip = Join-Path $repo "vendor\zapret-$($core.version).zip"
if (-not (Test-Path $vendorZip)) { throw "Нет $vendorZip — офлайн-копии ядра для выпуска" }
if ((Get-FileHash $vendorZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $core.zip_sha256) { throw "$vendorZip не совпал с zip_sha256 в data\core.json" }
$offline = Join-Path $OutDir 'zapret-core-offline.zip'
Copy-Item $vendorZip $offline -Force
Write-Host "Офлайн-копия ядра: $offline ($($core.version), sha256 $($core.zip_sha256))"

$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Готово: $zip ($size МБ)"
Write-Host "sha256: $zipHash"
