# Publica PC Doctor en un pendrive (o en cualquier carpeta) de forma repetible.
#   1. Agente/herramienta (pcdoctor-cli) sin extraer nada a carpetas temporales: sus DLL quedan a la vista.
#   2. Calcula las huellas SHA-256 de esos archivos y las graba dentro de la interfaz.
#   3. Copia programa, biblioteca, datos, licencias e instrucciones.
# El estado de las PCs revisadas (pcdoctor\state) no se toca. Uso: .\tools\publicar.ps1 -Destino H:\
param([string]$Destino = 'H:\')
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$stage = Join-Path $env:LOCALAPPDATA 'pcdbuild\usb'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$comun = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=none', '-nologo', '-v', 'q')

& $dotnet publish (Join-Path $raiz 'src\PCDoctor.Cli') @comun '-p:IncludeNativeLibrariesForSelfExtract=false' -o "$stage\cli"
if ($LASTEXITCODE -ne 0) { throw 'falló la publicación de pcdoctor-cli' }
$huellas = (Get-ChildItem "$stage\cli" -File | ForEach-Object { "$($_.Name)=$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)" }) -join '|'

& $dotnet publish (Join-Path $raiz 'src\PCDoctor.App') @comun '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' "-p:AgentHashes=$huellas" -o "$stage\app"
if ($LASTEXITCODE -ne 0) { throw 'falló la publicación de la interfaz' }

$pc = Join-Path $Destino 'pcdoctor'
New-Item -ItemType Directory -Force -Path $pc, "$pc\licencias" | Out-Null
Copy-Item "$stage\app\PCDoctor.exe" (Join-Path $Destino 'ABRIR PC DOCTOR.exe') -Force
Get-ChildItem "$stage\cli" -File | Copy-Item -Destination $pc -Force
foreach ($d in 'knowledge', 'data') {
    if (Test-Path "$pc\$d") { Remove-Item "$pc\$d" -Recurse -Force }
    Copy-Item (Join-Path $raiz $d) "$pc\$d" -Recurse
}
Copy-Item (Join-Path $raiz 'LICENSE') "$pc\licencias\GPL-3.0.txt" -Force
Copy-Item (Join-Path $raiz 'THIRD-PARTY-NOTICES.md') "$pc\licencias\THIRD-PARTY-NOTICES.md" -Force
Copy-Item (Join-Path $raiz 'packaging\LEEME.txt') (Join-Path $Destino 'LEEME.txt') -Force
"Publicado en $Destino · agente: $huellas"
