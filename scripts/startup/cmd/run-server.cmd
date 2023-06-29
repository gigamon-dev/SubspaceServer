@echo off

REM This is a startup script for running Subspace Server .NET.
REM It checks the server's exit code and will automatically restart the server when necessary.
REM For example, if the server is to recycle with ?recyclezone or ?shutdown -r.

:START

ECHO %DATE% %TIME%: Starting Subspace Server .NET...

bin\SubspaceServer.exe

IF %ERRORLEVEL% EQU 0 GOTO SHUTDOWN
IF %ERRORLEVEL% EQU 1 GOTO RECYCLE
IF %ERRORLEVEL% EQU 2 GOTO GENERAL
IF %ERRORLEVEL% EQU 3 GOTO OOM
IF %ERRORLEVEL% EQU 4 GOTO MODCONF
IF %ERRORLEVEL% EQU 5 GOTO MODLOAD

ECHO %DATE% %TIME%: Subspace Server .NET exited with an unknown exit code: %ERRORLEVEL%.
GOTO END

:SHUTDOWN
ECHO %DATE% %TIME%: Subspace Server .NET exited with shutdown.
GOTO END

:RECYCLE
ECHO %DATE% %TIME%: Subspace Server .NET exited with recycle. Restarting...
GOTO START

:GENERAL
ECHO %DATE% %TIME%: Subspace Server .NET exited with general error.
GOTO END

:OOM
ECHO %DATE% %TIME%: Subspace Server .NET out of memory. Restarting...
GOTO START

:MODCONF
:MODLOAD
ECHO %DATE% %TIME%: Subspace Server .NET cannot start. Error loading modules.
GOTO END

:END
