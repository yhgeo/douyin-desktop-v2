@echo off
cd /d "%~dp0"

echo ============================================
echo   Danmaku API capture
echo ============================================
echo.
echo  0) Close the main DouyinDesktop window first.
echo     (Both use the same browser profile, only one can hold it.)
echo.
echo  1) A Douyin window will open - you are already logged in.
echo  2) Find a video WITH danmaku: the player shows a red
echo     round button with the character for "dan" (danmu).
echo     Not every video has it - if the button is missing,
echo     go back and try another one.
echo  3) Let it play for a few seconds.
echo  4) Click the button at the bottom right of that window
echo     to write the report, then close it.
echo.
echo  Report goes to:  data\logs\app-YYYYMMDD.log
echo.
pause

start "" "DouyinDesktop.exe" --diagnose
