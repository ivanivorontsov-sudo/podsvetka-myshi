/*
 * KWin bridge for Подсветка мыши.
 * The compositor provides global pointer coordinates; the app renders the
 * non-interactive glow through an XWayland transparent surface.
 */
var lastSentAt = 0;

function sendCursorPosition() {
    var now = Date.now();
    // Cap IPC to about 60 updates/second; never block KWin with per-device-event traffic.
    if (now - lastSentAt < 16) {
        return;
    }
    lastSentAt = now;

    var point = workspace.cursorPos;
    callDBus(
        "org.podsvetka.Myshi",
        "/org/podsvetka/Myshi",
        "org.podsvetka.Myshi",
        "SetCursorPosition",
        Math.round(point.x),
        Math.round(point.y)
    );
}

workspace.cursorPosChanged.connect(sendCursorPosition);
sendCursorPosition();
