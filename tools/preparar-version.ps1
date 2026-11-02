# Prepara una versión nueva de PC Doctor, firmada, lista para subir a GitHub Releases (gratis).
#   1. Corre todas las pruebas: si alguna falla, no se publica nada.
#   2. Arma el programa para Windows y el del modo rescate.
#   3. Crea los paquetes, el manifiesto y su firma con tu clave privada.
#   4. Si tenés la herramienta «gh» de GitHub instalada y con sesión iniciada, crea la versión y sube los archivos.
# Uso: .\tools\preparar-version.ps1 -Version 0.3.0 -Notas "Qué cambió" [-Clave C:\Users\...\pcdoctor-clave-privada.pem] [-Subir]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Notas,
    [string]$Clave = (Join-Path $env:USERPROFILE 'PCDoctor-claves\pcdoctor-clave-privada.pem'),
    [switch]$Subir
)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
if (-not (Test-Path $Clave)) { throw "No encuentro la clave privada en $Clave" }
$props = Get-Content (Join-Path $raiz 'Directory.Build.props') -Raw
if ($props -notmatch "<Version>$([regex]::Escape($Version))</Version>") { throw "Primero poné <Version>$Version</Version> en Directory.Build.props (el programa tiene que saber qué versión es)." }

Write-Host '1/4  Pruebas...' -ForegroundColor Cyan
& $dotnet test (Join-Path $raiz 'tests\PCDoctor.Tests') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Fallaron pruebas: no se publica.' }

Write-Host '2/4  Armando los programas...' -ForegroundColor Cyan
$base = Join-Path $env:LOCALAPPDATA "pcdbuild\version-$Version"
if (Test-Path $base) { Remove-Item $base -Recurse -Force }
$datos = "$base\datos"; $rescate = "$base\rescate"; $salida = "$base\publicar"
& (Join-Path $PSScriptRoot 'publicar.ps1') -Destino "$datos\" -Clave $Clave | Out-Null
& $dotnet publish (Join-Path $raiz 'src\PCDoctor.Rescue') -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -nologo -v q -o "$base\rescate-bin"
if ($LASTEXITCODE -ne 0) { throw 'falló el programa del modo rescate' }
New-Item -ItemType Directory -Force "$rescate\pcdoctor", "$rescate\live\config-hooks" | Out-Null
Copy-Item "$base\rescate-bin\pcdoctor-rescate" "$rescate\pcdoctor\"
# El script de arranque va con saltos de línea de Linux.
[IO.File]::WriteAllText("$rescate\live\config-hooks\9990-pcdoctor", ([IO.File]::ReadAllText((Join-Path $raiz 'packaging\rescate\9990-pcdoctor')) -replace "`r", ''))

Write-Host '3/4  Firmando...' -ForegroundColor Cyan
& "$datos\pcdoctor\pcdoctor-cli.exe" preparar-version --version $Version --notas $Notas --datos $datos --rescate $rescate --clave $Clave --salida $salida
if ($LASTEXITCODE -ne 0) { throw 'no se pudo preparar la versión' }

Write-Host '4/4  Publicando...' -ForegroundColor Cyan
if ($Subir) {
    $archivos = Get-ChildItem $salida -File | ForEach-Object FullName
    gh release create "v$Version" @archivos --title "PC Doctor $Version" --notes $Notas
    if ($LASTEXITCODE -ne 0) { throw 'gh no pudo crear la versión (¿iniciaste sesión con «gh auth login»?)' }
    Write-Host "Versión v$Version publicada. Los pendrives la ven en «Actualizar el pendrive» (por Internet o con el archivo pcdoctor-actualizacion-$Version.zip)." -ForegroundColor Green
} else {
    Write-Host "Listo. Subí TODOS los archivos de $salida a una versión nueva (tag v$Version) en GitHub > Releases. El archivo pcdoctor-actualizacion-$Version.zip sirve para PCs sin Internet." -ForegroundColor Green
}
