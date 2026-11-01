# Rearma el pendrive de PC Doctor en dos partes (necesita permiso de administrador):
#   1. «PCD-RESCATE» (FAT32, 4 GB): el modo rescate que arranca sin Windows (UEFI, compatible con Arranque seguro).
#   2. «PC DOCTOR» (exFAT, el resto): el programa para Windows y los archivos que se rescaten.
# BORRA el pendrive: antes hay que copiar su contenido a otro lado. Solo acepta un disco USB que hoy tenga la letra indicada.
param([Parameter(Mandatory)][string]$Letra, [Parameter(Mandatory)][string]$Registro)
$ErrorActionPreference = 'Stop'
Start-Transcript -Path $Registro -Force | Out-Null
try {
    $disk = Get-Partition -DriveLetter $Letra | Get-Disk
    if ($disk.BusType -ne 'USB') { throw "La unidad $Letra no es un pendrive USB ($($disk.BusType)): no se toca." }
    if ($disk.Size -lt 8GB -or $disk.Size -gt 512GB) { throw "Tamaño inesperado ($([math]::Round($disk.Size/1GB)) GB): no se toca." }
    $n = $disk.Number
    "Disco $n ($($disk.FriendlyName), $([math]::Round($disk.Size/1GB,1)) GB): se rearma."
    Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false
    if ((Get-Disk -Number $n).PartitionStyle -eq 'RAW') { Initialize-Disk -Number $n -PartitionStyle MBR }
    $boot = New-Partition -DiskNumber $n -Size 4GB -MbrType FAT32 -IsActive -AssignDriveLetter
    Format-Volume -Partition $boot -FileSystem FAT32 -NewFileSystemLabel 'PCD-RESCATE' -Confirm:$false | Out-Null
    $data = New-Partition -DiskNumber $n -UseMaximumSize -DriveLetter $Letra
    Format-Volume -Partition $data -FileSystem exFAT -NewFileSystemLabel 'PC DOCTOR' -Confirm:$false | Out-Null
    "RESCATE=$((Get-Partition -DiskNumber $n -PartitionNumber $boot.PartitionNumber).DriveLetter)"
    'LISTO'
}
catch { "ERROR: $($_.Exception.Message)" }
finally { Stop-Transcript | Out-Null }
