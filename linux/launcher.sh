#!/bin/sh
# Menu launcher: preserve startup errors and present them even when GTK import fails.
set -u
PYTHON="/usr/bin/python3"
APP="/usr/lib/podsvetka-myshi/podsvetka_myshi.py"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/podsvetka-myshi"
LOG="$STATE_DIR/launcher.log"
mkdir -p "$STATE_DIR" || exit 1
{
  echo "===== $(date -Is) ====="
  echo "DISPLAY=${DISPLAY:-}"
  echo "WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-}"
  echo "XDG_SESSION_TYPE=${XDG_SESSION_TYPE:-}"
  "$PYTHON" "$APP" "$@"
} >>"$LOG" 2>&1
STATUS=$?
if [ "$STATUS" -eq 0 ]; then
  exit 0
fi
MESSAGE="Не удалось запустить «Подсветку мыши». Код ошибки: $STATUS. Журнал: $LOG"
if command -v zenity >/dev/null 2>&1; then
  zenity --error --title="Подсветка мыши" --text="$MESSAGE" --width=420 2>/dev/null || true
elif command -v kdialog >/dev/null 2>&1; then
  kdialog --error "$MESSAGE" --title "Подсветка мыши" 2>/dev/null || true
elif command -v xmessage >/dev/null 2>&1; then
  xmessage -center -title "Подсветка мыши" "$MESSAGE" 2>/dev/null || true
elif command -v notify-send >/dev/null 2>&1; then
  notify-send --urgency=critical "Подсветка мыши" "$MESSAGE" 2>/dev/null || true
elif command -v x-terminal-emulator >/dev/null 2>&1; then
  x-terminal-emulator -e sh -c 'cat "$1"; printf "\nНажмите Enter для закрытия..."; read _' sh "$LOG" 2>/dev/null || true
else
  echo "$MESSAGE" >&2
fi
exit "$STATUS"
