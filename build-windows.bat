@echo off
setlocal
echo ========================================================
echo   Plyph for Windows - Test and Build (Portable + Setup)
echo ========================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-windows.ps1"
if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Build failed with exit code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)
echo.
echo Done! Your built files are in the "dist" folder.
pause
