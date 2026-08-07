@echo off
chcp 65001 >nul
cd /d "%~dp0"
venv\Scripts\pyinstaller --clean --noconfirm build.spec
echo.
echo 打包完成: dist\B站弹幕助手.exe
pause
