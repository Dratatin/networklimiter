#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Enregistre le pilote WinDivert pour le développement local.

.DESCRIPTION
    En production, le pilote est enregistré par l'installeur MSI, à un moment où
    l'utilisateur a consenti à une élévation (research.md, R-011). Le service ouvre donc
    ses handles avec WINDIVERT_FLAG_NO_INSTALL et n'installe jamais rien silencieusement.

    Ce script rend le même service en développement : il enregistre le pilote restauré et
    vérifié par restore-windivert.ps1, sans rien télécharger lui-même.

    Il refuse d'enregistrer un binaire dont la signature n'est pas valide.

.PARAMETER Remove
    Supprime le service pilote au lieu de l'enregistrer.

.EXAMPLE
    ./tools/register-windivert-dev.ps1
    ./tools/register-windivert-dev.ps1 -Remove
#>
[CmdletBinding()]
param([switch] $Remove)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$driverPath = Join-Path $repoRoot 'build/windivert/x64/WinDivert64.sys'
$serviceName = 'WinDivert'

function Write-Step { param([string] $Message) Write-Host "[windivert-dev] $Message" }

if ($Remove) {
    Write-Step "suppression du service $serviceName"
    & sc.exe stop   $serviceName | Out-Null
    & sc.exe delete $serviceName | Out-Null
    Write-Step 'OK.'
    exit 0
}

if (-not (Test-Path -LiteralPath $driverPath)) {
    Write-Host "[windivert-dev] ECHEC : pilote introuvable a $driverPath" -ForegroundColor Red
    Write-Host "[windivert-dev] Executez d abord ./tools/restore-windivert.ps1" -ForegroundColor Red
    exit 1
}

# Le script de restauration a deja verifie empreinte et signature, mais ce script peut etre
# lance seul : on revalide plutot que de supposer.
$signature = Get-AuthenticodeSignature -LiteralPath $driverPath
if ($signature.Status -ne 'Valid') {
    Write-Host "[windivert-dev] ECHEC : signature du pilote invalide ($($signature.Status))" -ForegroundColor Red
    Write-Host '[windivert-dev] Ne PAS contourner. Relancez restore-windivert.ps1.' -ForegroundColor Red
    exit 1
}

Write-Step "signature valide : $($signature.SignerCertificate.Subject)"
Write-Step "enregistrement du service $serviceName"

& sc.exe create $serviceName type= kernel start= demand binPath= $driverPath | Out-Null
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1073) {
    Write-Host "[windivert-dev] ECHEC : sc.exe create a rendu $LASTEXITCODE" -ForegroundColor Red
    exit 1
}

Write-Step 'OK. Le service peut maintenant ouvrir ses handles avec NO_INSTALL.'
