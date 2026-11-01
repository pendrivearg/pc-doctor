# Descarga el motor de IA (llama.cpp) y los modelos Qwen3.5 a la carpeta pcdoctor del pendrive,
# verificando cada archivo contra su SHA-256 oficial. Se puede volver a correr: retoma lo que falte.
param([string]$Destino = 'H:\pcdoctor')
$ErrorActionPreference = 'Stop'

$motor = @{
    Url    = 'https://github.com/ggml-org/llama.cpp/releases/download/b12032/llama-b12032-bin-win-cpu-x64.zip'
    Carpeta = Join-Path $Destino 'engines\llama-cpu-x64'
}
$modelos = @(
    @{ Archivo = 'Qwen3.5-0.8B-Q4_K_M.gguf'; Repo = 'unsloth/Qwen3.5-0.8B-GGUF'; Sha = 'bd258782e35f7f458f8aced1adc053e6e92e89bc735ba3be89d38a06121dc517' },
    @{ Archivo = 'Qwen3.5-2B-Q4_K_M.gguf';   Repo = 'unsloth/Qwen3.5-2B-GGUF';   Sha = 'aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223' },
    @{ Archivo = 'Qwen3.5-4B-Q4_K_M.gguf';   Repo = 'unsloth/Qwen3.5-4B-GGUF';   Sha = '00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4' }
)

New-Item -ItemType Directory -Force -Path $motor.Carpeta, (Join-Path $Destino 'models') | Out-Null
if (-not (Test-Path (Join-Path $motor.Carpeta 'llama-server.exe'))) {
    $zip = Join-Path $env:TEMP 'llama-cpu-x64.zip'
    curl.exe -sS -L --fail --retry 3 -o $zip $motor.Url
    if ($LASTEXITCODE -ne 0) { throw "no se pudo descargar el motor (curl $LASTEXITCODE)" }
    Expand-Archive -Path $zip -DestinationPath $motor.Carpeta -Force
    Remove-Item $zip
}
"motor: $(Test-Path (Join-Path $motor.Carpeta 'llama-server.exe'))"

foreach ($m in $modelos) {
    $dest = Join-Path $Destino "models\$($m.Archivo)"
    $tmp = "$dest.parcial"
    if (Test-Path $dest) { "$($m.Archivo): ya estaba"; continue }
    curl.exe -sS -L --fail --retry 5 -C - -o $tmp "https://huggingface.co/$($m.Repo)/resolve/main/$($m.Archivo)"
    if ($LASTEXITCODE -ne 0) { throw "no se pudo descargar $($m.Archivo) (curl $LASTEXITCODE)" }
    $hash = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $m.Sha) { Remove-Item $tmp; throw "$($m.Archivo): la suma de verificación no coincide" }
    Move-Item $tmp $dest
    "$($m.Archivo): descargado y verificado"
}
