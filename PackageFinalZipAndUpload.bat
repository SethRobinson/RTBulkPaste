
REM Delete the existing dist dir if it exists
if exist dist rmdir /s /q dist
REM Create the dist dir
mkdir dist
REM Build the project
dotnet publish .\RTBulkPaste.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
REM Copy the files to the dist dir
copy .\bin\Release\net8.0-windows\win-x64\publish\*.exe dist
REM Copy the readme
copy README.md dist
REM Copy the config.txt
copy config.txt dist
REM Zip it up using 7zip
cd dist
%RT_UTIL%\7z a -tzip ..\RTBulkPasteWindows.zip *
cd ..
call %RT_PROJECTS%\UploadFileToRTsoftSSH.bat RTBulkPasteWindows.zip files
pause