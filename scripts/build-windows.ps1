#Requires -Version 5.1
param(
    [switch]$SkipTests,
    [switch]$Msix,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot

# 1. Locate dotnet.exe (requires .NET 8.0.100+ SDK)
$DotnetExe = $null
$DotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($DotnetCmd) {
    $DotnetExe = $DotnetCmd.Source
} else {
    $CandidatePaths = @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "C:\Program Files\dotnet\dotnet.exe",
        "X:\Program Files\dotnet\dotnet.exe",
        "$env:LocalAppData\Microsoft\dotnet\dotnet.exe",
        "$env:USERPROFILE\.dotnet\dotnet.exe"
    )
    foreach ($Candidate in $CandidatePaths) {
        if ($Candidate -and [System.IO.File]::Exists($Candidate)) {
            $DotnetExe = $Candidate
            $DotnetDir = [System.IO.Path]::GetDirectoryName($Candidate)
            $env:PATH = "$DotnetDir;$env:PATH"
            $env:DOTNET_ROOT = $DotnetDir
            break
        }
    }
}

if (-not $DotnetExe) {
    Write-Host "[ERROR] .NET SDK (8.0.100+) was not found." -ForegroundColor Red
    Write-Host "Install it in PowerShell with:" -ForegroundColor Yellow
    Write-Host "  winget install --id Microsoft.DotNet.SDK.8 --exact --silent --accept-package-agreements --accept-source-agreements" -ForegroundColor Yellow
    exit 1
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

Write-Host "Using .NET CLI: $DotnetExe" -ForegroundColor Gray
& $DotnetExe --version

$DistDir = if ($OutputDirectory) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
} else {
    Join-Path $RootDir "dist"
}
[System.IO.Directory]::CreateDirectory($DistDir) | Out-Null

$StageRoot = Join-Path $DistDir (".plyph-build-" + [guid]::NewGuid().ToString("N"))
$PortableOut = Join-Path $DistDir "Plyph-Portable"
$PortableStage = Join-Path $StageRoot "portable"
$InstalledStage = Join-Path $StageRoot "app"
$SetupStage = Join-Path $StageRoot "setup"
$InstalledZip = Join-Path $StageRoot "Plyph.Payload.zip"
$PortableZip = Join-Path $StageRoot "Plyph-Portable-win-x64.zip"
$PreviousPortable = Join-Path $StageRoot "previous-portable"
$PortablePublished = $false
$WinUiCsproj = Join-Path $RootDir "src\Plyph.Windows\Plyph.Windows.csproj"
$InstallerCsproj = Join-Path $RootDir "src\Plyph.Installer\Plyph.Installer.csproj"
$TestsCsproj = Join-Path $RootDir "tests\Plyph.Core.Tests\Plyph.Core.Tests.csproj"

