@echo off
REM ========================================
REM Lottery Display POS - App Launcher Setup
REM ========================================
REM Replaces Disable-RunExeAsAdmin.bat.
REM
REM The old script switched off UAC prompts for EVERY program on this PC
REM (ConsentPromptBehaviorAdmin = 0). This one leaves UAC alone. Instead it
REM registers a scheduled task that starts LdPosService.exe with highest
REM privileges, plus a desktop shortcut that triggers that task. Result: the
REM app opens without a UAC prompt, and nothing else on the PC changes.
REM It also puts the UAC setting back to the Windows default in case the
REM old script was run on this PC before.
REM
REM Expected layout:
REM   C:\ProgramData\LotteryDisplayPOS\LdPosService\LdPosService.exe
REM
REM IMPORTANT: Right-click this file and "Run as administrator", logged in
REM as the Windows user who will use the app. That user must be an
REM administrator (the app controls the Windows service).
REM ========================================

echo.
echo ========================================
echo Lottery Display POS - Launcher Setup
echo ========================================
echo.

REM Check if running as Administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator!
    echo Right-click the file and select "Run as administrator"
    pause
    exit /b 1
)

set "APP_DIR=C:\ProgramData\LotteryDisplayPOS\LdPosService"
set "APP_EXE=%APP_DIR%\LdPosService.exe"
set "TASK_NAME=LotteryDisplayPOS\LdPosService"
set "SHORTCUT=%PUBLIC%\Desktop\Lottery Display POS.lnk"

if not exist "%APP_EXE%" (
    echo ERROR: LdPosService.exe not found at:
    echo %APP_EXE%
    echo.
    echo Copy the LotteryDisplayPOS folder to C:\ProgramData first.
    pause
    exit /b 1
)

echo [1/3] Registering scheduled task "%TASK_NAME%"...
REM /RL HIGHEST = run elevated. /IT = only while the user is logged on (it is a desktop app).
REM /SC ONCE with a start time in the past = the task never runs by itself, only when the shortcut triggers it.
schtasks /Create /F /TN "%TASK_NAME%" /TR "\"%APP_EXE%\"" /SC ONCE /ST 00:00 /RL HIGHEST /IT >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Could not create the scheduled task.
    pause
    exit /b 1
)
echo Task registered.

echo.
echo [2/3] Creating the desktop shortcut "Lottery Display POS"...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('%SHORTCUT%'); $s.TargetPath = 'schtasks.exe'; $s.Arguments = '/Run /TN %TASK_NAME%'; $s.WorkingDirectory = '%APP_DIR%'; $s.IconLocation = '%APP_EXE%,0'; $s.WindowStyle = 7; $s.Description = 'Opens the Lottery Display POS setup app'; $s.Save()"
if %errorLevel% neq 0 (
    echo WARNING: Could not create the desktop shortcut. The app can still be started with:
    echo   schtasks /Run /TN %TASK_NAME%
) else (
    echo Shortcut created on the desktop for all users.
)

echo.
echo [3/3] Restoring the normal UAC prompt setting...
REM The old Disable-RunExeAsAdmin.bat set this to 0 (never prompt). 5 is the Windows default.
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 5 /f >nul 2>&1
if %errorLevel% neq 0 (
    echo WARNING: Could not restore the UAC setting. Check it under Control Panel, User Accounts,
    echo "Change User Account Control settings".
) else (
    echo UAC prompts are back to the Windows default. The app is not affected: the shortcut starts it elevated.
)

echo.
echo ========================================
echo SUCCESS!
echo Open the app with the "Lottery Display POS" shortcut on the desktop.
echo Do not right-click LdPosService.exe and "Run as administrator" any more;
echo the shortcut does that for you, without a prompt.
echo ========================================
echo.
pause
