@echo off
REM ---------------------------------------------------------------------------
REM  Loads the exported library data into the hosted SmarterASP.NET database.
REM
REM  Run this from the project folder. It asks for the database password rather
REM  than taking it on the command line, so the password never lands in your
REM  shell history, in a file, or in this repository.
REM
REM  The script it runs replaces the contents of the tables it populates - it is
REM  safe to run twice, and running it does not touch the schema, which the
REM  application creates for itself with EF migrations on first start.
REM ---------------------------------------------------------------------------

setlocal

set SERVER=sql5063.site4now.net
set DATABASE=db_acd077_library
set LOGIN=db_acd077_library_admin
set SCRIPT=sma-library-data.sql

if not exist "%SCRIPT%" (
    echo.
    echo   Cannot find %SCRIPT% in this folder.
    echo   Run this from C:\Users\GT\Desktop\SMA-Library\SMA-LMS
    echo   or regenerate it with:
    echo.
    echo     sqlcmd -S .\SQLEXPRESS -d SMA_LMS -i tools\generate-data-script.sql -o %SCRIPT% -y 0
    echo.
    exit /b 1
)

echo.
echo   Loading %SCRIPT% into %DATABASE% on %SERVER%
echo   You will be asked for the database password.
echo.

REM -b makes sqlcmd exit non-zero on the first error instead of carrying on and
REM leaving the data half-loaded.
sqlcmd -S %SERVER% -d %DATABASE% -U %LOGIN% -i "%SCRIPT%" -b

if errorlevel 1 (
    echo.
    echo   The load failed. Nothing was committed - the script runs in a single
    echo   transaction, so the database is exactly as it was.
    echo.
    exit /b 1
)

echo.
echo   Loaded. Checking what arrived - you will be asked for the password once more.
echo.

sqlcmd -S %SERVER% -d %DATABASE% -U %LOGIN% -Q "SET NOCOUNT ON; SELECT 'books' AS Item, COUNT(*) AS Count FROM Books UNION ALL SELECT 'copies', COUNT(*) FROM BookCopies UNION ALL SELECT 'book tags', COUNT(*) FROM BookRfidTags UNION ALL SELECT 'accounts', COUNT(*) FROM AspNetUsers UNION ALL SELECT 'students', COUNT(*) FROM Students UNION ALL SELECT 'student cards', COUNT(*) FROM StudentRfidTags;"

echo.
echo   Expect: 50 books, 400 copies, 400 book tags, 15 accounts, 13 students, 16 cards.
echo   Then reload https://library.sma-techno.net/
echo.

endlocal
