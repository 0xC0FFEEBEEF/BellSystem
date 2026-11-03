@echo off
REM Build SchoolBellClient.exe (run this on Windows, in the client\ folder).
REM Requires Python 3.12+ installed (py launcher). Output: dist\SchoolBellClient.exe

py -m venv build-venv || goto :err
call build-venv\Scripts\activate.bat || goto :err
python -m pip install --upgrade pip || goto :err
python -m pip install -r requirements.txt || goto :err
pyinstaller --onefile --console --name SchoolBellClient schoolbell_client.py || goto :err

echo.
echo Built dist\SchoolBellClient.exe
echo Run it on each classroom PC like:
echo   SchoolBellClient.exe --server http://SERVER:8011 --name "Room 104"
echo (quote the .exe path; put it in Task Scheduler or the Startup folder to start it with the PC)
goto :eof

:err
echo Build failed. Check the messages above.
exit /b 1
