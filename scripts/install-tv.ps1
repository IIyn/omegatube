# One-command install for end users on Windows (PowerShell), the counterpart of install-tv.sh:
#   irm https://raw.githubusercontent.com/IIyn/omegatube/main/scripts/install-tv.ps1 | iex
# Guides through the TV's Developer Mode, sets up Java and the Tizen command-line tools if needed,
# creates a Samsung certificate for the TV, then signs and installs the latest OmegaTube release.
# Run as a file: .\install-tv.ps1 [TV IP address] [OmegaTube .tpk file]   (no arguments: asks interactively)

$Repo = 'IIyn/omegatube'
$Release = "https://github.com/$Repo/releases/latest/download"
$TizenStudioVersion = '6.1'
# Tizen tools break on paths with spaces (often the case under C:\Users), so everything lives at the drive root.
$TizenStudio = if ($env:TIZEN_STUDIO) { $env:TIZEN_STUDIO } else { "$env:SystemDrive\tizen-studio" }
$Work = "$env:SystemDrive\omegatube-installer"
$CertDir = "$Work\certificate"
$Sdb = "$TizenStudio\tools\sdb.exe"
$Tizen = "$TizenStudio\tools\ide\bin\tizen.bat"
$SrcDir = if ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { '' }

function Step($text) { Write-Host "`n==> $text" -ForegroundColor White }
function Fail($text) { throw $text }

# Runs a native tool and stops on a non-zero exit code.
function Run($exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { Fail "$(Split-Path -Leaf $exe) failed (exit code $LASTEXITCODE)." }
}

function Download($url, $out) {
    & curl.exe -fL --progress-bar -o $out $url
    if ($LASTEXITCODE -ne 0) { Fail "Download failed: $url" }
}

# Private Temurin JRE for the Samsung certificate tool (a system java is often Oracle's javapath shim, without keytool).
function Setup-Java {
    $script:JavaBin = "$Work\jre\bin"
    if (-not (Test-Path "$JavaBin\java.exe")) {
        Step 'Downloading Java (one time)'
        Download 'https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jre/hotspot/normal/eclipse' "$Work\jre.zip"
        Remove-Item -Recurse -Force "$Work\jre" -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force "$Work\jre" | Out-Null
        Run tar.exe -xf "$Work\jre.zip" -C "$Work\jre" --strip-components=1
        Remove-Item "$Work\jre.zip"
    }
    $env:Path = "$JavaBin;$env:Path"
}

# Tizen command-line tools + Samsung Certificate Extension (one time).
function Setup-Tizen {
    if (-not (Test-Path $Tizen)) {
        $installer = "$Work\web-cli_Tizen_Studio_$TizenStudioVersion.exe"
        Step "Downloading Samsung's TV tools (one time, a few minutes)"
        Download "https://download.tizen.org/sdk/Installer/tizen-studio_$TizenStudioVersion/web-cli_Tizen_Studio_${TizenStudioVersion}_windows-64.exe" $installer
        Step "Installing them into $TizenStudio"
        Start-Process -FilePath $installer -ArgumentList '--accept-license', $TizenStudio -Wait -NoNewWindow
        Remove-Item $installer
        if (-not (Test-Path $Tizen)) { Fail "Samsung's TV tools did not install into $TizenStudio." }
    }
    if (-not (Test-Path "$TizenStudio\.info\cert-add-on")) {
        Step 'Installing the Samsung Certificate Extension (one time)'
        Run "$TizenStudio\package-manager\package-manager-cli.exe" install --accept-license cert-add-on
    }
}

function Local-IP {
    try {
        $config = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1
        return @($config.IPv4Address)[0].IPAddress
    } catch {
        return $null
    }
}