try {
    if (-not $SkipTests) {
        Write-Host ""
        Write-Host "[1/4] Running Plyph.Core unit tests..." -ForegroundColor Cyan
        & $DotnetExe test $TestsCsproj -c Release -p:NuGetAudit=false --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Unit tests failed."
        }
    } else {
        Write-Host "[1/4] Skipping unit tests..." -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "[2/4] Publishing the shared WinUI 3 app..." -ForegroundColor Cyan
    & $DotnetExe publish $WinUiCsproj `
        -c Release `
        -r win-x64 `
        -p:Platform=x64 `
        -p:PortableBuild=false `
        -p:SelfContained=true `
        -p:NuGetAudit=false `
        -p:CopyOutputSymbolsToPublishDirectory=false `
        --nologo `
        -o $InstalledStage

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish Plyph."
    }

    Write-Host ""
    Write-Host "[3/4] Packaging the installed payload and Portable ZIP..." -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($InstalledStage, $InstalledZip)
    $CscExe = @(
        "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    ) | Where-Object { $_ -and [System.IO.File]::Exists($_) } | Select-Object -First 1

    if ($CscExe) {
        [IO.Directory]::CreateDirectory($PortableStage) | Out-Null
        $PortableAppDir = Join-Path $PortableStage "app"
        Move-Item -LiteralPath $InstalledStage -Destination $PortableAppDir
        [IO.File]::WriteAllText((Join-Path $PortableAppDir "portable-parent-data.marker"), "")
        # Build lightweight root launcher so runtime DLLs/WinUI files remain cleanly tucked inside app\
        $LauncherSrc = Join-Path $StageRoot "PortableLauncher.cs"
        $LauncherExe = Join-Path $PortableStage "Plyph-Portable.exe"
        $IconFile = Join-Path $RootDir "src\Plyph.Windows\Assets\Plyph.ico"
        $LauncherCode = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

[assembly: AssemblyTitle("Plyph Portable")]
[assembly: AssemblyProduct("Plyph")]
[assembly: AssemblyVersion("1.1.0.0")]

internal static class PortableLauncher
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        var appDir = Path.Combine(root, "app");
        var target = Path.Combine(appDir, "Plyph.exe");
        if (!File.Exists(target))
        {
            MessageBoxW(IntPtr.Zero, "Missing application folder:\n" + target, "Plyph Portable", 0x10u);
            return 1;
        }

        var psi = new ProcessStartInfo(target, JoinArgs(args))
        {
            WorkingDirectory = appDir,
            UseShellExecute = false
        };
        Process.Start(psi);
        return 0;
    }

    private static string JoinArgs(string[] args)
    {
        if (args == null || args.Length == 0) return "";
        var quoted = new string[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i] ?? "";
            var escaped = Regex.Replace(a, "(\\\\*)\"", "$1$1\\\"");
            escaped = Regex.Replace(escaped, "(\\\\+)$", "$1$1");
            quoted[i] = "\"" + escaped + "\"";
        }
        return string.Join(" ", quoted);
    }
}
'@
        [System.IO.File]::WriteAllText($LauncherSrc, $LauncherCode)
        & $CscExe /nologo /target:winexe /optimize+ "/win32icon:$IconFile" "/out:$LauncherExe" $LauncherSrc
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to build Portable root launcher."
        }
    } else {
        Move-Item -LiteralPath $InstalledStage -Destination $PortableStage
        $PortableAppDir = $PortableStage
        Move-Item -LiteralPath (Join-Path $PortableStage "Plyph.exe") -Destination (Join-Path $PortableStage "Plyph-Portable.exe")
    }

    [IO.File]::WriteAllText((Join-Path $PortableAppDir "portable.marker"), "")
    [IO.File]::Copy((Join-Path $RootDir "LICENSE"), (Join-Path $PortableAppDir "LICENSE.txt"), $true)
    [IO.File]::Copy((Join-Path $RootDir "README.md"), (Join-Path $PortableAppDir "README.md"), $true)
    [System.IO.Compression.ZipFile]::CreateFromDirectory($PortableStage, $PortableZip)

    Write-Host ""
    Write-Host "[4/4] Building self-extracting Windows Installer (Plyph-Setup.exe)..." -ForegroundColor Cyan
    & $DotnetExe publish $InstallerCsproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PayloadZipPath="$InstalledZip" `
        -p:NuGetAudit=false `
        -p:DebugType=none `
        --nologo `
        -o $SetupStage

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build Plyph-Setup.exe"
    }

    if ([IO.Directory]::Exists($PortableOut)) {
        [IO.Directory]::Move($PortableOut, $PreviousPortable)
    }

    try {
        # Keep local portable settings and avoid including them in the release ZIP.
        $previousData = Join-Path $PreviousPortable "data"
        if ([IO.Directory]::Exists($previousData)) {
            Copy-Item -LiteralPath $previousData -Destination $PortableStage -Recurse
        }
        [IO.Directory]::Move($PortableStage, $PortableOut)
        $PortablePublished = $true
    } catch {
        if ([IO.Directory]::Exists($PreviousPortable)) {
            [IO.Directory]::Move($PreviousPortable, $PortableOut)
        }
        throw
    }

    [IO.File]::Copy($PortableZip, (Join-Path $DistDir "Plyph-Portable-win-x64.zip"), $true)
    [IO.File]::Copy((Join-Path $SetupStage "Plyph-Setup.exe"), (Join-Path $DistDir "Plyph-Setup.exe"), $true)

    if ($Msix) {
        $WinAppCmd = Get-Command winapp -ErrorAction SilentlyContinue
        if (-not $WinAppCmd) {
            Write-Host "[WARN] -Msix requested, but WinApp CLI (`winapp`) was not found. Skipping MSIX." -ForegroundColor Yellow
        } else {
            Write-Host ""
            Write-Host "[Bonus] Packaging MSIX via WinApp CLI..." -ForegroundColor Cyan
            & winapp package $WinUiCsproj -p:MsixPackageBuild=true --arch x64 --self-contained --no-sign --output $DistDir
        }
    }

    Write-Host ""
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "WinUI 3 build succeeded! Output files in: $DistDir" -ForegroundColor Green
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "  1. Non-Install Portable Folder : $PortableOut\Plyph-Portable.exe"
    Write-Host "  2. Non-Install Portable ZIP    : $DistDir\Plyph-Portable-win-x64.zip"
    Write-Host "  3. Windows Installer EXE       : $DistDir\Plyph-Setup.exe"
} finally {
    if ([IO.Directory]::Exists($StageRoot)) {
        $resolvedStage = (Resolve-Path -LiteralPath $StageRoot).ProviderPath
        $resolvedDist = (Resolve-Path -LiteralPath $DistDir).ProviderPath
        if ((Split-Path -Parent $resolvedStage) -eq $resolvedDist -and
            (Split-Path -Leaf $resolvedStage) -like '.plyph-build-*' -and
            (![IO.Directory]::Exists($PreviousPortable) -or $PortablePublished)) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
