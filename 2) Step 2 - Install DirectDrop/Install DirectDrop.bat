@echo off
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

rem Relaunch elevated so the app can be installed under Program Files.
>nul 2>&1 net session
if errorlevel 1 (
  echo Requesting administrator permission to install DirectDrop...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b 0
)

set "SETUP=%~dp0Setup Files"
rem IMPORTANT: build outside the extracted setup folder so the setup package
rem remains completely unchanged after installation.
set "TEMP_ROOT=%TEMP%\DirectDrop-Install"
set "PUBLISH=%TEMP_ROOT%\publish"
set "INSTALL=%ProgramFiles%\DirectDrop"
set "DOWNLOADS=%USERPROFILE%\Downloads\DirectDrop"

if not exist "%SETUP%\src\DirectDrop.App\DirectDrop.App.csproj" (
  echo.
  echo DirectDrop setup files are missing or incomplete.
  echo Make sure the Setup Files folder stays beside this BAT file.
  pause
  exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo.
  echo .NET 8 SDK was not found.
  echo Please complete Step 1 first, then run this file again.
  echo.
  pause
  exit /b 1
)

for /f "delims=" %%V in ('dotnet --version 2^>nul') do set "SDKVER=%%V"
if not defined SDKVER (
  echo.
  echo Could not detect a working .NET SDK.
  echo Please install the .NET 8 SDK from Step 1.
  pause
  exit /b 1
)

echo.
echo ================================================
echo   DirectDrop - Install
echo ================================================
echo.
echo The setup package will remain unchanged.
echo Building the application in a temporary folder...
echo.

if exist "%TEMP_ROOT%" rmdir /s /q "%TEMP_ROOT%"
mkdir "%TEMP_ROOT%"

dotnet publish "%SETUP%\src\DirectDrop.App\DirectDrop.App.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "%PUBLISH%"

if errorlevel 1 (
  echo.
  echo ================================================
  echo   DirectDrop build failed.
  echo ================================================
  echo Check the error above and make sure Step 1 installed the .NET 8 SDK.
  rmdir /s /q "%TEMP_ROOT%" >nul 2>&1
  pause
  exit /b 1
)

if not exist "%PUBLISH%\DirectDrop.exe" (
  echo.
  echo DirectDrop.exe was not created.
  rmdir /s /q "%TEMP_ROOT%" >nul 2>&1
  pause
  exit /b 1
)

echo.
echo Installing DirectDrop into:
echo %INSTALL%

if exist "%INSTALL%" rmdir /s /q "%INSTALL%"
mkdir "%INSTALL%"
xcopy "%PUBLISH%\*" "%INSTALL%\" /E /I /Y >nul

rem Ensure the embedded website is present in the installed directory.
rem The server serves its UI from AppContext.BaseDirectory\wwwroot.
xcopy "%SETUP%\src\DirectDrop.Server\wwwroot\*" "%INSTALL%\wwwroot\" /E /I /Y >nul

if not exist "%DOWNLOADS%" mkdir "%DOWNLOADS%"

rem Install the uninstall script into the actual installation folder.
if exist "%~dp0uninstall direct drop.bat" copy /Y "%~dp0uninstall direct drop.bat" "%INSTALL%\" >nul

set "STARTMENU=%ProgramData%\Microsoft\Windows\Start Menu\Programs"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws=New-Object -ComObject WScript.Shell; $sc=$ws.CreateShortcut((Join-Path '%STARTMENU%' 'DirectDrop.lnk')); $sc.TargetPath='%INSTALL%\DirectDrop.exe'; $sc.WorkingDirectory='%INSTALL%'; $sc.IconLocation='%INSTALL%\DirectDrop.exe,0'; $sc.Save(); $desktop=[Environment]::GetFolderPath('Desktop'); $sc=$ws.CreateShortcut((Join-Path $desktop 'DirectDrop.lnk')); $sc.TargetPath='%INSTALL%\DirectDrop.exe'; $sc.WorkingDirectory='%INSTALL%'; $sc.IconLocation='%INSTALL%\DirectDrop.exe,0'; $sc.Save()"

rem Remove the temporary build completely.
rmdir /s /q "%TEMP_ROOT%" >nul 2>&1

echo.
echo ================================================
echo   DirectDrop installed successfully!
echo ================================================
echo.
echo Start Menu shortcut: DirectDrop
echo Desktop shortcut:     DirectDrop
echo Install location:     %INSTALL%
echo Receive folder:       %DOWNLOADS%
echo.
echo The extracted setup folder was not modified.
echo.
echo DirectDrop is now launching.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%INSTALL%\DirectDrop.exe' -WorkingDirectory '%INSTALL%'"
exit /b 0
