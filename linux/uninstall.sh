#!/usr/bin/env bash
set -euo pipefail
if command -v podsvetka-myshi >/dev/null 2>&1; then
  podsvetka-myshi --quit >/dev/null 2>&1 || true
fi
if command -v kwriteconfig6 >/dev/null 2>&1; then
  kwriteconfig6 --file kwinrc --group Plugins --key podsvetka-myshiEnabled false >/dev/null 2>&1 || true
fi
if command -v qdbus6 >/dev/null 2>&1; then
  qdbus6 org.kde.KWin /KWin reconfigure >/dev/null 2>&1 || true
elif command -v qdbus >/dev/null 2>&1; then
  qdbus org.kde.KWin /KWin reconfigure >/dev/null 2>&1 || true
fi
rm -f "${HOME}/.local/bin/podsvetka-myshi"
rm -f "${HOME}/.local/bin/podsvetka-myshi-launch"
rm -f "${HOME}/.local/share/applications/podsvetka-myshi.desktop"
rm -f "${HOME}/.config/autostart/podsvetka-myshi.desktop"
rm -rf "${HOME}/.local/share/podsvetka-myshi"
rm -rf "${HOME}/.local/share/kwin/scripts/podsvetka-myshi"
echo "Linux-версия удалена. Настройки и журнал сохранены."
