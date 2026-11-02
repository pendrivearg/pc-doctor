# PC Doctor — arma un pendrive de PC Doctor con modo rescate. BORRA el pendrive: lo ejecuta el dueño, con permiso de administrador.
#   1. Firmas de virus con el actualizador oficial de ClamAV (si no estaban).
#   2. Dos partes: «PCD-RESCATE» (FAT32, 4 GB, arranca sin Windows) y «PC DOCTOR» (exFAT, el resto).
#   3. Copia el modo rescate y deja a la vista solo un aviso de no tocar.
#   4. Copia PC Doctor (y lo que tuviera el pendrive) desde la carpeta de respaldo.
# Si se corta después de borrar, se puede volver a correr: sigue desde donde quedó.
# Uso: .\tools\armar-pendrive.ps1 [-Letra H] [-Base C:\Users\<vos>\PCDoctor-armado]
#   La carpeta Base tiene: stage-boot\ (modo rescate armado), respaldo\ (lo que va en la parte PC DOCTOR) y clamav-win\clamav.zip.
param([string]$Letra = 'H', [string]$Base = (Join-Path $env:USERPROFILE 'PCDoctor-armado'))
$ErrorActionPreference = 'Stop'
$stage = "$Base\stage-boot"
$respaldo = "$Base\respaldo"
$resultado = "$Base\armado-resultado.txt"
Set-Location C:\
Start-Transcript "$Base\armado.log" -Force | Out-Null
function Paso($t) { Write-Host ''; Write-Host $t -ForegroundColor Cyan }
try {
    if (-not (Test-Path "$respaldo\ABRIR PC DOCTOR.exe")) { throw 'No está la copia de seguridad del pendrive: no se borra nada.' }

    Paso '1/4  Firmas de virus (actualizador oficial de ClamAV)...'
    $db = "$stage\pcdoctor\clamav-db"
    if ((Get-ChildItem $db -Filter 'daily.*' -ErrorAction SilentlyContinue).Count -gt 0) { Write-Host 'Las firmas ya estaban descargadas.' }
    else {
        $x = "$Base\clamav-win\x"
        if (-not (Test-Path $x)) { Expand-Archive "$Base\clamav-win\clamav.zip" $x -Force }
        $fc = Get-ChildItem $x -Recurse -Filter freshclam.exe | Select-Object -First 1
        New-Item -ItemType Directory -Force $db | Out-Null
        "DatabaseMirror database.clamav.net`r`nDatabaseDirectory $db`r`n" | Out-File -Encoding ascii "$Base\clamav-win\freshclam.conf"
        & $fc.FullName "--config-file=$Base\clamav-win\freshclam.conf" "--datadir=$db" --stdout
    }
    $firmas = (Get-ChildItem $db -Filter 'daily.*' -ErrorAction SilentlyContinue).Count -gt 0
    if (-not $firmas) { Write-Host 'No se pudieron bajar las firmas: el modo rescate las descarga cuando tenga Internet por cable.' -ForegroundColor Yellow }

    Paso '2/4  Rearmando el pendrive en dos partes...'
    $disk = Get-Partition -DriveLetter $Letra | Get-Disk
    if ($disk.BusType -ne 'USB') { throw "$Letra`: no es un pendrive USB: no se toca." }
    if ($disk.Size -lt 8GB -or $disk.Size -gt 512GB) { throw 'Tamaño inesperado: no se toca.' }
    $n = $disk.Number
    $parts = @(Get-Partition -DiskNumber $n)
    $yaBorrado = $parts.Count -eq 1 -and (Get-Volume -Partition $parts[0] -ErrorAction SilentlyContinue).FileSystemLabel -eq 'PCD-RESCATE'
    if ($yaBorrado) {
        Write-Host 'El pendrive ya estaba borrado: sigo desde donde quedó.'
        $boot = $parts[0]
    } else {
        mountvol "$Letra`:" /P | Out-Null  # suelta el pendrive aunque alguna ventana lo tenga abierto
        Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false
        if ((Get-Disk -Number $n).PartitionStyle -eq 'RAW') { Initialize-Disk -Number $n -PartitionStyle MBR }
        $boot = New-Partition -DiskNumber $n -Size 4GB -MbrType FAT32 -IsActive
        Format-Volume -Partition $boot -FileSystem FAT32 -NewFileSystemLabel 'PCD-RESCATE' -Confirm:$false | Out-Null
    }
    # La parte de rescate no se queda con la letra principal (es para PC Doctor y los archivos).
    $boot = Get-Partition -DiskNumber $n -PartitionNumber $boot.PartitionNumber
    if ($boot.DriveLetter -eq $Letra) { Remove-PartitionAccessPath -DiskNumber $n -PartitionNumber $boot.PartitionNumber -AccessPath "$Letra`:\" }
    $data = New-Partition -DiskNumber $n -UseMaximumSize -DriveLetter $Letra
    Format-Volume -Partition $data -FileSystem exFAT -NewFileSystemLabel 'PC DOCTOR' -Confirm:$false | Out-Null
    $boot = Get-Partition -DiskNumber $n -PartitionNumber $boot.PartitionNumber
    if (-not $boot.DriveLetter -or $boot.DriveLetter -eq [char]0) { Add-PartitionAccessPath -DiskNumber $n -PartitionNumber $boot.PartitionNumber -AssignDriveLetter }
    $r = (Get-Partition -DiskNumber $n -PartitionNumber $boot.PartitionNumber).DriveLetter

    Paso "3/4  Copiando el modo rescate a $r`:..."
    robocopy $stage "$r`:\" /E /R:2 /W:2 /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "falló la copia del modo rescate (código $LASTEXITCODE)" }
    # Para quien abra esa parte del pendrive: solo se ve el aviso; lo del sistema queda oculto (el arranque no usa ese atributo).
    foreach ($nombre in '.disk', 'EFI', 'boot', 'live', 'pcdoctor') {
        $i = Get-Item -LiteralPath "$r`:\$nombre" -Force -ErrorAction SilentlyContinue
        if ($i) { $i.Attributes = $i.Attributes -bor [IO.FileAttributes]::Hidden }
    }
    "Esta parte del pendrive es el MODO RESCATE de PC Doctor (arranca sin Windows).`r`nNo borres ni cambies nada acá.`r`n`r`nPara usar PC Doctor, abrí la otra parte del pendrive (PC DOCTOR) y hacé doble clic en «ABRIR PC DOCTOR»." |
        Out-File -Encoding utf8 "$r`:\MODO RESCATE - NO BORRAR NADA.txt"

    Paso "4/4  Copiando PC Doctor a $Letra`:..."
    robocopy $respaldo "$Letra`:\" /E /R:2 /W:2 /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "falló la copia (código $LASTEXITCODE). El respaldo sigue en $respaldo" }
    foreach ($oculta in 'pcdoctor', 'PC-Doctor-AI-codigo') {
        $i = Get-Item -LiteralPath "$Letra`:\$oculta" -Force -ErrorAction SilentlyContinue
        if ($i) { $i.Attributes = $i.Attributes -bor [IO.FileAttributes]::Hidden }
    }

    $okBoot = (Test-Path "$r`:\EFI\boot\bootx64.efi") -and (Test-Path "$r`:\live\filesystem.squashfs") -and (Test-Path "$r`:\pcdoctor\pcdoctor-rescate")
    $okData = Test-Path "$Letra`:\ABRIR PC DOCTOR.exe"
    "LISTO rescate=$r firmas=$firmas arranque=$okBoot datos=$okData" | Out-File -Encoding utf8 $resultado
    Write-Host ''
    Write-Host 'LISTO. El pendrive ya tiene PC Doctor y el modo rescate.' -ForegroundColor Green
}
catch {
    "ERROR: $($_.Exception.Message)" | Out-File -Encoding utf8 $resultado
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Stop-Transcript | Out-Null
    Write-Host ''
    Read-Host 'Apretá Enter para cerrar'
}
