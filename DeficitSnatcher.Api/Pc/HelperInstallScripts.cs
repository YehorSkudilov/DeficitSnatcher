namespace DeficitSnatcher.Api.Pc;

/// <summary>
/// The one-line install commands on the Helpers tab: a script that downloads the newest PC helper from the download page
/// and installs it, or updates it when it's installed already (keeping its setup). The setup code is given on the command
/// line, never put in the script, so the scripts are the same for everyone and served without signing in.
/// </summary>
public static class HelperInstallScripts
{
    /// <summary>Mac and Linux: <c>curl -fsSL {site}/api/runner/install.sh | sh -s -- [setup code]</c></summary>
    public static string Shell => $$"""
        #!/bin/sh
        # Installs or updates the Deficit Snatcher PC helper (Mac, Linux).
        #   curl -fsSL <site>/api/runner/install.sh | sh -s -- [setup code]
        set -e
        code="$1"
        case "$(uname -s)-$(uname -m)" in
          Darwin-arm64) rid=osx-arm64 ;;
          Darwin-*) rid=osx-x64 ;;
          Linux-x86_64) rid=linux-x64 ;;
          *) echo "There's no PC helper for $(uname -s) $(uname -m)."; exit 1 ;;
        esac
        tmp="$(mktemp -d)"
        trap 'rm -rf "$tmp"' EXIT
        echo "Downloading the PC helper ($rid)..."
        curl -fsSL "{{HelperVersions.ReleaseBase}}/DeficitSnatcher-PC-helper-$rid.zip" -o "$tmp/helper.zip"
        mkdir "$tmp/helper"
        if command -v unzip >/dev/null 2>&1; then
          unzip -q "$tmp/helper.zip" -d "$tmp/helper"
        elif command -v python3 >/dev/null 2>&1; then
          python3 -m zipfile -e "$tmp/helper.zip" "$tmp/helper"
        else
          echo "Unpacking it needs unzip (or python3): install one and run this again."; exit 1
        fi
        chmod +x "$tmp/helper/DeficitSnatcher.Runner"
        # It copies itself into place, sets up autostart and starts (over an installed one: updates it, same setup). The
        # terminal is its input (this script came through a pipe).
        if [ -n "$code" ]; then
          "$tmp/helper/DeficitSnatcher.Runner" setup "$code" < /dev/tty
        else
          "$tmp/helper/DeficitSnatcher.Runner" < /dev/tty
        fi
        """.Replace("\r\n", "\n");

    /// <summary>Windows (PowerShell): <c>&amp; ([scriptblock]::Create((irm {site}/api/runner/install.ps1))) [setup code]</c></summary>
    public static string PowerShell => $$"""
        # Installs or updates the Deficit Snatcher PC helper (Windows), with its installer.
        #   & ([scriptblock]::Create((irm <site>/api/runner/install.ps1))) [setup code]
        param([string]$Code)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $setup = Join-Path $env:TEMP 'DeficitSnatcher-PC-helper-Setup.exe'
        Write-Host 'Downloading the PC helper...'
        Invoke-WebRequest '{{HelperVersions.ReleaseBase}}/DeficitSnatcher-PC-helper-Setup.exe' -OutFile $setup -UseBasicParsing
        Write-Host 'Installing...'
        Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        Remove-Item $setup -ErrorAction SilentlyContinue
        $exe = Join-Path $env:LOCALAPPDATA 'DeficitSnatcher\app\DeficitSnatcher.Runner.exe'
        # Connects it (first time) or starts it again (an update).
        if ($Code) { Start-Process $exe -ArgumentList 'setup', $Code -NoNewWindow -Wait }
        else { Start-Process $exe -NoNewWindow -Wait }
        """;
}
