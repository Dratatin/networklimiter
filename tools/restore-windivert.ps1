#Requires -Version 5.1
<#
.SYNOPSIS
    Restaure les binaires WinDivert epingles et verifie leur provenance.

.DESCRIPTION
    Telecharge l'archive WinDivert decrite par tools/windivert.lock.json, verifie son
    empreinte SHA-256, extrait les binaires x64, puis verifie pour chacun :
      - son empreinte SHA-256 par rapport au verrou ;
      - sa signature Authenticode et l'empreinte du certificat signataire, lorsque le
        verrou l'exige.

    Toute divergence est une ERREUR BLOQUANTE. Le script n'ecrit rien dans l'arborescence
    de build tant que l'ensemble des verifications n'a pas reussi : un binaire noyau dont
    la provenance n'est pas etablie ne doit jamais atteindre un build.

    Ce script ne charge ni n'execute aucun des binaires qu'il telecharge.

.PARAMETER Destination
    Repertoire ou deposer WinDivert.dll et WinDivert64.sys. Defaut : build/windivert/x64.

.PARAMETER LockFile
    Chemin du verrou de provenance. Defaut : tools/windivert.lock.json.

.PARAMETER Force
    Retelecharge et revalide meme si la destination est deja peuplee et conforme.

.EXAMPLE
    ./tools/restore-windivert.ps1
#>
[CmdletBinding()]
param(
    [string] $Destination,
    [string] $LockFile,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $LockFile)    { $LockFile    = Join-Path $PSScriptRoot 'windivert.lock.json' }
if (-not $Destination) { $Destination = Join-Path $repoRoot 'build/windivert/x64' }

function Write-Step { param([string] $Message) Write-Host "[windivert] $Message" }
function Fail {
    param([string] $Message)
    Write-Host ''
    Write-Host "[windivert] ECHEC : $Message" -ForegroundColor Red
    Write-Host '[windivert] Aucun binaire n a ete installe.' -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# 1. Lecture du verrou
# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $LockFile)) {
    Fail "verrou de provenance introuvable : $LockFile"
}

$lock = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
Write-Step "verrou charge : WinDivert $($lock.version)"

# ---------------------------------------------------------------------------
# 2. Court-circuit si la destination est deja conforme
# ---------------------------------------------------------------------------

function Test-DestinationUpToDate {
    foreach ($relativePath in $lock.files.PSObject.Properties.Name) {
        $leaf = Split-Path -Leaf $relativePath
        $target = Join-Path $Destination $leaf
        if (-not (Test-Path -LiteralPath $target)) { return $false }
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($actual -ne $lock.files.$relativePath.sha256.ToUpperInvariant()) { return $false }
    }
    return $true
}

if (-not $Force -and (Test-Path -LiteralPath $Destination) -and (Test-DestinationUpToDate)) {
    Write-Step 'binaires deja presents et conformes au verrou, rien a faire.'
    exit 0
}

# ---------------------------------------------------------------------------
# 3. Telechargement de l'archive
# ---------------------------------------------------------------------------

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("windivert-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

try {
    $archivePath = Join-Path $workDir 'windivert.zip'
    Write-Step "telechargement depuis $($lock.archive.url)"

    # TLS 1.2 explicite : Windows PowerShell 5.1 ne le selectionne pas toujours seul.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $lock.archive.url -OutFile $archivePath -UseBasicParsing
    } finally {
        $ProgressPreference = $previousProgress
    }

    # -----------------------------------------------------------------------
    # 4. Verification de l'archive
    # -----------------------------------------------------------------------

    $actualSize = (Get-Item -LiteralPath $archivePath).Length
    if ($actualSize -ne $lock.archive.sizeBytes) {
        Fail "taille inattendue de l archive : $actualSize octets, attendu $($lock.archive.sizeBytes)"
    }

    $actualArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    $expectedArchiveHash = $lock.archive.sha256.ToUpperInvariant()
    if ($actualArchiveHash -ne $expectedArchiveHash) {
        Fail @"
empreinte de l archive non conforme au verrou.
  attendu : $expectedArchiveHash
  obtenu  : $actualArchiveHash
Ne PAS contourner cette verification. Soit l archive publiee a change, soit le
telechargement a ete altere. Dans les deux cas, une revue humaine est requise avant
toute mise a jour de tools/windivert.lock.json.
"@
    }
    Write-Step "archive verifiee (SHA-256 conforme, $actualSize octets)"

    # -----------------------------------------------------------------------
    # 5. Extraction
    # -----------------------------------------------------------------------

    $extractDir = Join-Path $workDir 'extracted'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir -Force

    $rootDir = Get-ChildItem -LiteralPath $extractDir -Directory | Select-Object -First 1
    if ($null -eq $rootDir) { Fail 'archive vide ou structure inattendue' }

    # -----------------------------------------------------------------------
    # 6. Verification fichier par fichier
    # -----------------------------------------------------------------------

    $staged = @{}

    foreach ($relativePath in $lock.files.PSObject.Properties.Name) {
        $entry = $lock.files.$relativePath
        $sourcePath = Join-Path $rootDir.FullName $relativePath
        $leaf = Split-Path -Leaf $relativePath

        if (-not (Test-Path -LiteralPath $sourcePath)) {
            Fail "fichier absent de l archive : $relativePath"
        }

        $actualHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        $expectedHash = $entry.sha256.ToUpperInvariant()
        if ($actualHash -ne $expectedHash) {
            Fail @"
empreinte non conforme pour $relativePath.
  attendu : $expectedHash
  obtenu  : $actualHash
"@
        }

        if ($entry.authenticode -eq 'required') {
            $signature = Get-AuthenticodeSignature -LiteralPath $sourcePath

            if ($signature.Status -ne 'Valid') {
                Fail "signature Authenticode invalide pour $relativePath : statut $($signature.Status)"
            }

            $thumbprint = $signature.SignerCertificate.Thumbprint
            if ($thumbprint -ne $entry.expectedThumbprint.ToUpperInvariant()) {
                Fail @"
le pilote est signe par un certificat different de celui epingle.
  attendu : $($entry.expectedThumbprint)
  obtenu  : $thumbprint
  sujet   : $($signature.SignerCertificate.Subject)
Il s agit d un changement d editeur du pilote noyau. Revue humaine obligatoire.
"@
            }

            $subject = $signature.SignerCertificate.Subject
            if ($subject -notlike "*$($entry.expectedSubjectContains)*") {
                Fail "sujet du certificat inattendu pour $relativePath : $subject"
            }

            Write-Step "$leaf : empreinte conforme, signature valide ($thumbprint)"
        }
        else {
            Write-Step "$leaf : empreinte conforme (non signe, atteste par SHA-256 uniquement)"
        }

        $staged[$leaf] = $sourcePath
    }

    # -----------------------------------------------------------------------
    # 7. Installation, une fois TOUTES les verifications passees
    # -----------------------------------------------------------------------

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($leaf in $staged.Keys) {
        Copy-Item -LiteralPath $staged[$leaf] -Destination (Join-Path $Destination $leaf) -Force
    }

    Write-Step "installe dans $Destination"
    Write-Step 'OK.'
}
finally {
    if (Test-Path -LiteralPath $workDir) {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
