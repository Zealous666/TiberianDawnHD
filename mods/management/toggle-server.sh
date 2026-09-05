#!/bin/zsh
PORT=7890
PIDFILE="$(cd "$(dirname "$0")" && pwd)/.server.pid"

if [ -f "$PIDFILE" ]; then
  PID=$(cat "$PIDFILE")
  kill "$PID" 2>/dev/null
  rm -f "$PIDFILE"
  echo "Server gestoppt (PID $PID)."
else
  cd "$(dirname "$0")"
  python3 serve.py &>/dev/null &
  echo $! > "$PIDFILE"
  echo "Server gestartet auf Port $PORT."
  echo ""
  echo "→ http://localhost:$PORT/kanban-board.html"
fi
