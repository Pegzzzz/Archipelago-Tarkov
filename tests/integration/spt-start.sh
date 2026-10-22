#!/bin/bash
# Start the SPT server in the background for the integration tests. Its console output goes to $1.
#   SPT_DIR     the SPT server folder (SPT_Runtime on 4.1, the SPT folder on 4.0)
#   SPT_DOTNET  the dotnet executable of a .NET runtime SPT supports (default: dotnet)
set -u
LOG=${1:-/tmp/spt.log}
SPT_DIR=${SPT_DIR:?set SPT_DIR to the SPT server folder}
DOTNET=${SPT_DOTNET:-dotnet}
PIDFILE="$SPT_DIR/.integration.pid"
TAILFILE="$SPT_DIR/.integration.tail"
cd "$SPT_DIR" || exit 1
if [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then echo "already running"; exit 0; fi
DLL=SPT.Server.Linux.dll; [ -f "$DLL" ] || DLL=SPT.Server.dll
: > "$LOG"
setsid nohup "$DOTNET" "$DLL" >> "$LOG" 2>&1 < /dev/null &
echo $! > "$PIDFILE"
# SPT 4.1 writes its console to user/logs/spt/spt<date>.log rather than stdout: follow it into $LOG too.
mkdir -p user/logs/spt
TODAY="user/logs/spt/spt$(date +%Y%m%d).log"
touch "$TODAY"
setsid nohup tail -n0 -F "$TODAY" >> "$LOG" 2>/dev/null < /dev/null &
echo $! > "$TAILFILE"
for i in $(seq 1 90); do
  sleep 2
  if grep -q "Server has started\|happy playing\|Started webserver" "$LOG"; then
    sleep 2; echo "started"; exit 0
  fi
  if ! kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then echo "died"; exit 1; fi
done
echo "timeout"; exit 1
