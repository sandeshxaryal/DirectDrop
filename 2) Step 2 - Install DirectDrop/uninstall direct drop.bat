@echo off
setlocal
>nul 2>&1 net session
if errorlevel 1 (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b 0
)

set "INSTALL=%ProgramFiles%\DirectDrop"
set "STARTMENU=%ProgramData%\Microsoft\Windows\Start Menu\Programs\DirectDrop.lnk"
set "DESKTOP=%USERPROFILE%\Desktop\DirectDrop.lnk"

echo.
echo Uninstalling DirectDrop...
taskkill /F /IM DirectDrop.exe >nul 2>&1
del /f /q "%STARTMENU%" >nul 2>&1
del /f /q "%DESKTOP%" >nul 2>&1
if exist "%INSTALL%" rmdir /s /q "%INSTALL%"

echo.
echo DirectDrop has been uninstalled.
echo Your Downloads\DirectDrop folder was not deleted.
echo.
pause
