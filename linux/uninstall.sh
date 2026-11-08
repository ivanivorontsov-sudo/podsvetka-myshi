#!/usr/bin/env bash
set -euo pipefail
rm -f "${HOME}/.local/bin/podsvetka-myshi"
rm -f "${HOME}/.local/bin/podsvetka-myshi-launch"
rm -f "${HOME}/.local/share/applications/podsvetka-myshi.desktop"
rm -f "${HOME}/.config/autostart/podsvetka-myshi.desktop"
rm -rf "${HOME}/.local/share/podsvetka-myshi"
echo "Linux-версия удалена. Настройки и журнал сохранены."
