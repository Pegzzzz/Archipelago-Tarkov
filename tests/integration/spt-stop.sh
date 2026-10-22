#!/bin/bash
# Stop the SPT server started by spt-start.sh (SIGTERM first, so SPT and the mod shut down cleanly).
SPT_DIR=${SPT_DIR:?set SPT_DIR to the SPT server folder}
PIDFILE="$SPT_DIR/.integration.pid"
TAILFILE="$SPT_DIR/.integration.tail"
if [ -f "$PIDFILE" ]; then
  PID=$(cat "$PIDFILE")
  kill "$PID" 2>/dev/null
  for i in $(seq 1 20); do kill -0 "$PID" 2>/dev/null || break; sleep 0.5; done
  kill -9 "$PID" 2>/dev/null
  rm -f "$PIDFILE"
fi
sleep 1
if [ -f "$TAILFILE" ]; then
  kill "$(cat "$TAILFILE")" 2>/dev/null
  rm -f "$TAILFILE"
fi
echo stopped
