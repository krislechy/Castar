#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$IosSdk,
    [Parameter(Mandatory)][string]$Zig,
    [Parameter(Mandatory)][string]$Ld64,
    [string]$CMake = 'cmake',
    [string]$Ninja = 'ninja',
    [string]$DotNet = 'dotnet',
    [ValidatePattern('^11\.0\.\d+$')][string]$RuntimeVersion = '11.0.0',
    [string]$PackageCache = (Join-Path ([IO.Path]::GetTempPath()) 'castar-ios-packages')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = Split-Path -Parent $PSScriptRoot
$IosSdk = (Resolve-Path -LiteralPath $IosSdk).Path
$Zig = (Resolve-Path -LiteralPath $Zig).Path
$Ld64 = (Resolve-Path -LiteralPath $Ld64).Path
$DotNet = (Get-Command $DotNet -CommandType Application).Source
$CMake = (Get-Command $CMake -CommandType Application).Source
$Ninja = (Get-Command $Ninja -CommandType Application).Source
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$PackageCache = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackageCache)
if (Test-Path -LiteralPath $OutputPath) {
    throw "Choose a new output directory: $OutputPath"
}
$outputPrefix = $OutputPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if ($PackageCache -eq $OutputPath -or $PackageCache.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PackageCache must be outside the output directory.'
}
if (!(Test-Path -LiteralPath (Join-Path $IosSdk 'usr/lib/libSystem.tbd'))) {
    throw 'IosSdk must contain the iPhoneOS SDK.'
}
$sdkMetadata = Get-Content -Raw -LiteralPath (Join-Path $IosSdk 'SDKSettings.json') | ConvertFrom-Json
$sdkVersion = [version]$sdkMetadata.Version
if ($sdkVersion -lt [version]'15.0') {
    throw 'Use an iPhoneOS SDK for iOS 15 or later.'
}

$outputParent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
$outputParent = (Resolve-Path -LiteralPath $outputParent).Path
$scratch = Join-Path $outputParent ('.castar-ios-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
New-Item -ItemType Directory -Path $PackageCache -Force | Out-Null

function Expand-NuGetPackage([string]$Id, [string]$Version) {
    $name = "$Id.$Version.nupkg"
    $package = Join-Path $PackageCache $name
    $snapshot = Join-Path $scratch $name
    $cached = Test-Path -LiteralPath $package
    if ($cached) {
        Copy-Item -LiteralPath $package -Destination $snapshot
    }
    else {
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$Id/$Version/$name" -OutFile $snapshot
    }
    & $DotNet nuget verify $snapshot --all | Out-Host

    if (!$cached) {
        $pending = Join-Path $PackageCache ('.download-' + [Guid]::NewGuid().ToString('N'))
        try {
            [IO.File]::Copy($snapshot, $pending)
            try {
                [IO.File]::Move($pending, $package, $false)
            }
            catch [IO.IOException] {
                if (![IO.File]::Exists($package)) { throw }
            }
        }
        finally {
            [IO.File]::Delete($pending)
        }
    }

    $destination = Join-Path $scratch $Id
    [IO.Compression.ZipFile]::ExtractToDirectory($snapshot, $destination)
    return $destination
}

function Copy-MissingAssemblies([string]$Source, [string]$Destination) {
    foreach ($assembly in Get-ChildItem -LiteralPath $Source -Filter '*.dll' -File) {
        $target = Join-Path $Destination $assembly.Name
        if (!(Test-Path -LiteralPath $target) -and $assembly.Name -ne 'Castar.Server.dll') {
            Copy-Item -LiteralPath $assembly.FullName -Destination $target
        }
    }
}

function Copy-Arm64Dylib([string]$Source, [string]$Destination) {
    # The framework contains a universal Mach-O. Extract the generic ARM64 device slice.
    $bytes = [IO.File]::ReadAllBytes($Source)
    $sliceCount = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 4))
    if ([Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 0)) -ne 0xCAFEBABE -or
        $sliceCount -lt 1 -or $sliceCount -gt 16 -or 8 + 20 * $sliceCount -gt $bytes.Length) {
        throw 'Unexpected SkiaSharp framework binary format.'
    }
    for ($index = 0; $index -lt $sliceCount; $index++) {
        $record = 8 + 20 * $index
        $cpu = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, $record))
        $subtype = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, $record + 4))
        if ($cpu -ne 0x0100000C -or ($subtype -band 0x00FFFFFF) -ne 0) { continue }
        $offset = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, $record + 8))
        $length = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, $record + 12))
        if ($offset -lt 0 -or $length -le 0 -or [long]$offset + $length -gt $bytes.Length) {
            throw 'Invalid ARM64 slice in the SkiaSharp framework.'
        }
        $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
        try { $output.Write($bytes, $offset, $length) }
        finally { $output.Dispose() }
        return
    }
    throw 'SkiaSharp framework does not contain a generic ARM64 slice.'
}