function Developer-Mode-Guide {
    $ip = Local-IP
    if (-not $ip) { $ip = '(not found: type ipconfig in PowerShell and look for IPv4 Address)' }
    Step 'Turn on Developer Mode on the TV'
    Write-Host @"
  1. On the TV, open the Apps screen.
  2. On the remote, type 1 2 3 4 5 (no number keys? press the 123 button to show a number pad).
  3. Switch Developer Mode to On.
  4. In "Host PC IP", enter this computer's address: $ip
  5. Restart the TV: hold the power button until the Samsung logo shows again.

  (Already done before? Make sure the Host PC IP is still this computer's address.)
"@
    Read-Host "`nPress Enter when the TV has restarted" | Out-Null
}

function Connect-TV($tvIp) {
    while ($true) {
        if (-not $tvIp) {
            Write-Host "`nThe TV address is in Settings > General > Network > Network Status > IP Settings."
            $tvIp = (Read-Host 'TV IP address (e.g. 192.168.1.9)').Trim()
            if (-not $tvIp) { continue }
        }
        $script:Target = "${tvIp}:26101"
        Step "Connecting to the TV at $tvIp"
        & $Sdb connect $Target | Out-Null
        $pattern = "^$([regex]::Escape($Target))\s+device\s+(\S+)"
        $line = & $Sdb devices | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if ($line -and $line -match $pattern) { $script:Device = $Matches[1]; break }
        Write-Host "The TV did not answer. Check that Developer Mode is On with this computer's address" -ForegroundColor Red
        Write-Host "($(Local-IP)), that the TV was restarted afterwards and that both are on the same network."
        $tvIp = $null
    }
    $script:Duid = ((& $Sdb -s $Target shell 0 getduid) -join '').Trim()
    if (-not $Duid) { Fail "Could not read the TV's unique id (DUID)." }
    Write-Host "Found $Device (DUID $Duid)"
}

# Samsung author + distributor certificate for the TV, registered as the "omegatube" signing profile.
# Reused as long as it covers this TV, so updates keep the same signature.
function Setup-Certificate {
    $duids = @()
    if (Test-Path "$CertDir\distributor.crt") {
        $duids = @(& "$JavaBin\keytool.exe" -printcert -file "$CertDir\distributor.crt" |
            Select-String -AllMatches 'deviceid=([A-Z0-9]+)' | ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value })
    }
    if ($duids -contains $Duid) {
        if (& $Tizen security-profiles list | Select-String -Quiet '\bomegatube\b') { return }
    } else {
        Step 'Creating your Samsung certificate: a browser page opens, sign in with your Samsung account'
        $javaSrc = if ($SrcDir) { Join-Path $SrcDir '..\tools\samsung-cert\SamsungCert.java' } else { '' }
        if (-not $javaSrc -or -not (Test-Path $javaSrc)) {
            $javaSrc = "$Work\SamsungCert.java"
            Download "$Release/SamsungCert.java" $javaSrc
        }
        $plugin = Get-ChildItem "$TizenStudio\tools\certificate-manager\plugins\org.tizen.common.cert_*.jar" | Select-Object -First 1
        Remove-Item -Recurse -Force "$Work\certjar" -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force "$Work\certjar" | Out-Null
        Run tar.exe -xf $plugin.FullName -C "$Work\certjar"
        $slf4j = Get-ChildItem "$TizenStudio\tools\ide\lib-ncli\slf4j-api-*.jar" | Select-Object -First 1
        $cp = (@("$Work\certjar") + (Get-ChildItem "$Work\certjar\lib\*.jar").FullName + $slf4j.FullName) -join ';'
        Run "$JavaBin\java.exe" -cp $cp $javaSrc $CertDir OmegaTube ((@($duids) + $Duid) -join ',')
    }
    $pw = (Select-String -Path "$CertDir\passwords.txt" -Pattern '^author=(.*)$').Matches[0].Groups[1].Value
    Run $Tizen security-profiles add -f -n omegatube -a "$CertDir\author.p12" -p $pw -d "$CertDir\distributor.p12" -dp $pw | Out-Null
}

function Install-App($tpkIn) {
    $tpk = "$Work\OmegaTube.tpk"
    if ($tpkIn) {
        Copy-Item $tpkIn $tpk
    } else {
        Step 'Downloading the latest OmegaTube'
        Download "$Release/OmegaTube.tpk" $tpk
    }
    Step 'Signing the app for your TV'
    Run $Tizen package -t tpk -s omegatube '--' $tpk | Out-Null
    Step 'Installing on the TV'
    Run $Tizen install -n OmegaTube.tpk -t $Device '--' $Work
    & $Sdb -s $Target shell 0 execute org.omegatube.app | Out-Null
}

function Install-OmegaTube($tvIp, $tpkIn) {
    Write-Host "`nOmegaTube installer" -ForegroundColor White
    New-Item -ItemType Directory -Force $Work | Out-Null
    Setup-Java
    Setup-Tizen
    if (-not $tvIp) { Developer-Mode-Guide }
    Connect-TV $tvIp
    Setup-Certificate
    Install-App $tpkIn
    Write-Host "`nDone! OmegaTube is in the Apps list of your TV." -ForegroundColor Green
    Write-Host 'Open it, then go to Account to sign in to YouTube with your phone.'
}

# No "exit" on errors: with "irm | iex" it would close the user's PowerShell window.
try {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    Install-OmegaTube $(if ($args.Count -gt 0) { $args[0] }) $(if ($args.Count -gt 1) { $args[1] })
} catch {
    Write-Host "`n[!] $($_.Exception.Message)" -ForegroundColor Red
}
