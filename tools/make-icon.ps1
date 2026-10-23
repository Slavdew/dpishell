# Пересобирает src/DpiShell.App/Assets/app.ico тем же кодом, что рисует значок в трее (Icons.BuildIco).
# Запуск: powershell -File tools\make-icon.ps1   (потом закоммитить изменённый app.ico)
$root = Split-Path $PSScriptRoot -Parent
$env:DPISHELL_WRITE_ICO = Join-Path $root 'src\DpiShell.App\Assets\app.ico'
dotnet test (Join-Path $root 'tests\DpiShell.Tests') --filter "FullyQualifiedName~IconTests.Ico_matches_generator"