function Build-Zxing([string]$Version, [string]$Destination, [string[]]$LinkFlags) {
    # Commit recorded by ZXingCpp 0.5.3 on NuGet.
    if ($Version -ne '0.5.3') {
        throw "Update the iOS ZXing source pin to match ZXingCpp $Version."
    }
    $commit = '287c85df6f961c8efbfb5ffd736cd9457b8b890e'
    $archive = Join-Path $scratch 'zxing-cpp.tar.gz'
    Invoke-WebRequest "https://codeload.github.com/zxing-cpp/zxing-cpp/tar.gz/$commit" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '97D952C661B1F79D21AACC2EC544EF05C4D1465F55692CC49622EA6A8166CA7B') {
        throw 'The ZXing-C++ source checksum did not match.'
    }
    $sourceFolder = "zxing-cpp-$commit"
    & tar -xzf $archive -C $scratch "$sourceFolder/core" "$sourceFolder/LICENSE" "$sourceFolder/zxing.cmake" | Out-Host
    $source = Join-Path $scratch $sourceFolder
    $toolchain = Join-Path $scratch 'ios-toolchain.cmake'
    $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ZxingToolchain.cmake') -Raw
    [IO.File]::WriteAllText($toolchain, $template.Replace('CASTAR_ZIG', $Zig.Replace('\', '/')).Replace('CASTAR_IOS_SDK', $IosSdk.Replace('\', '/')))

    $build = Join-Path $scratch 'zxing-build'
    & $CMake -S (Join-Path $source 'core') -B $build -G Ninja "-DCMAKE_MAKE_PROGRAM=$Ninja" `
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF `
        -DZXING_C_API=ON -DZXING_READERS=ON -DZXING_WRITERS=OFF -DZXING_UNIT_TESTS=OFF | Out-Host
    & $CMake --build $build --target ZXing --parallel 6 | Out-Host
    & $Ld64 @LinkFlags -dylib -dead_strip -install_name '@rpath/libZXing.dylib' -exported_symbol '_ZXing_*' `
        -all_load (Join-Path $build 'libZXing.a') -lc++ -lSystem -o (Join-Path $Destination 'libZXing.dylib') | Out-Host
    return $source
}

try {
    [xml]$versions = Get-Content -Raw -LiteralPath (Join-Path $repo 'Directory.Packages.props')
    $sqliteVersion = $versions.SelectSingleNode('//PackageVersion[@Include="SQLite"]').Version
    $skiaVersion = $versions.SelectSingleNode('//PackageVersion[@Include="SkiaSharp"]').Version
    $zxingVersion = $versions.SelectSingleNode('//PackageVersion[@Include="ZXingCpp"]').Version
    if (!$sqliteVersion -or !$skiaVersion -or !$zxingVersion) {
        throw 'The shared SQLite, SkiaSharp or ZXingCpp package version is missing.'
    }

    $core = Expand-NuGetPackage 'microsoft.netcore.app.runtime.ios-arm64' $RuntimeVersion
    $asp = Expand-NuGetPackage 'microsoft.aspnetcore.app.runtime.osx-arm64' $RuntimeVersion
    $sqlite = Expand-NuGetPackage 'sqlite' $sqliteVersion
    $skia = Expand-NuGetPackage 'skiasharp.nativeassets.ios' $skiaVersion
    $published = Join-Path $scratch 'managed'
    Push-Location -LiteralPath $repo
    try {
        & $DotNet publish Castar.Server/Castar.Server.csproj -c Release --nologo `
            --no-self-contained -p:UseAppHost=false -o $published
    }
    finally {
        Pop-Location
    }

    $bundle = Join-Path $scratch 'bundle'
    $lib = Join-Path $bundle 'lib'
    New-Item -ItemType Directory -Path $lib -Force | Out-Null
    $coreManaged = Join-Path $core 'runtimes/ios-arm64/lib/net11.0'
    $coreNative = Join-Path $core 'runtimes/ios-arm64/native'
    Copy-MissingAssemblies $coreManaged $lib
    Copy-MissingAssemblies (Join-Path $asp 'runtimes/osx-arm64/lib/net11.0') $lib
    Copy-MissingAssemblies $published $lib
    foreach ($directory in @($coreManaged, $coreNative)) {
        foreach ($library in Get-ChildItem -LiteralPath $directory -Filter '*.dylib' -File) {
            Copy-Item -LiteralPath $library.FullName -Destination $lib
        }
    }
    Copy-Item -LiteralPath (Join-Path $published 'Castar.Server.dll') -Destination $bundle

    $linkFlags = @('-arch', 'arm64', '-platform_version', 'ios', '15.0', $sdkVersion.ToString(), '-syslibroot', $IosSdk,
        '-L' + (Join-Path $IosSdk 'usr/lib'), '-headerpad', '0x400')
    Push-Location -LiteralPath $scratch
    try {
        $programs = @{ Host = 'castar-host'; Watchdog = 'castar-watchdog' }
        foreach ($source in $programs.Keys) {
            $object = Join-Path $scratch "$source.o"
            & $Zig cc -target aarch64-ios.15.0 -mcpu=apple_a9 -isysroot $IosSdk `
                -isystem (Join-Path $IosSdk 'usr/include') -O2 -Wall -Wextra -Werror `
                -c (Join-Path $PSScriptRoot "$source.c") -o $object
            & $Ld64 @linkFlags -S $object -lSystem -o (Join-Path $bundle $programs[$source])
        }

        $appSource = Join-Path $PSScriptRoot 'App'
        $app = Join-Path $bundle 'CastarControl.app'
        $frameworks = Join-Path $IosSdk 'System/Library/Frameworks'
        $appObject = Join-Path $scratch 'App.o'
        New-Item -ItemType Directory -Path $app | Out-Null
        & $Zig cc -target aarch64-ios.15.0 -mcpu=apple_a9 -isysroot $IosSdk `
            -isystem (Join-Path $IosSdk 'usr/include') -isystem (Join-Path $IosSdk 'usr/include/pthread') `
            -iframework $frameworks -fobjc-arc -fblocks `
            -O2 -Wall -Wextra -Werror -c (Join-Path $appSource 'main.m') -o $appObject
        & $Ld64 @linkFlags "-F$frameworks" -S $appObject -framework UIKit -framework Foundation -lobjc -lSystem `
            -o (Join-Path $app 'CastarControl')
        Copy-Item -LiteralPath (Join-Path $appSource 'Info.plist') -Destination $app
        foreach ($icon in @('AppIcon60@2x.png', 'AppIcon60@3x.png')) {
            Copy-Item -LiteralPath (Join-Path $appSource $icon) -Destination $app
        }
        Copy-Item -LiteralPath (Join-Path $appSource 'entitlements.plist') -Destination (Join-Path $bundle 'CastarControl.entitlements.plist')
    }
    finally {
        Pop-Location
    }
    foreach ($file in @('run-server.sh', 'install.sh', 'update-server.sh')) {
        $script = Get-Content -LiteralPath (Join-Path $PSScriptRoot $file) -Raw
        [IO.File]::WriteAllText((Join-Path $bundle $file), $script.Replace("`r`n", "`n"))
    }
    foreach ($file in @('org.castar.server.plist', 'org.castar.watchdog.plist', 'org.castar.updater.plist',
            'org.castar.updater-recovery.plist', 'DEPLOYMENT.md', 'New-HttpsCertificate.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $bundle
    }
    $serverProject = Join-Path $repo 'Castar.Server/Castar.Server.csproj'
    $serverVersion = (& $DotNet msbuild $serverProject -nologo '-getProperty:Version').Trim()
    if ($serverVersion -notmatch '^\d{1,9}\.\d{1,9}\.\d{1,9}$') {
        throw 'Castar Server version must contain three numeric components.'
    }
    [IO.File]::WriteAllText((Join-Path $bundle 'version'), "$serverVersion`n")
    & $Ld64 @linkFlags -dylib -install_name '@rpath/libe_sqlite3.dylib' `
        -force_load (Join-Path $sqlite 'runtimes/ios-arm64/native/e_sqlite3.a') -lSystem `
        -o (Join-Path $lib 'libe_sqlite3.dylib')
    $skiaFramework = Join-Path $skia 'runtimes/ios/native/libSkiaSharp.framework/libSkiaSharp'
    Copy-Arm64Dylib $skiaFramework (Join-Path $lib 'libSkiaSharp.dylib')
    $zxing = Build-Zxing $zxingVersion $lib $linkFlags

    $notices = Join-Path $bundle 'notices'
    New-Item -ItemType Directory -Path $notices | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'notices') -File |
        Copy-Item -Destination $notices
    foreach ($packageRoot in @($core, $asp, $sqlite, $skia, $zxing)) {
        foreach ($notice in Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Name -Match '^(LICENSE|THIRD.PARTY|NOTICE)') {
            $name = (Split-Path -Leaf $packageRoot) + '-' + $notice.Name
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $notices $name)
        }
    }
    $assets = Get-Content -LiteralPath (Join-Path $repo 'Castar.Server/obj/project.assets.json') -Raw |
        ConvertFrom-Json -AsHashtable
    $dependencyModel = $assets.libraries.Keys | Where-Object { $_ -like 'Microsoft.Extensions.DependencyModel/*' }
    foreach ($folder in $assets.packageFolders.Keys) {
        $notice = Join-Path $folder $assets.libraries[$dependencyModel].path 'THIRD-PARTY-NOTICES.TXT'
        if (Test-Path -LiteralPath $notice) {
            Copy-Item -LiteralPath $notice -Destination (Join-Path $notices 'Microsoft.Extensions.DependencyModel-THIRD-PARTY-NOTICES.TXT')
            break
        }
    }
    if (!(Test-Path -LiteralPath (Join-Path $notices 'Microsoft.Extensions.DependencyModel-THIRD-PARTY-NOTICES.TXT'))) {
        throw 'The Microsoft.Extensions.DependencyModel notice is missing.'
    }

    [IO.Directory]::Move($bundle, $OutputPath)
    Write-Output $OutputPath
}
finally {
    $resolvedScratch = (Resolve-Path -LiteralPath $scratch).Path
    if (!([IO.File]::GetAttributes($resolvedScratch) -band [IO.FileAttributes]::ReparsePoint) -and
        (Split-Path -Parent $resolvedScratch) -eq $outputParent -and
        (Split-Path -Leaf $resolvedScratch) -match '^\.castar-ios-build-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
