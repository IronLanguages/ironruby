#!/bin/sh
# ir-server.sh start|stop|restart|status IR_BINARY ARGS...
#
# Manages the warm-start pool for one ir build (Util/warm/README.md). ir.sh calls it with the
# command line it would run, so the pool is started exactly the way a cold `ir` would be:
#
#   IR_SERVER=start ./ir.sh          # IR_SERVER_POOL=K workers (default 2), waits until ready
#   IR_SERVER=1 ./ir.sh -e 'puts 1'  # runs warm; cold when the pool cannot serve it
#   IR_SERVER=status ./ir.sh
#   IR_SERVER=stop ./ir.sh
#
# The pool serves requests whose startup inputs match its own, so start it from the same
# environment the programs will run in (RUBYOPT, RUBYLIB, GEM_*, LANG/LC_*, HOME, TZ, DOTNET_*).
W="$(dirname "$(readlink -f "$0")")"
cmd="$1"
shift
"$W/build.sh" >&2 || exit 1
SOCK="${IR_SERVER_SOCKET:-$("$W/bin/ir-client" --socket-path "$1")}"
PIDFILE="$SOCK.pid"
LOG="${IR_SERVER_LOG:-$SOCK.log}"
POOL="${IR_SERVER_POOL:-2}"

running() {
  [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null
}

case "$cmd" in
  start)
    if running; then
      echo "ir-server: already running (pid $(cat "$PIDFILE")) on $SOCK" >&2
      exit 0
    fi
    touch "$LOG"
    before=$(wc -l < "$LOG")
    "$W/bin/ir-broker" -d -k "$POOL" -s "$SOCK" -p "$PIDFILE" -l "$LOG" -- "$@" || exit 1
    # Wait for the first pool to warm up, so that the next `ir` is a warm one.
    i=0
    while [ $i -lt 300 ]; do
      ready=$(tail -n +"$((before + 1))" "$LOG" | grep -c 'ready after')
      [ "$ready" -ge "$POOL" ] && break
      sleep 0.1
      i=$((i + 1))
    done
    echo "ir-server: $ready of $POOL workers ready on $SOCK (log: $LOG)" >&2
    ;;
  stop)
    if running; then
      kill "$(cat "$PIDFILE")"
      echo "ir-server: stopped" >&2
    else
      echo "ir-server: not running" >&2
    fi
    ;;
  restart)
    "$0" stop "$@"
    sleep 0.2
    exec "$0" start "$@"
    ;;
  status)
    if running; then
      echo "ir-server: running (pid $(cat "$PIDFILE")) on $SOCK, log $LOG" >&2
      tail -n 5 "$LOG" >&2
    else
      echo "ir-server: not running" >&2
      exit 1
    fi
    ;;
  *)
    echo "usage: IR_SERVER=start|stop|restart|status ./ir.sh" >&2
    exit 2
    ;;
esac
