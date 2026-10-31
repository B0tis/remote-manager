<#
.SYNOPSIS
    Builds, packs, and optionally uploads a Remote-Manager release (Velopack).

.DESCRIPTION
    1. dotnet publish (Release, win-x64, self-contained) with the version stamped into the assemblies.
    2. Optional Authenticode signing (see below), done by vpk on every exe/dll and on Setup.exe.
    3. vpk pack: full package, delta against the previous release in the output folder, Setup.exe, portable zip.
    4. Optional upload: a folder (local or network share) and/or GitHub Releases.

    Signing is ON when a certificate is configured, OFF otherwise (the log says which):
      - RM_SIGN_THUMBPRINT: thumbprint of a code-signing certificate in Cert:\CurrentUser\My (or LocalMachine\My),
        or the same as the password of the generic credential "RemoteManager:release-sign-thumbprint".
      - RM_SIGN_PFX + RM_SIGN_PFX_PASSWORD: a .pfx file and its password, or the generic credential
        "RemoteManager:release-sign-pfx" (user name = .pfx path, password = .pfx password). The certificate is
        imported into Cert:\CurrentUser\My for the run and removed afterwards, so the password never reaches a
        command line.
      - RM_SIGN_TIMESTAMP_URL: RFC 3161 timestamp server (default http://timestamp.digicert.com).
    Store a credential without putting the secret on a command line: cmdkey prompts when /pass has no value, e.g.
      cmdkey /generic:RemoteManager:release-sign-pfx /user:C:\certs\botis.pfx /pass

    GitHub upload token: GITHUB_TOKEN, else the token of the GitHub CLI (gh auth token). It goes to vpk through the
    VPK_TOKEN environment variable, never as an argument, and is not printed.

.EXAMPLE
    tools\release.ps1 -Version 0.4.0
    tools\release.ps1 -Version 0.4.1 -UploadTo \\fileserver\software\remote-manager
    tools\release.ps1 -Version 0.4.1 -UploadGitHub
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    # Release folder; it keeps earlier packages, which vpk needs for delta updates.
    [string] $OutputDir = (Join-Path $PSScriptRoot '..\artifacts\releases'),
    # Folder update source (local path or share): the previous release is fetched from it, the new one copied to it.
    [string] $UploadTo,
    [switch] $UploadGitHub,
    [string] $RepoUrl = 'https://github.com/B0tis/remote-manager',
    [string] $ReleaseNotes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PackId = 'Botis.RemoteManager'   # Setup installs to %LOCALAPPDATA%\Botis.RemoteManager; data stays in %LOCALAPPDATA%\RemoteManager.
$MainExe = 'RemoteManager.App.exe'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Project = Join-Path $Root 'src\RemoteManager.App\RemoteManager.App.csproj'

function Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Invoke-Checked([string] $exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments[0]) failed with exit code $LASTEXITCODE" }
}

# Generic credential from the Windows Credential Manager: @{ User; Secret } or $null.
function Read-StoredCredential([string] $target) {
    if (-not ('RmRelease.CredStore' -as [type])) {
        Add-Type -Namespace RmRelease -Name CredStore -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct CREDENTIAL {
    public int Flags; public int Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist;
    public int AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
}
[DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
[DllImport("advapi32.dll")]
public static extern void CredFree(IntPtr credential);
public static string[] Read(string target) {
    IntPtr p;
    if (!CredRead(target, 1, 0, out p)) return null;
    try {
        CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
        string secret = c.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) : "";
        return new[] { c.UserName ?? "", secret };
    } finally { CredFree(p); }
}
'@
    }
    $values = [RmRelease.CredStore]::Read($target)
    if ($null -eq $values) { return $null }
    return @{ User = $values[0]; Secret = $values[1] }
}

# Picks the signing certificate; returns @{ Thumbprint; Subject; Imported } or $null (signing off).
function Resolve-SigningCertificate {
    $thumbprint = $env:RM_SIGN_THUMBPRINT
    if (-not $thumbprint) {
        $storedThumbprint = Read-StoredCredential 'RemoteManager:release-sign-thumbprint'
        if ($storedThumbprint) { $thumbprint = $storedThumbprint.Secret }
    }
    if ($thumbprint) {
        $thumbprint = ($thumbprint -replace '\s', '').ToUpperInvariant()
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
        if (-not $cert) { throw "Signing: certificate $thumbprint not found in CurrentUser\My or LocalMachine\My." }
        return @{ Thumbprint = $cert.Thumbprint; Subject = $cert.Subject; Imported = $null }
    }

    $pfx = $env:RM_SIGN_PFX
    $password = $env:RM_SIGN_PFX_PASSWORD
    $stored = Read-StoredCredential 'RemoteManager:release-sign-pfx'
    if (-not $pfx -and $stored) { $pfx = $stored.User }
    if ($pfx -and -not $password -and $stored) { $password = $stored.Secret }
    if (-not $pfx) { return $null }
    if (-not (Test-Path $pfx)) { throw "Signing: PFX file not found: $pfx" }
    if (-not $password) { throw 'Signing: RM_SIGN_PFX is set but no password (RM_SIGN_PFX_PASSWORD or credential RemoteManager:release-sign-pfx).' }

    # Into the user's store for this run, so signtool selects it by thumbprint and the password stays in memory.
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]'UserKeySet, PersistKeySet'
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path $pfx).Path, $password, $flags)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
    $store.Open('ReadWrite')
    $alreadyThere = $store.Certificates.Find('FindByThumbprint', $cert.Thumbprint, $false).Count -gt 0
    if (-not $alreadyThere) { $store.Add($cert) }
    $store.Close()
    return @{ Thumbprint = $cert.Thumbprint; Subject = $cert.Subject; Imported = $(if ($alreadyThere) { $null } else { $cert }) }
}

