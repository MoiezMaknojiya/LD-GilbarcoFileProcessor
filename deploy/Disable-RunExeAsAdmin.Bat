@echo off
echo ============================================
echo UAC Settings Modifier - DISABLE PROMPTS
echo ============================================
echo.
echo This script will modify:
echo User Account Control: Behavior of the elevation prompt
echo for administrators in Admin Approval Mode
echo.
echo Setting: Elevate without prompting
echo.
echo ============================================
echo.

REM Check for administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator!
    echo Right-click the file and select "Run as administrator"
    pause
    exit /b 1
)

echo Modifying registry...
echo.

REM Set ConsentPromptBehaviorAdmin to 0 (Elevate without prompting)
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 0 /f

if %errorLevel% equ 0 (
    echo.
    echo ============================================
    echo SUCCESS: UAC setting has been modified!
    echo ============================================
    echo.
    echo UAC prompts are now DISABLED for administrators.
    echo The setting takes effect immediately.
    echo No restart required.
    echo.
) else (
    echo.
    echo ERROR: Failed to modify registry setting.
    echo.
)

pause