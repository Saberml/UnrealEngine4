@if not defined TEAMCITY_CAPTURE_ENV ( echo off ) else ( echo on )

setlocal

pushd "%~dp0"

call :MarkStartOfBlock "%~0"

call :MarkStartOfBlock "Setup variables"

set UNREAL_REPO_DIR=%~dp0UnrealEngine
set LINUX_TOOL_CHAIN_ARCHIVE=%UNREAL_REPO_DIR%/v11_clang-5.0.0-centos7.zip
set LINUX_MULTIARCH_ROOT=%UNREAL_REPO_DIR%/ClangToolchain
set LINUX_TOOL_CHAIN_COPY_PATH=%UNREAL_REPO_DIR%/LocalBuilds/Engine/Windows/ClangToolchain/v11_clang-5.0.0-centos7

call :MarkEndOfBlock "Setup variables"

call :MarkStartOfBlock "Download linux toolchain"

powershell "Import-Module BitsTransfer; Start-BitsTransfer -Source http://cdn.unrealengine.com/CrossToolchain_Linux/v11_clang-5.0.0-centos7.zip -Destination %LINUX_TOOL_CHAIN_ARCHIVE%"

call :MarkEndOfBlock "Download linux toolchain"

call :MarkStartOfBlock "Extract linux toolchain"

echo %LINUX_TOOL_CHAIN_ARCHIVE%

echo %UNREAL_REPO_DIR%

powershell "Expand-Archive -LiteralPath %LINUX_TOOL_CHAIN_ARCHIVE% -DestinationPath %LINUX_MULTIARCH_ROOT% -Force"

call :MarkEndOfBlock "Extract linux toolchain"

pushd "%UNREAL_REPO_DIR%"

call :MarkStartOfBlock "Setting up git depencencies"

%UNREAL_REPO_DIR%/Engine/Binaries/DotNET/GitDependencies.exe

call :MarkEndOfBlock "Setting up git depencencies"

call :MarkStartOfBlock "Setting up file associations"

%UNREAL_REPO_DIR%/Engine/Extras/Redist/en-us/UE4PrereqSetup_x64.exe /quiet

call :MarkEndOfBlock "Setting up file associations"

call :MarkStartOfBlock "Generating project files"

powershell "Start-Process -FilePath ./GenerateProjectFiles.bat -Wait -NoNewWindow"

call :MarkEndOfBlock "Generating project files"

call :MarkStartOfBlock "Building the engine"

call "%UNREAL_REPO_DIR%/Engine/Build/BatchFiles/RunUAT.bat" BuildGraph -target="Make Installed Build Win64" -script="%UNREAL_REPO_DIR%/Engine/Build/InstalledEngineBuild.xml" -set:WithDDC="false" -set:WithWin64="true" -set:WithLinux="true" -set:WithWin32="false" -set:WithMac="false" -set:WithAndroid="false" -set:WithIOS="false" -set:WithTVOS="false" -set:WithHTML5="false"

call :MarkEndOfBlock "Building the engine"

call :MarkStartOfBlock "Copy cross compilation toolchain"

xcopy /s /i /q /y "%LINUX_MULTIARCH_ROOT%" LINUX_TOOL_CHAIN_COPY_PATH

call :MarkEndOfBlock "Copy cross compilation toolchain"

popd

call :MarkEndOfBlock "%~0"

popd

echo Unreal Engine fork build completed successfully^!
if not defined TEAMCITY_CAPTURE_ENV pause
exit /b %ERRORLEVEL%

:MarkStartOfBlock
if defined TEAMCITY_CAPTURE_ENV (
    echo ##teamcity[blockOpened name='%~1']
) else (
    echo Starting: %~1
)
exit /b 0

:MarkEndOfBlock
if defined TEAMCITY_CAPTURE_ENV (
    echo ##teamcity[blockClosed name='%~1']
) else (
    echo Finished: %~1
)
exit /b 0