function Remove-ImportedCertificate($signing) {
    if (-not $signing -or -not $signing.Imported) { return }
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
    $store.Open('ReadWrite')
    $store.Remove($signing.Imported)
    $store.Close()
    Write-Host 'Signing: temporary certificate removed from CurrentUser\My.'
}

# --- 0. Arguments ---------------------------------------------------------------------------------------------
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version must be SemVer like 0.4.1 or 0.5.0-beta.1, got '$Version'." }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
# Next to the release folder, so -OutputDir on another drive takes the ~250 MB publish output along.
$PublishDir = Join-Path (Split-Path $OutputDir -Parent) "publish\$Version"
New-Item -ItemType Directory -Force $OutputDir | Out-Null

Push-Location $Root
$signing = $null
$tokenSet = $false
try {
    Step 'Restoring vpk (dotnet-tools.json)'
    Invoke-Checked dotnet @('tool', 'restore')

    # --- 1. Publish ---------------------------------------------------------------------------------------------
    Step "Publishing Remote-Manager $Version (Release, win-x64, self-contained)"
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
    Invoke-Checked dotnet @('publish', $Project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        "-p:Version=$Version", '-o', $PublishDir, '--nologo')

    $stamped = (Get-Item (Join-Path $PublishDir $MainExe)).VersionInfo.ProductVersion
    if (-not $stamped.StartsWith($Version)) { throw "Published exe reports version '$stamped', expected '$Version'." }
    Write-Host "Published $MainExe, product version $stamped"

    # --- 2. Signing ---------------------------------------------------------------------------------------------
    $signing = Resolve-SigningCertificate
    $signArgs = @()
    if ($signing) {
        $timestamp = if ($env:RM_SIGN_TIMESTAMP_URL) { $env:RM_SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
        Write-Host "Signing: ON  (certificate '$($signing.Subject)', thumbprint $($signing.Thumbprint), timestamp $timestamp)" -ForegroundColor Green
        $signArgs = @('--signParams', "/fd sha256 /td sha256 /tr $timestamp /sha1 $($signing.Thumbprint)")
    }
    else {
        Write-Host 'Signing: OFF (no certificate: set RM_SIGN_THUMBPRINT or RM_SIGN_PFX, or store RemoteManager:release-sign-*). This build is unsigned.' -ForegroundColor Yellow
    }

    # --- 3. GitHub token (only for GitHub transfers) ------------------------------------------------------------
    if ($UploadGitHub) {
        $token = $env:GITHUB_TOKEN
        if (-not $token) { $token = (& gh auth token 2>$null) }
        if (-not $token) { throw 'GitHub upload needs GITHUB_TOKEN or a logged-in GitHub CLI (gh auth login).' }
        $env:VPK_TOKEN = $token.Trim()
        $tokenSet = $true
        Remove-Variable token
    }

    # --- 4. Previous release (for the delta package) ------------------------------------------------------------
    if ($UploadTo -and (Test-Path (Join-Path $UploadTo 'releases.win.json'))) {
        Step "Fetching the previous release from $UploadTo"
        Invoke-Checked dotnet @('vpk', 'download', 'local', '--path', $UploadTo, '-o', $OutputDir)
    }
    elseif ($UploadGitHub) {
        Step "Fetching the previous release from $RepoUrl"
        & dotnet vpk download github --repoUrl $RepoUrl -o $OutputDir
        if ($LASTEXITCODE -ne 0) { Write-Host 'No previous GitHub release found; packing without a delta.' -ForegroundColor Yellow }
    }

    # --- 5. Pack ------------------------------------------------------------------------------------------------
    Step "Packing $PackId $Version into $OutputDir"
    $packArgs = @('vpk', 'pack', '--packId', $PackId, '--packVersion', $Version, '--packDir', $PublishDir, '--runtime', 'win-x64',
        '--mainExe', $MainExe, '--packTitle', 'Remote-Manager', '--packAuthors', 'Botis', '-o', $OutputDir) + $signArgs
    if ($ReleaseNotes) { $packArgs += @('--releaseNotes', (Resolve-Path $ReleaseNotes).Path) }
    Invoke-Checked dotnet $packArgs

    if ($signing) {
        $setup = Join-Path $OutputDir "$PackId-win-Setup.exe"
        $signature = Get-AuthenticodeSignature $setup
        Write-Host "Signature of $(Split-Path $setup -Leaf): $($signature.Status), signer $($signature.SignerCertificate.Subject)"
    }

    # --- 6. Upload ----------------------------------------------------------------------------------------------
    if ($UploadTo) {
        Step "Copying the release to $UploadTo"
        New-Item -ItemType Directory -Force $UploadTo | Out-Null
        Invoke-Checked dotnet @('vpk', 'upload', 'local', '-o', $OutputDir, '--path', $UploadTo)
    }
    if ($UploadGitHub) {
        Step "Uploading to GitHub Releases of $RepoUrl (tag v$Version)"
        Invoke-Checked dotnet @('vpk', 'upload', 'github', '-o', $OutputDir, '--repoUrl', $RepoUrl, '--publish',
            '--releaseName', "Remote-Manager $Version", '--tag', "v$Version")
    }

    Step 'Done'
    Get-ChildItem $OutputDir -File | Where-Object { $_.Name -like "*$Version*" -or $_.Name -like '*Setup.exe' -or $_.Name -like 'releases.*.json' } |
        ForEach-Object { '{0,-55} {1,10:N0} KB' -f $_.Name, ($_.Length / 1KB) }
}
finally {
    if ($tokenSet) { Remove-Item Env:VPK_TOKEN -ErrorAction SilentlyContinue }
    Remove-ImportedCertificate $signing
    Pop-Location
}
