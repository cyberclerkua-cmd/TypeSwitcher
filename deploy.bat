@echo off
setlocal
rem Deploy the portable build (exe + dict + assets) to a target folder WITHOUT
rem touching user data (data\: config.json, my_words/stopwords/rules, ngram cache,
rem log) and without shipping debug symbols (*.pdb).
rem
rem Folder model:
rem   TypeSwitcher.exe, dict\, assets\ = APPLICATION (overwritten on update)
rem   data\                       = YOUR SETTINGS (created on first run, preserved)
rem
rem ASCII-only on purpose: the console codepage mangles Cyrillic in .bat.
rem
rem Usage:
rem   deploy.bat                       -> %LOCALAPPDATA%\Programs\TypeSwitcher
rem   deploy.bat D:\Apps\TypeSwitcher  -> custom target
rem   deploy.bat /publish              -> rebuild portable first, then deploy
rem   deploy.bat D:\Apps\TypeSwitcher /publish

set "ROOT=%~dp0"
set "SRC=%ROOT%dist\TypeSwitcher"
set "TARGET=%LOCALAPPDATA%\Programs\TypeSwitcher"
set "DOPUBLISH="

:parse
if "%~1"=="" goto afterparse
if /I "%~1"=="/publish" set "DOPUBLISH=1" & shift & goto parse
if /I "%~1"=="-publish" set "DOPUBLISH=1" & shift & goto parse
set "TARGET=%~1"
shift
goto parse
:afterparse

if defined DOPUBLISH (
  echo Rebuilding portable...
   rem Publish settings (single-file, self-contained, JIT) live in the csproj.
  rem Always a CLEAN publish: an incremental one skips the up-to-date bundle and then
  rem does not lay the loose native WPF dlls (*_cor3.dll) into the output folder.
   dotnet clean "%ROOT%src\TypeSwitcher\TypeSwitcher.csproj" -c Release -r win-x64
   if errorlevel 1 (echo clean failed & set "RC=1" & goto end)
   dotnet publish "%ROOT%src\TypeSwitcher\TypeSwitcher.csproj" -c Release -o "%SRC%"
  if errorlevel 1 (echo publish failed & set "RC=1" & goto end)
)

if not exist "%SRC%\TypeSwitcher.exe" (
  echo Not found: %SRC%\TypeSwitcher.exe - build the portable first ^(or run with /publish^).
  set "RC=1" & goto end
)

echo Copying application to %TARGET% ^(data\ and *.pdb are left alone^)...
rem /E - subdirs (dict, assets); /XD data - exclude the settings folder; /XF *.pdb - no
rem symbols; /NFL /NDL /NP - quieter. No /MIR or /PURGE so nothing in the target is deleted.
robocopy "%SRC%" "%TARGET%" /E /XD "%SRC%\data" /XF *.pdb /NFL /NDL /NP >nul
if %ERRORLEVEL% GEQ 8 (echo robocopy error %ERRORLEVEL% & set "RC=1" & goto end)

echo Done. Settings in %TARGET%\data left untouched.
set "RC=0"

:end
echo.
pause
exit /b %RC%
