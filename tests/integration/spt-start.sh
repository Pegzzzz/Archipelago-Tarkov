#!/bin/bash
# Start the SPT server in the background for the integration tests. Logs to $1.
#   SPT_DIR     the SPT server folder (contains SPT.Server.Linux.dll or SPT.Server.dll)
#   SPT_DOTNET  the dotnet executable of a .NET 9 runtime (default: dotnet)
set -u
LOG=${1:-/tmp/spt.log}
SPT_DIR=${SPT_DIR:?set SPT_DIR to the SPT server folder}
DOTNET=${SPT_DOTNET:-dotnet}
PIDFILE="$SPT_DIR/.integration.pid"
cd "$SPT_DIR" || exit 1
if [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then echo "already running"; exit 0; fi
DLL=SPT.Server.Linux.dll; [ -f "$DLL" ] || DLL=SPT.Server.dll
setsid nohup "$DOTNET" "$DLL" > "$LOG" 2>&1 < /dev/null &
echo $! > "$PIDFILE"
for i in $(seq 1 60); do
  sleep 2
  if grep -q "Server has started\|happy playing\|Started webserver" "$LOG"; then
    sleep 2; echo "started"; exit 0
  fi
  if ! kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then echo "died"; exit 1; fi
done
echo "timeout"; exit 1
