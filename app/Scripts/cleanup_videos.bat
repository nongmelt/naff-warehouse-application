@echo off
powershell.exe -ExecutionPolicy Bypass -File "%~dp0cleanup_videos.ps1"
exit /b %ERRORLEVEL%