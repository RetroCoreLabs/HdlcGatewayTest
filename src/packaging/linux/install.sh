#!/bin/sh
# Installs HdlcGatewayTest for the current user: the window shows up in the application menu
# with its icon, and both programs are in one folder. Nothing goes outside the home folder and
# nothing needs root. Same layout as RetroTerm's install.sh.
#
#   ~/.local/share/HdlcGatewayTest/hdlc-gateway-tool                  the window
#   ~/.local/share/HdlcGatewayTest/hdlc-loopback                      the command-line tool
#   ~/.local/share/icons/hicolor/<size>/apps/hdlc-gateway-tool.png    the icon, one per size
#   ~/.local/share/icons/hicolor/scalable/apps/hdlc-gateway-tool.svg
#   ~/.local/share/applications/hdlc-gateway-tool.desktop             the menu entry
#
# The serial ports (/dev/ttyACM*, /dev/ttyUSB*) belong to the group "dialout" on most
# distributions; add yourself once with: sudo usermod -aG dialout $USER (then log in again).
#
# To remove it again: ./install.sh --uninstall
set -e

here=$(cd "$(dirname "$0")" && pwd)
data="${XDG_DATA_HOME:-$HOME/.local/share}"
app="$data/HdlcGatewayTest"
icons="$data/icons/hicolor"
menu="$data/applications"

if [ "$1" = "--uninstall" ]; then
    rm -rf "$app"
    rm -f "$menu/hdlc-gateway-tool.desktop"
    find "$icons" -name 'hdlc-gateway-tool.*' -path '*/apps/*' -delete 2>/dev/null || true
    echo "HdlcGatewayTest removed."
    exit 0
fi

mkdir -p "$app" "$menu"
cp "$here/hdlc-gateway-tool" "$here/hdlc-loopback" "$app/"
chmod +x "$app/hdlc-gateway-tool" "$app/hdlc-loopback"

# icons/<size>/hdlc-gateway-tool.*, as laid out by the release workflow.
for dir in "$here"/icons/*/; do
    size=$(basename "$dir")
    mkdir -p "$icons/$size/apps"
    cp "$dir"hdlc-gateway-tool.* "$icons/$size/apps/"
done

# The menu entry needs the full path of the program, which is only known now.
sed "s|__EXEC__|$app/hdlc-gateway-tool|" "$here/hdlc-gateway-tool.desktop" > "$menu/hdlc-gateway-tool.desktop"
chmod +x "$menu/hdlc-gateway-tool.desktop"

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$menu" || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$icons" || true

echo "HdlcGatewayTest installed. Start the window from the application menu, or run $app/hdlc-gateway-tool"
echo "The command-line tool is $app/hdlc-loopback"
