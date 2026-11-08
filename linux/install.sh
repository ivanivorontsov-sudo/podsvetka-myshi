#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -eq 0 ]]; then
  echo "Запустите установщик от обычного пользователя, без sudo."
  exit 1
fi
if ! command -v apt-get >/dev/null 2>&1; then
  echo "Этот установщик рассчитан на Debian/Ubuntu и apt."
  exit 1
fi

sudo apt-get update
sudo apt-get install -y python3 python3-gi gir1.2-gtk-3.0 python3-cairo python3-gi-cairo python3-xlib python3-dbus xwayland

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="${HOME}/.local/share/podsvetka-myshi"
BIN="${HOME}/.local/bin"
APPS="${HOME}/.local/share/applications"
KWIN="${HOME}/.local/share/kwin/scripts/podsvetka-myshi"

mkdir -p "${DEST}" "${BIN}" "${APPS}" "${KWIN}/contents/code"
install -m 755 "${ROOT}/linux/podsvetka_myshi.py" "${DEST}/podsvetka_myshi.py"
install -m 755 "${ROOT}/linux/launcher.sh" "${BIN}/podsvetka-myshi-launch"
install -m 644 "${ROOT}/linux/kwin-script/metadata.json" "${KWIN}/metadata.json"
install -m 644 "${ROOT}/linux/kwin-script/contents/code/main.js" "${KWIN}/contents/code/main.js"

cat > "${BIN}/podsvetka-myshi" <<EOF
#!/bin/sh
exec /usr/bin/python3 "${DEST}/podsvetka_myshi.py" "\$@"
EOF
chmod 755 "${BIN}/podsvetka-myshi"

cat > "${APPS}/podsvetka-myshi.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Подсветка мыши
Comment=Подсветка курсора и круги при щелчке
Exec=${BIN}/podsvetka-myshi-launch
Icon=input-mouse
Terminal=false
Categories=Utility;Accessibility;
StartupNotify=false
EOF

echo "Установлено. Для KDE Wayland при первом запуске приложение включит KWin-мост в текущем сеансе."
echo "Запустите из меню приложений или выполните:"
echo "  ${BIN}/podsvetka-myshi"
if [[ ":${PATH}:" != *":${BIN}:"* ]]; then
  echo "Добавьте ~/.local/bin в PATH или используйте полный путь выше."
fi
