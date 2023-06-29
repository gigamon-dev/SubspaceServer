#!/bin/bash

# This is a startup script for running Subspace Server .NET.
# It checks the server's exit code and will automatically restart the server when necessary.
# For example, if the server is to recycle with ?recyclezone or ?shutdown -r.
# 
# The zone directory can be passed to this script as a parameter.
# By default, the location of this script is used.

# Possible exit codes.
E_SHUTDOWN=0
E_RECYCLE=1
E_GENERAL=2
E_OOM=3
E_MODCONF=4
E_MODLOAD=5

if [ $# -eq 0 ]; then
  # Use the directory of this script.
  ZONEDIR=$(dirname "$0")
else
  # Use the directory provided as a parameter.
  ZONEDIR=$1
fi

if [ ! -d $ZONEDIR ]; then
  echo "Directory '$ZONEDIR' not found."
  exit 1
fi

if [ ! -f "$ZONEDIR/bin/SubspaceServer.dll" ]; then
  echo "Directory '$ZONEDIR' is not a valid zone directory ('bin/SubspaceServer.dll' not found)."
  exit 1
fi

if [ ! -d "$ZONEDIR/conf" ]; then
  echo "Directory '$ZONEDIR' is not a valid zone directory ('conf' folder not found)."
  exit 1
fi

cd $ZONEDIR

SCRIPTLOG=log/run-server.log

log ()
{
  echo "`date '+%Y-%m-%d %H:%M:%S'`: $*" | tee -a $SCRIPTLOG
}

CONTINUE=true

while $CONTINUE; do

  log "Starting Subspace Server .NET"

  # Run the server, writing stderr to both stdout and the script log file.
  dotnet ./bin/SubspaceServer.dll 3>&1 1>&2 2>&3 | tee -a $SCRIPTLOG

  EXIT=${PIPESTATUS[0]}

  if [ $EXIT -eq $E_SHUTDOWN ]; then
    MSG="shutdown"
    CONTINUE=false
  elif [ $EXIT -eq $E_RECYCLE ]; then
    MSG="recycle, restarting"
  elif [ $EXIT -eq $E_GENERAL ]; then
    MSG="unknown general error"
    CONTINUE=false
  elif [ $EXIT -eq $E_OOM ]; then
    MSG="out of memory, restarting"
  elif [ $EXIT -eq $E_MODCONF -o $EXIT -eq $E_MODLOAD ]; then
    MSG="error loading modules"
    CONTINUE=false
  else
    MSG="UNKNOWN EXIT CODE: $EXIT"
    CONTINUE=false
  fi

log "Subspace Server .NET exited: $MSG"

done
