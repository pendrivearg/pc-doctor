# Publica PC Doctor en un pendrive (o en cualquier carpeta) de forma repetible.
#   1. Agente/herramienta (pcdoctor-cli) sin extraer nada a carpetas temporales: sus DLL quedan a la vista.
#   2. Calcula las huellas SHA-256 de esos archivos y las graba dentro de la interfaz.
#   3. Copia programa, abridor automático, biblioteca, datos, licencias e instrucciones.
#   4. Si está la clave privada del proyecto, sella el programa (así el abridor sabe que es un PC Doctor original).
# El estado de las PCs revisadas (pcdoctor\state) no se toca. Uso: .\tools\publicar.ps1 -Destino H:\
param([string]$Destino = 'H:\', [string]$Clave = (Join-Path $env:USERPROFILE 'PCDoctor-claves\pcdoctor-clave-privada.pem'))
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
# Abridor automático (.NET Framework 4.8, ya incluido en Windows): queda en la PC solo si la persona lo pide.
& $dotnet build (Join-Path $raiz 'src\PCDoctor.Abrir') -c Release -nologo -v q -o "$stage\abrir"
if ($LASTEXITCODE -ne 0) { throw 'falló el abridor automático' }

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
Copy-Item "$stage\abrir\PCDoctor-Abrir.exe" $pc -Force
Copy-Item (Join-Path $raiz 'src\PCDoctor.App\pcdoctor.ico') $pc -Force
# Ícono y nombre del pendrive en el Explorador (Windows ya no deja que un pendrive abra programas solo; esto es lo único que respeta).
$autorun = Join-Path $Destino 'autorun.inf'
if (Test-Path $autorun) { (Get-Item $autorun -Force).Attributes = 'Normal' }
[IO.File]::WriteAllText($autorun, "[autorun]`r`nicon=pcdoctor\pcdoctor.ico`r`nlabel=PC DOCTOR`r`n", [Text.Encoding]::ASCII)

# Sello firmado del programa: sin él, el abridor automático no abre el pendrive. Lo pone solo el dueño (con la clave privada).
if (Test-Path $Clave) {
    & "$pc\pcdoctor-cli.exe" sellar-programa --datos $Destino --clave $Clave | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'no se pudo sellar el programa' }
} else {
    Remove-Item "$pc\firma-programa.txt", "$pc\firma-programa.txt.firma" -Force -ErrorAction SilentlyContinue
    Write-Host "Sin la clave privada ($Clave): el programa queda sin sello y no se abre solo al enchufarlo." -ForegroundColor Yellow
}
# Al abrir el pendrive se ve solo «ABRIR PC DOCTOR» y el LEEME: las carpetas del programa quedan ocultas (siguen funcionando).
foreach ($oculta in 'pcdoctor', 'PC-Doctor-AI-codigo', 'autorun.inf') {
    $item = Get-Item -LiteralPath (Join-Path $Destino $oculta) -Force -ErrorAction SilentlyContinue
    if ($item) { $item.Attributes = $item.Attributes -bor [IO.FileAttributes]::Hidden }
}
"Publicado en $Destino · agente: $huellas"
