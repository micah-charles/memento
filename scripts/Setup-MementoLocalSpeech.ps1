[CmdletBinding()]
param([string]$SpeechRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MEMENTO\speech'))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$SpeechRoot = [IO.Path]::GetFullPath($SpeechRoot)
New-Item -ItemType Directory -Force -Path $SpeechRoot | Out-Null
$zip = Join-Path $SpeechRoot 'whisper-1.9.2.zip'
$model = Join-Path $SpeechRoot 'ggml-large-v3-turbo-q5_0.bin'
function Get-VerifiedAsset([string]$Url, [string]$Path, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Path) -and ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256)) { return }
    & curl.exe --fail --location --silent --show-error --connect-timeout 20 --max-time 900 --retry 2 --output ($Path + '.download') $Url
    if ($LASTEXITCODE -ne 0) { throw 'Unable to download local speech asset.' }
    if ((Get-FileHash -LiteralPath ($Path + '.download') -Algorithm SHA256).Hash -ne $Sha256) { throw 'Downloaded asset checksum failed.' }
    Move-Item -LiteralPath ($Path + '.download') -Destination $Path -Force
}
Get-VerifiedAsset 'https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/whisper-bin-x64.zip' $zip '49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a'
Get-VerifiedAsset 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin' $model '394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2'
$runtime = Join-Path $SpeechRoot 'whisper-1.9.2'
Expand-Archive -LiteralPath $zip -DestinationPath $runtime -Force
$cli = Get-ChildItem -LiteralPath $runtime -Filter whisper-cli.exe -Recurse | Select-Object -First 1
if ($null -eq $cli) { throw 'Whisper CLI missing from verified bundle.' }
@{ executable = $cli.FullName; model = $model; version = '1.9.2'; modelSha256 = '394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $SpeechRoot 'local-speech.json') -Encoding UTF8
Write-Output "Local speech assets ready: $SpeechRoot"
