"""Circular sleep timer for Windows.

Counts down on a ring; at zero it shuts down (default), sleeps or restarts.
Shutdown/restart use `shutdown /s|/r /t 0` (plus /f if "force" is on).
- Two ways to set it: a duration, or a clock time ("At time" - a time that has
  already passed today means tomorrow, so 01:00 at 22:30 works as expected).
- Start / pause / resume / cancel; time can be adjusted with the wheel, also while running.
- Mouse wheel over the ring: see WHEEL_MIN below.
- Minimize hides to the tray; last duration, mode and force setting are remembered.
- While a timer runs, Windows is kept from idle-sleeping so it can finish.
- Optional "Start with Windows" (launches minimized to tray).
"""
import ctypes
import math
import os
import subprocess
import sys
import threading
import time
import winreg
from datetime import datetime, timedelta

from PySide6.QtCore import (QAbstractAnimation, QEasingCurve, QEvent, QPoint,
                            QPointF, QRectF, QSettings, QSize, Qt, QTime, QTimer,
                            QVariantAnimation, Signal)
from PySide6.QtGui import (QAction, QColor, QConicalGradient, QFont, QFontMetrics,
                           QIcon, QPainter, QPainterPath, QPen, QPixmap)
from PySide6.QtWidgets import (QAbstractButton, QAbstractSpinBox, QApplication,
                               QButtonGroup, QDialog, QGridLayout, QHBoxLayout,
                               QGraphicsOpacityEffect, QLabel, QLayout, QMenu, QPushButton,
                               QSizePolicy,
                               QSystemTrayIcon, QTimeEdit, QVBoxLayout, QWidget)
from PySide6.QtNetwork import QLocalServer, QLocalSocket

# Fixed-size window: width is set here, height follows the content (no user resizing).
WIN_W, MARGIN = 390, 22
H, H_BIG = 38, 44  # every small control is H tall, Start/Cancel are H_BIG

IDLE, RUNNING, PAUSED = "idle", "running", "paused"
MIN_SET, MAX_SET, MIN_ACTIVE = 60, 24 * 3600, 10
DEFAULT_SECS = 30 * 60

# Minutes per wheel notch. Fine -> coarse: Alt < plain < Shift < Ctrl < Ctrl+Shift.
# (Same numbers as the buttons, so there's only one scale to remember.)
WHEEL_MIN = {"alt": 1, "plain": 5, "shift": 15, "ctrl": 30, "ctrl+shift": 60}
WHEEL_HINT = (f"Scroll \u00b1{WHEEL_MIN['plain']} \u00b7 Alt \u00b1{WHEEL_MIN['alt']} \u00b7 "
              f"Shift \u00b1{WHEEL_MIN['shift']} \u00b7 Ctrl \u00b1{WHEEL_MIN['ctrl']} \u00b7 "
              f"Ctrl+Shift \u00b1{WHEEL_MIN['ctrl+shift']} min")

ACTIONS = {
    "shutdown": {"label": "Shut down", "verb": "shut down", "would": "Would shut down at",
                 "at": "Shutdown at", "run_title": "SHUTTING DOWN IN", "clock_title": "SHUT DOWN AT",
                 "final_title": "GOODNIGHT", "final_sub": "Shutting down..."},
    "sleep": {"label": "Sleep", "verb": "go to sleep", "would": "Would sleep at",
              "at": "Sleep at", "run_title": "SLEEPING IN", "clock_title": "SLEEP AT",
              "final_title": "GOODNIGHT", "final_sub": "Going to sleep..."},
    "restart": {"label": "Restart", "verb": "restart", "would": "Would restart at",
                "at": "Restart at", "run_title": "RESTARTING IN", "clock_title": "RESTART AT",
                "final_title": "SEE YOU SOON", "final_sub": "Restarting..."},
}

# Matcha palette
BG, SURFACE, SURFACE2, BORDER = "#0D1210", "#151B18", "#19211D", "#28322D"
MATCHA, MATCHA_HI, MATCHA_DOT = "#9FB982", "#B1C995", "#D1DEBB"
TEXT, SECOND, FAINT, DISABLED = "#E7ECE8", "#89958D", "#647068", "#3B443F"
TRACK = "#26302A"
DANGER, DANGER_HI = "#D9776A", "#E8A093"  # only for subtract-hover and the last 10 seconds
MUTED = SECOND
FAMILIES = ["Segoe UI Variable Display", "Segoe UI"]

STYLE = """
QWidget { background: %(bg)s; color: %(text)s; font-family: "Segoe UI Variable Text", "Segoe UI"; font-size: 10pt; }
QLabel { background: transparent; }
QLabel#dlgTitle { font-size: 13pt; color: %(text)s; }
QLabel#dlgBody { color: %(second)s; }
QLabel#hint { color: %(second)s; }
QLabel#hint:disabled { color: %(disabled)s; }
QLabel#scrollhint { color: %(faint)s; font-size: 8pt; }
QToolTip { background: %(surface)s; color: %(text)s; border: 1px solid %(border)s; padding: 6px 8px; }

QPushButton { background: %(surface)s; border: 1px solid %(border)s; border-radius: 10px; padding: 10px 0; color: %(second)s; }
QPushButton:hover { background: %(surface2)s; border-color: %(disabled)s; color: %(text)s; }
QPushButton:pressed { background: %(bg)s; }
QPushButton:disabled { background: %(bg)s; border-color: %(border)s; color: %(disabled)s; }

QPushButton[seg="true"] { padding: 8px 0; }
QPushButton[seg="true"]:checked { background: %(surface2)s; border-color: rgba(159,185,130,0.45); color: %(text)s; }
QPushButton[seg="true"]:checked:disabled { background: %(surface)s; border-color: %(border)s; color: %(faint)s; }

QPushButton#primary { background: %(matcha)s; border-color: %(matcha)s; color: %(bg)s; font-size: 11pt; font-weight: 600; }
QPushButton#primary:hover { background: %(matcha_hi)s; border-color: %(matcha_hi)s; color: %(bg)s; }
QPushButton#primary:pressed { background: #8AA46F; border-color: #8AA46F; }
QPushButton#primary:disabled { background: %(surface2)s; border-color: %(border)s; color: %(disabled)s; }
QPushButton#danger { color: %(danger_hi)s; }
QPushButton#danger:hover { background: rgba(217,119,106,0.14); border-color: %(danger)s; color: %(danger_hi)s; }

QTimeEdit { background: %(surface)s; border: 1px solid %(border)s; border-radius: 10px; padding: 7px 10px; color: %(text)s; font-weight: bold; selection-background-color: %(matcha)s; selection-color: %(bg)s; }
QTimeEdit:hover { border-color: %(disabled)s; }
QTimeEdit:focus { border-color: %(matcha)s; }
QTimeEdit:disabled { background: %(bg)s; border-color: %(border)s; color: %(disabled)s; }

QMenu { background: %(surface)s; border: 1px solid %(border)s; padding: 4px; }
QMenu::item { padding: 8px 26px; border-radius: 6px; }
QMenu::item:selected { background: %(surface2)s; color: %(matcha_hi)s; }
QMenu::item:disabled { color: %(disabled)s; }
QMenu::separator { height: 1px; background: %(border)s; margin: 4px 8px; }
""" % dict(bg=BG, surface=SURFACE, surface2=SURFACE2, border=BORDER, matcha=MATCHA,
           matcha_hi=MATCHA_HI, text=TEXT, second=SECOND, faint=FAINT,
           disabled=DISABLED, danger=DANGER, danger_hi=DANGER_HI)
SERVER_NAME = "SleepTimerSingleInstance"

# ---- helpers ------------------------------------------------------------
def keep_awake(on):
    """Stop Windows idling into sleep while the timer runs (display may still sleep)."""
    try:
        ES_CONTINUOUS, ES_SYSTEM_REQUIRED = 0x80000000, 0x00000001
        ctypes.windll.kernel32.SetThreadExecutionState(
            ES_CONTINUOUS | (ES_SYSTEM_REQUIRED if on else 0))
    except Exception:
        pass



def is_already_running() -> bool:
    """Try to connect to an existing instance. Returns True if one is running."""
    socket = QLocalSocket()
    socket.connectToServer(SERVER_NAME)
    if socket.waitForConnected(300):
        # Tell the existing instance to show itself
        socket.write(b"show")
        socket.flush()
        socket.waitForBytesWritten(300)
        socket.disconnectFromServer()
        return True
    return False


def start_single_instance_server(window):
    """Start a local server so new instances can ask us to show the window."""
    server = QLocalServer()
    # Remove any leftover server from a previous crash
    QLocalServer.removeServer(SERVER_NAME)
    if server.listen(SERVER_NAME):
        def on_new_connection():
            sock = server.nextPendingConnection()
            if sock:
                sock.waitForReadyRead(500)
                window.show_window()          # ← this is the important call
                sock.disconnectFromServer()
        server.newConnection.connect(on_new_connection)
        # Keep a reference so it isn’t garbage-collected
        window._single_instance_server = server

def dark_titlebar(widget):
    """Dark title bar on Windows 10/11 so the window frame matches the UI."""
    try:
        val = ctypes.c_int(1)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            int(widget.winId()), 20, ctypes.byref(val), ctypes.sizeof(val))
    except Exception:
        pass


def font(size, weight=QFont.Normal, spacing=0):
    f = QFont()
    f.setFamilies(FAMILIES)
    f.setPointSizeF(size)
    f.setWeight(weight)
    if spacing:
        f.setLetterSpacing(QFont.AbsoluteSpacing, spacing)
    return f


def mix(a, b, t):
    a, b = QColor(a), QColor(b)
    return QColor(int(a.red() + (b.red() - a.red()) * t),
                  int(a.green() + (b.green() - a.green()) * t),
                  int(a.blue() + (b.blue() - a.blue()) * t))


def fmt(secs):
    secs = max(0, math.ceil(secs))
    h, rem = divmod(secs, 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m:02d}:{s:02d}"


def human(secs):
    h, m = divmod(max(1, math.ceil(secs / 60)), 60)
    return f"{h}h {m:02d}m" if h else f"{m}m"


def next_occurrence(clock_min):
    """Next moment the wall clock shows HH:MM. Already passed today -> tomorrow."""
    now = datetime.now()
    t = now.replace(hour=clock_min // 60, minute=clock_min % 60, second=0, microsecond=0)
    if t <= now:
        t += timedelta(days=1)
    return t


def make_icon(fraction, mode, urgent):
    """Tray/window icon: idle = empty ring, running = arc, paused = grey, last 10s = red."""
    pm = QPixmap(64, 64)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    rect = QRectF(9, 9, 46, 46)
    p.setPen(QPen(QColor("#4A554E"), 9))
    p.drawEllipse(rect)
    if mode != IDLE and fraction > 0.001:
        color = DANGER if urgent else (SECOND if mode == PAUSED else MATCHA)
        pen = QPen(QColor(color), 9)
        pen.setCapStyle(Qt.FlatCap)
        p.setPen(pen)
        p.drawArc(rect, 90 * 16, int(-fraction * 360 * 16))
    p.end()
    return QIcon(pm)


def get_app_path():
    """Path to the running executable (or script while developing)."""
    if getattr(sys, "frozen", False):
        return sys.executable
    return os.path.abspath(sys.argv[0])


def set_startup(enabled: bool):
    """Add / remove the app from HKCU Run key. Always launches with --tray."""
    name = "SleepTimer"
    key_path = r"Software\Microsoft\Windows\CurrentVersion\Run"
    try:
        key = winreg.OpenKey(
            winreg.HKEY_CURRENT_USER, key_path, 0,
            winreg.KEY_SET_VALUE | winreg.KEY_QUERY_VALUE
        )
        if enabled:
            value = f'"{get_app_path()}" --tray'
            winreg.SetValueEx(key, name, 0, winreg.REG_SZ, value)
        else:
            try:
                winreg.DeleteValue(key, name)
            except FileNotFoundError:
                pass
        winreg.CloseKey(key)
    except Exception:
        pass


# ---- widgets --------------------------------------------------------------
class Switch(QAbstractButton):
    """Small animated on/off toggle."""

    def __init__(self, checked=True):
        super().__init__()
        self.setCheckable(True)
        self.setChecked(checked)
        self.setCursor(Qt.PointingHandCursor)
        self.setFixedSize(44, 26)
        self._pos = 1.0 if checked else 0.0
        self._anim = QVariantAnimation(self)
        self._anim.setDuration(160)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)
        self._anim.valueChanged.connect(self._on_anim)
        self.toggled.connect(self._animate)

    def _animate(self, on):
        self._anim.stop()
        self._anim.setStartValue(float(self._pos))
        self._anim.setEndValue(1.0 if on else 0.0)
        self._anim.start()

    def _on_anim(self, v):
        self._pos = float(v)
        self.update()

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.setPen(Qt.NoPen)
        if not self.isEnabled():
            p.setOpacity(0.35)
        r = QRectF(self.rect()).adjusted(1, 1, -1, -1)
        p.setBrush(mix(BORDER, MATCHA, self._pos))
        p.drawRoundedRect(r, r.height() / 2, r.height() / 2)
        d = r.height() - 6
        x = r.x() + 3 + self._pos * (r.width() - d - 6)
        p.setBrush(mix(SECOND, BG, self._pos))  # grey knob off, dark knob on matcha
        p.drawEllipse(QRectF(x, r.y() + 3, d, d))


class ActionPicker(QWidget):
    """Subtle chip showing what happens at zero. Click: it expands into all options.
    Click an option (or the chip, or anywhere else) and it collapses again."""

    changed = Signal(str)

    def __init__(self, options, current):
        super().__init__()
        self.options = list(options)  # [(key, label), ...]
        self.current = current
        self._open, self._p, self._hover = False, 0.0, -1
        fm = QFontMetrics(font(10))
        self._tw = [fm.horizontalAdvance(label) for _, label in self.options]
        self._seg = [tw + 32 for tw in self._tw]
        self._ew = sum(self._seg) + 8  # expanded pill width
        self.setFixedSize(self._ew + 2, 32)
        self.setCursor(Qt.PointingHandCursor)
        self.setMouseTracking(True)
        self._anim = QVariantAnimation(self)
        self._anim.setDuration(220)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)
        self._anim.valueChanged.connect(self._on_anim)

    # -- state
    def set_current(self, key):
        self.current = key
        self.update()

    def _index(self):
        return [k for k, _ in self.options].index(self.current)

    def _set_open(self, on):
        if on == self._open:
            return
        self._open = on
        app = QApplication.instance()
        if on:
            app.installEventFilter(self)  # so a click anywhere else collapses it
        else:
            app.removeEventFilter(self)
        self._anim.stop()
        self._anim.setStartValue(float(self._p))
        self._anim.setEndValue(1.0 if on else 0.0)
        self._anim.start()

    def _on_anim(self, v):
        self._p = float(v)
        self.update()

    # -- geometry
    def _geom(self):
        cw = self._tw[self._index()] + 16 + 30  # collapsed: label + chevron
        w = cw + (self._ew - cw) * self._p
        return (self.width() - w) / 2, w

    def _seg_at(self, x):
        x0, _ = self._geom()
        sx = x0 + 4
        for i, sw in enumerate(self._seg):
            if sx <= x < sx + sw:
                return i
            sx += sw
        return -1

    def _in_pill(self, x):
        x0, w = self._geom()
        return x0 <= x <= x0 + w

    # -- events
    def mouseMoveEvent(self, e):
        x = e.position().x()
        h = self._seg_at(x) if self._open else (0 if self._in_pill(x) else -1)
        if h != self._hover:
            self._hover = h
            self.update()

    def leaveEvent(self, e):
        self._hover = -1
        self.update()
        super().leaveEvent(e)

    def mousePressEvent(self, e):
        if e.button() != Qt.LeftButton or not self.isEnabled():
            return
        x = e.position().x()
        if not self._open:
            if self._in_pill(x):
                self._set_open(True)
            return
        i = self._seg_at(x)
        if i >= 0 and self.options[i][0] != self.current:
            self.current = self.options[i][0]
            self.changed.emit(self.current)
        self._set_open(False)

    def eventFilter(self, obj, ev):
        if self._open and ev.type() == QEvent.MouseButtonPress:
            if not self.rect().contains(self.mapFromGlobal(ev.globalPosition().toPoint())):
                self._set_open(False)
        return False

    def changeEvent(self, e):
        if e.type() == QEvent.EnabledChange:
            if not self.isEnabled():
                self._set_open(False)
            self.update()
        super().changeEvent(e)

    # -- painting
    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        H, on = self.height(), self.isEnabled()
        x0, w = self._geom()
        rect = QRectF(x0 + 0.5, 0.5, w - 1, H - 1)
        hover_chip = on and not self._open and self._hover >= 0

        fill = QColor(SURFACE)
        fill.setAlphaF(self._p)
        p.setBrush(fill)
        p.setPen(QPen(mix(BORDER, DISABLED, 0.8) if hover_chip else QColor(BORDER), 1))
        p.drawRoundedRect(rect, H / 2, H / 2)

        # collapsed: "Shut down  v"
        a1 = 1.0 - min(1.0, self._p * 2)
        if a1 > 0:
            col = QColor(DISABLED if not on else (TEXT if hover_chip else SECOND))
            col.setAlphaF(a1)
            p.setFont(font(10))
            p.setPen(col)
            p.drawText(QRectF(x0 + 16, 0, w - 16, H), Qt.AlignVCenter | Qt.AlignLeft,
                       dict(self.options)[self.current])
            pen = QPen(col, 1.6)
            pen.setCapStyle(Qt.RoundCap)
            pen.setJoinStyle(Qt.RoundJoin)
            p.setPen(pen)
            cx, cy = x0 + w - 18, H / 2
            p.drawPolyline([QPointF(cx - 4, cy - 2), QPointF(cx, cy + 2), QPointF(cx + 4, cy - 2)])

        # expanded: three options, current one softly highlighted
        a2 = max(0.0, min(1.0, self._p * 2 - 1))
        if a2 > 0:
            clip = QPainterPath()
            clip.addRoundedRect(rect, H / 2, H / 2)
            p.setClipPath(clip)
            p.setFont(font(10))
            sx = x0 + 4
            for i, (key, label) in enumerate(self.options):
                seg = QRectF(sx, 4, self._seg[i], H - 8)
                if key == self.current:
                    hi = QColor(MATCHA)
                    hi.setAlphaF(0.16 * a2)
                    p.setPen(Qt.NoPen)
                    p.setBrush(hi)
                    p.drawRoundedRect(seg, (H - 8) / 2, (H - 8) / 2)
                col = QColor(MATCHA_HI if key == self.current else (TEXT if i == self._hover else SECOND))
                col.setAlphaF(a2)
                p.setPen(col)
                p.drawText(seg, Qt.AlignCenter, label)
                sx += self._seg[i]


class Ring(QWidget):
    wheel = Signal(int)  # seconds to add (negative = subtract)

    def __init__(self):
        super().__init__()
        self.setMinimumSize(320, 320)
        self.fraction, self.shown = 1.0, 1.0
        self.mode, self.urgent = IDLE, False
        self.title, self.time_text, self.sub = "", "", ""
        self._wheel_acc = 0

        # smooth sweep when the ring value jumps (add/subtract time, cancel)
        self._sweep = QVariantAnimation(self)
        self._sweep.setDuration(380)
        self._sweep.setEasingCurve(QEasingCurve.OutCubic)
        self._sweep.valueChanged.connect(self._on_sweep)

    def set_fraction(self, f, animate=False):
        self.fraction = f
        if animate:
            self._sweep.stop()
            self._sweep.setStartValue(float(self.shown))
            self._sweep.setEndValue(float(f))
            self._sweep.start()
        elif self._sweep.state() != QAbstractAnimation.Running:
            self.shown = f

    def _on_sweep(self, v):
        self.shown = float(v)
        self.update()

    def wheelEvent(self, e):
        # Windows/Qt may report Alt- or Shift-scroll on the horizontal axis
        d = e.angleDelta()
        self._wheel_acc += d.y() or d.x()
        steps = int(self._wheel_acc / 120)  # one notch = 120; touchpads accumulate
        if steps:
            self._wheel_acc -= steps * 120
            m = e.modifiers()
            ctrl, shift = bool(m & Qt.ControlModifier), bool(m & Qt.ShiftModifier)
            if ctrl and shift:
                minutes = WHEEL_MIN["ctrl+shift"]
            elif ctrl:
                minutes = WHEEL_MIN["ctrl"]
            elif shift:
                minutes = WHEEL_MIN["shift"]
            elif m & Qt.AltModifier:
                minutes = WHEEL_MIN["alt"]
            else:
                minutes = WHEEL_MIN["plain"]
            self.wheel.emit(steps * minutes * 60)
        e.accept()

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        side = min(self.width(), self.height()) - 60
        rect = QRectF((self.width() - side) / 2, (self.height() - side) / 2, side, side)
        c = rect.center()

        p.setPen(QPen(QColor(TRACK), 16))
        p.drawEllipse(rect)

        if self.urgent:
            c1, c2 = QColor(DANGER), QColor(DANGER_HI)
        elif self.mode == PAUSED:
            c1, c2 = QColor(FAINT), QColor(SECOND)
        else:
            c1, c2 = QColor(MATCHA), QColor(MATCHA_HI)

        if self.shown > 0.001:
            g = QConicalGradient(c, 90)
            g.setColorAt(0, c1)
            g.setColorAt(0.5, c2)
            g.setColorAt(1, c1)
            pen = QPen(g, 16)
            pen.setCapStyle(Qt.RoundCap)
            p.setPen(pen)
            p.drawArc(rect, 90 * 16, int(-self.shown * 360 * 16))
            a = math.radians(90 - self.shown * 360)
            r = side / 2
            # small dot at the arc's end: not while idle, and it fades in over the
            # first ~8% of the countdown so a full ring stays clean
            fade = 0.0 if self.mode == IDLE else min(1.0, (1.0 - self.shown) / 0.08)
            if fade > 0:
                pt = QPointF(c.x() + r * math.cos(a), c.y() - r * math.sin(a))
                halo, dot = QColor(BG), QColor(MATCHA_DOT)
                halo.setAlphaF(0.5 * fade)  # soft dark ring keeps it readable on the arc
                dot.setAlphaF(fade)
                p.setPen(Qt.NoPen)
                p.setBrush(halo)
                p.drawEllipse(pt, 7.0, 7.0)
                p.setBrush(dot)
                p.drawEllipse(pt, 4.0, 4.0)

        p.setPen(QColor(MUTED))
        p.setFont(font(9, QFont.Medium, 2))
        p.drawText(QRectF(rect.x(), c.y() - side * 0.27, side, 24), Qt.AlignCenter, self.title)

        # the number - the only bold text
        p.setPen(QColor(TEXT))
        p.setFont(font(36, QFont.Bold))
        p.drawText(QRectF(rect.x(), c.y() - 36, side, 72), Qt.AlignCenter, self.time_text)

        p.setPen(QColor(MUTED))
        p.setFont(font(10))
        p.drawText(QRectF(rect.x(), c.y() + side * 0.2, side, 24), Qt.AlignCenter, self.sub)


class ConfirmDialog(QDialog):
    """Themed replacement for the stock message box."""

    def __init__(self, parent, verb="shut down"):
        super().__init__(parent)
        self.setWindowTitle("Timer active")
        self.setModal(True)
        self.setWindowFlag(Qt.WindowContextHelpButtonHint, False)
        self.setMinimumWidth(460)

        title = QLabel("Cancel the timer?")
        title.setObjectName("dlgTitle")
        body = QLabel(f"Closing the app stops the timer, so your PC won't {verb}.")
        body.setObjectName("dlgBody")
        body.setWordWrap(True)

        keep = QPushButton("Keep timer")
        keep.setObjectName("primary")
        keep.setDefault(True)
        keep.setMinimumWidth(130)
        keep.setCursor(Qt.PointingHandCursor)
        keep.clicked.connect(self.reject)
        close = QPushButton("Close anyway")
        close.setObjectName("danger")
        close.setMinimumWidth(130)
        close.setCursor(Qt.PointingHandCursor)
        close.clicked.connect(self.accept)

        row = QHBoxLayout()
        row.setSpacing(10)
        row.addStretch(1)
        row.addWidget(close)
        row.addWidget(keep)

        lay = QVBoxLayout(self)
        lay.setContentsMargins(24, 22, 24, 20)
        lay.setSpacing(8)
        lay.addWidget(title)
        lay.addWidget(body)
        lay.addSpacing(16)
        lay.addLayout(row)

    def showEvent(self, e):
        super().showEvent(e)
        dark_titlebar(self)


# ---- main window -----------------------------------------------------------
class ModeRow(QWidget):
    """[Duration] [At time] [HH:mm].
    The two buttons are centered while the time box is hidden; choosing "At time"
    slides them left (same column width) and fades the time box in on the right."""

    def __init__(self, left, right, edit, gap=8):
        super().__init__()
        self.left, self.right, self.edit, self.gap = left, right, edit, gap
        for w in (left, right, edit):
            w.setParent(self)
        self.fx = QGraphicsOpacityEffect(edit)
        edit.setGraphicsEffect(self.fx)
        self._t = 0.0
        self.setFixedHeight(H)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self._anim = QVariantAnimation(self)
        self._anim.setDuration(240)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)
        self._anim.valueChanged.connect(self._on_anim)
        self._place()

    def sizeHint(self):
        return QSize(WIN_W - 2 * MARGIN, H)

    def set_expanded(self, on, animate=True):
        target = 1.0 if on else 0.0
        self._anim.stop()
        if animate:
            self._anim.setStartValue(float(self._t))
            self._anim.setEndValue(target)
            self._anim.start()
        else:
            self._t = target
            self._place()

    def _on_anim(self, v):
        self._t = float(v)
        self._place()

    def resizeEvent(self, e):
        super().resizeEvent(e)
        self._place()

    def _place(self):
        W = self.width()
        if W <= 0:
            return
        w = (W - 2 * self.gap) / 3  # one column
        x = (W - (2 * w + self.gap)) / 2 * (1 - self._t)  # centered -> flush left
        self.left.setGeometry(round(x), 0, round(w), H)
        self.right.setGeometry(round(x + w + self.gap), 0, round(w), H)
        self.edit.setGeometry(round(2 * (w + self.gap)), 0, round(w), H)
        self.fx.setOpacity(self._t)
        self.edit.setVisible(self._t > 0.01)


class SleepTimer(QWidget):
    resumed = Signal()  # emitted (from a worker thread) after the PC wakes from sleep

    def __init__(self):
        super().__init__()
        self.setWindowTitle("Sleep Timer")
        self.setWindowFlags(Qt.Window | Qt.CustomizeWindowHint | Qt.WindowTitleHint
                            | Qt.WindowSystemMenuHint | Qt.WindowMinimizeButtonHint
                            | Qt.WindowCloseButtonHint)  # no maximize; size is fixed
        self.settings = QSettings("SleepTimer", "SleepTimer")
        self.mode = IDLE
        self.duration = self._load_duration()
        self.total = self.remaining = self.duration
        self.end = 0.0
        self.clock_mode = str(self.settings.value("clock_mode", "false")).lower() == "true"
        self.clock_touched = False
        self.clock_min = self.default_clock()
        force = str(self.settings.value("force", "true")).lower() == "true"
        act = str(self.settings.value("action", "shutdown"))
        self.action = act if act in ACTIONS else "shutdown"
        self.start_with_windows = str(self.settings.value("start_with_windows", "false")).lower() == "true"

        def flat(w, h=H):
            """Uniform height, width comes purely from the layout columns."""
            w.setFixedHeight(h)
            w.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Fixed)
            return w

        def grid3():
            g = QGridLayout()
            g.setHorizontalSpacing(8)
            g.setVerticalSpacing(10)
            for c in range(3):
                g.setColumnStretch(c, 1)
            return g

        hint = QLabel(WHEEL_HINT)
        hint.setObjectName("scrollhint")
        hint.setAlignment(Qt.AlignCenter)
        hint.setFixedHeight(16)
        hint.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Fixed)  # never widens the window

        self.ring = Ring()
        self.ring.setFixedSize(WIN_W - 2 * MARGIN, 336)
        self.ring.wheel.connect(self.add)

        # subtle action chip under the ring (expands into Shut down / Sleep / Restart)
        self.picker = ActionPicker([(k, m["label"]) for k, m in ACTIONS.items()], self.action)
        self.picker.setToolTip("What happens when the timer ends")
        self.picker.changed.connect(self.set_action)

        # [Duration] [At time] [ HH:mm ] on a 3-column grid

        self.btn_dur = flat(QPushButton("Duration"))
        self.btn_clock = flat(QPushButton("At time"))
        group = QButtonGroup(self)
        group.setExclusive(True)
        for b in (self.btn_dur, self.btn_clock):
            b.setCheckable(True)
            b.setProperty("seg", True)
            b.setCursor(Qt.PointingHandCursor)
            group.addButton(b)
        self.btn_dur.clicked.connect(lambda: self.set_mode(False))
        self.btn_clock.clicked.connect(lambda: self.set_mode(True))
        self.time_edit = flat(QTimeEdit())
        self.time_edit.setDisplayFormat("HH:mm")
        self.time_edit.setButtonSymbols(QAbstractSpinBox.NoButtons)
        self.time_edit.setAlignment(Qt.AlignCenter)
        self.time_edit.setToolTip(
            "Type a time, e.g. 01:00.\nIf it has already passed today, it means tomorrow.")
        self.time_edit.timeChanged.connect(self.on_time_edit)

        self.mode_row = ModeRow(self.btn_dur, self.btn_clock, self.time_edit)

        # Start (2 columns) + Cancel (1 column)
        self.primary = flat(QPushButton("Start"), H_BIG)
        self.primary.setObjectName("primary")
        self.primary.setCursor(Qt.PointingHandCursor)
        self.primary.clicked.connect(self.start_pause)
        self.cancel_btn = flat(QPushButton("Cancel"), H_BIG)
        self.cancel_btn.setCursor(Qt.PointingHandCursor)
        self.cancel_btn.clicked.connect(self.cancel)
        bottom = grid3()
        bottom.addWidget(self.primary, 0, 0, 1, 2)
        bottom.addWidget(self.cancel_btn, 0, 2)

        self.force_sw = Switch(force)
        self.force_sw.toggled.connect(lambda on: self.settings.setValue("force", on))
        self.force_lbl = QLabel("Force close apps that block shutdown")
        self.force_lbl.setObjectName("hint")
        self.force_lbl.setCursor(Qt.PointingHandCursor)
        self.force_lbl.setToolTip("Adds /f: apps with unsaved work are closed without asking.\n(Shut down and restart only.)")
        self.force_lbl.mousePressEvent = lambda e: self.force_sw.toggle() if self.force_sw.isEnabled() else None
        force_row = QHBoxLayout()
        force_row.addWidget(self.force_lbl, 1)
        force_row.addWidget(self.force_sw)

        self.startup_sw = Switch(self.start_with_windows)
        self.startup_sw.toggled.connect(self.on_startup_toggled)
        self.startup_lbl = QLabel("Start with Windows (tray)")
        self.startup_lbl.setObjectName("hint")
        self.startup_lbl.setCursor(Qt.PointingHandCursor)
        self.startup_lbl.setToolTip(
            "When enabled, the timer launches minimized to the system tray\n"
            "every time you log in to Windows."
        )
        self.startup_lbl.mousePressEvent = (
            lambda e: self.startup_sw.toggle() if self.startup_sw.isEnabled() else None
        )
        startup_row = QHBoxLayout()
        startup_row.addWidget(self.startup_lbl, 1)
        startup_row.addWidget(self.startup_sw)

        lay = QVBoxLayout(self)
        lay.setContentsMargins(MARGIN, 12, MARGIN, 20)
        lay.setSpacing(10)
        lay.setSizeConstraint(QLayout.SetFixedSize)  # window always fits content exactly
        lay.addWidget(hint)
        lay.addWidget(self.ring, 0, Qt.AlignHCenter)
        lay.addWidget(self.picker, 0, Qt.AlignHCenter)
        lay.addWidget(self.mode_row)
        lay.addSpacing(6)
        lay.addLayout(bottom)
        lay.addSpacing(2)
        lay.addLayout(startup_row)
        lay.addLayout(force_row)

        self.setStyleSheet(STYLE)
        for child in self.findChildren(QWidget):
            child.ensurePolished()  # so the fixed window size reflects the stylesheet
        self.resumed.connect(self.on_resumed)
        self.sync_mode_ui()
        self.sync_action_ui()

        self._icon_key = None
        self.tray = None
        if QSystemTrayIcon.isSystemTrayAvailable():
            menu = QMenu(self)
            act_show = QAction("Show window", self)
            act_show.triggered.connect(self.show_window)
            self.act_primary = QAction("Start", self)
            self.act_primary.triggered.connect(self.start_pause)
            self.act_cancel = QAction("Cancel", self)
            self.act_cancel.triggered.connect(self.cancel)
            act_quit = QAction("Quit", self)
            act_quit.triggered.connect(self.quit_app)
            menu.addAction(act_show)
            menu.addSeparator()
            menu.addAction(self.act_primary)
            menu.addAction(self.act_cancel)
            menu.addSeparator()
            menu.addAction(act_quit)
            self.tray = QSystemTrayIcon(self)
            self.tray.setContextMenu(menu)
            self.tray.activated.connect(self.on_tray)

        self.clock = QTimer(self)
        self.clock.timeout.connect(self.tick)
        self.clock.start(200)
        self.refresh()

    # ---- settings / mode -------------------------------------------------
    def _load_duration(self):
        try:
            v = int(self.settings.value("duration", DEFAULT_SECS))
        except (TypeError, ValueError):
            v = DEFAULT_SECS
        return min(MAX_SET, max(MIN_SET, v))

    def default_clock(self):
        t = datetime.now() + timedelta(seconds=self.duration)
        return ((t.hour * 60 + t.minute + 4) // 5 * 5) % 1440  # next multiple of 5 min

    def sync_mode_ui(self, animate=False):
        self.btn_clock.setChecked(self.clock_mode)
        self.btn_dur.setChecked(not self.clock_mode)
        self.mode_row.set_expanded(self.clock_mode, animate)
        self.sync_time_edit()

    def sync_time_edit(self):
        self.time_edit.blockSignals(True)
        self.time_edit.setTime(QTime(self.clock_min // 60, self.clock_min % 60))
        self.time_edit.blockSignals(False)

    def sync_action_ui(self):
        self.picker.set_current(self.action)
        can_force = self.action != "sleep"
        self.force_sw.setEnabled(can_force)
        self.force_lbl.setEnabled(can_force)

    def set_action(self, key):
        if self.mode != IDLE:
            return
        self.action = key
        self.settings.setValue("action", key)
        self.sync_action_ui()
        self.refresh()

    def on_startup_toggled(self, on: bool):
        self.start_with_windows = on
        self.settings.setValue("start_with_windows", on)
        set_startup(on)

    def set_mode(self, clock):
        if self.mode != IDLE:
            return
        self.clock_mode = clock
        if clock and not self.clock_touched:
            self.clock_min = self.default_clock()
        self.settings.setValue("clock_mode", clock)
        self.sync_mode_ui(animate=True)
        self.refresh()

    def on_time_edit(self, t):
        self.clock_min = t.hour() * 60 + t.minute()
        self.clock_touched = True
        self.refresh()

    # ---- actions -----------------------------------------------------------
    def add(self, delta):
        if self.mode == IDLE:
            if self.clock_mode:  # shift the target clock time (wraps past midnight)
                self.clock_min = (self.clock_min + int(delta / 60)) % 1440
                self.clock_touched = True
                self.sync_time_edit()
                self.refresh()
                return
            self.duration = min(MAX_SET, max(MIN_SET, self.duration + delta))
            self.remaining = self.total = self.duration
            self.settings.setValue("duration", self.duration)
        else:
            if self.mode == RUNNING:
                self.remaining = self.end - time.monotonic()
            self.remaining = min(MAX_SET, max(MIN_ACTIVE, self.remaining + delta))
            self.total = max(self.total, self.remaining)
            if self.mode == RUNNING:
                self.end = time.monotonic() + self.remaining
        self.refresh(animate=True)

    def start_pause(self):
        if self.mode == RUNNING:
            self.remaining = self.end - time.monotonic()
            self.mode = PAUSED
        else:
            if self.mode == IDLE:
                if self.clock_mode:
                    secs = max(1.0, next_occurrence(self.clock_min).timestamp() - time.time())
                else:
                    secs = self.duration
                    self.settings.setValue("duration", self.duration)
                self.remaining = self.total = secs
            self.end = time.monotonic() + self.remaining
            self.mode = RUNNING
            keep_awake(True)
        self.refresh()

    def cancel(self):
        self.mode = IDLE
        self.remaining = self.total = self.duration
        keep_awake(False)
        self.refresh(animate=True)

    def fire(self):
        act = ACTIONS[self.action]
        self.mode = IDLE
        self.clock.stop()
        keep_awake(False)
        r = self.ring
        r.mode, r.urgent = IDLE, False
        r.fraction = r.shown = 0.0
        r.title, r.time_text, r.sub = act["final_title"], "00:00", act["final_sub"]
        r.update()
        self.primary.setEnabled(False)
        self.cancel_btn.setEnabled(False)
        if self.tray:
            self.act_primary.setEnabled(False)
            self.act_cancel.setEnabled(False)
            self.tray.setToolTip("Sleep Timer - " + act["final_sub"].lower())
        force = self.force_sw.isChecked()
        if self.action == "sleep":
            # SetSuspendState blocks until the PC wakes, so run it off the GUI thread
            threading.Thread(target=self._do_sleep, args=(force,), daemon=True).start()
            return
        cmd = ["shutdown", "/r" if self.action == "restart" else "/s", "/t", "0"]
        if force:
            cmd.insert(2, "/f")  # e.g. shutdown /s /f /t 0
        flags = 0x08000000 if sys.platform == "win32" else 0
        subprocess.Popen(cmd, creationflags=flags)

    def _do_sleep(self, force):
        try:  # (hibernate=False, force, wakeup events enabled) -> real sleep, not hibernate
            ctypes.windll.powrprof.SetSuspendState(0, 1 if force else 0, 0)
        except Exception:
            pass
        self.resumed.emit()

    def on_resumed(self):
        """Back from sleep: reset to a fresh idle timer."""
        self.mode = IDLE
        self.remaining = self.total = self.duration
        self.primary.setEnabled(True)
        if self.tray:
            self.act_primary.setEnabled(True)
        self.clock.start(200)
        self.refresh(animate=True)

    # ---- display -----------------------------------------------------------
    def tick(self):
        if self.mode == RUNNING:
            self.remaining = self.end - time.monotonic()
            if self.remaining <= 0:
                return self.fire()
        self.refresh()

    def refresh(self, animate=False):
        r = self.ring
        act = ACTIONS[self.action]
        r.mode = self.mode
        idle_label = ""
        if self.mode == IDLE and self.clock_mode:
            tgt = next_occurrence(self.clock_min)
            secs = max(0.0, tgt.timestamp() - time.time())
            tomorrow = tgt.date() > datetime.now().date()
            r.set_fraction(1.0, animate)
            r.title = act["clock_title"]
            r.time_text = f"{self.clock_min // 60:02d}:{self.clock_min % 60:02d}"
            r.sub = ("tomorrow, " if tomorrow else "") + f"in {human(secs)}"
            idle_label = "at " + r.time_text
        elif self.mode == IDLE:
            secs = self.duration
            r.set_fraction(1.0, animate)
            r.title = "SLEEP TIMER"
            r.time_text = fmt(secs)
            r.sub = act["would"] + " " + time.strftime("%H:%M", time.localtime(time.time() + secs))
            idle_label = fmt(secs)
        else:
            secs = self.remaining
            r.set_fraction(max(0.0, min(1.0, secs / self.total)), animate)
            r.time_text = fmt(secs)
            if self.mode == RUNNING:
                r.title = act["run_title"]
                r.sub = act["at"] + " " + time.strftime("%H:%M", time.localtime(time.time() + secs))
            else:
                r.title = "PAUSED"
                r.sub = "Resume to continue"
        r.urgent = self.mode == RUNNING and secs <= 10
        idle = self.mode == IDLE
        for w in (self.btn_dur, self.btn_clock, self.time_edit, self.picker):
            w.setEnabled(idle)
        self.primary.setText({IDLE: "Start", RUNNING: "Pause", PAUSED: "Resume"}[self.mode])
        self.cancel_btn.setEnabled(not idle)
        r.update()
        self.update_tray(secs, idle_label)

    def update_tray(self, secs, idle_label):
        r = self.ring
        key = (self.mode, r.urgent, round(r.fraction * 48))
        if key != self._icon_key:
            self._icon_key = key
            icon = make_icon(r.fraction, self.mode, r.urgent)
            self.setWindowIcon(icon)
            if self.tray:
                self.tray.setIcon(icon)
                if not self.tray.isVisible():
                    self.tray.show()
        if self.tray:
            self.act_primary.setText(
                {IDLE: "Start", RUNNING: "Pause", PAUSED: "Resume"}[self.mode])
            self.act_cancel.setEnabled(self.mode != IDLE)
            self.tray.setToolTip({
                IDLE: f"Sleep Timer - not started ({idle_label})",
                RUNNING: f"Sleep Timer - {fmt(secs)} left",
                PAUSED: f"Sleep Timer - paused ({fmt(secs)} left)",
            }[self.mode])

    # ---- window / tray behaviour -------------------------------------------
    def show_window(self):
        self.showNormal()
        self.raise_()
        self.activateWindow()

        try:
            hwnd = int(self.winId())
            user32 = ctypes.windll.user32
            kernel32 = ctypes.windll.kernel32

            # Get thread IDs
            foreground = user32.GetForegroundWindow()
            current_thread = kernel32.GetCurrentThreadId()
            foreground_thread = user32.GetWindowThreadProcessId(foreground, None)

            # Attach to the foreground thread so we are allowed to set focus
            if foreground_thread != current_thread:
                user32.AttachThreadInput(foreground_thread, current_thread, True)

            user32.ShowWindow(hwnd, 9)               # SW_RESTORE
            user32.SetForegroundWindow(hwnd)
            user32.BringWindowToTop(hwnd)
            user32.SetActiveWindow(hwnd)
            user32.SetFocus(hwnd)

            # Temporary topmost (extra insurance)
            user32.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0003 | 0x0001 | 0x0020)
            user32.SetWindowPos(hwnd, -2, 0, 0, 0, 0, 0x0003 | 0x0001 | 0x0020)

            if foreground_thread != current_thread:
                user32.AttachThreadInput(foreground_thread, current_thread, False)

        except Exception:
            pass

    def on_tray(self, reason):
        if reason in (QSystemTrayIcon.Trigger, QSystemTrayIcon.DoubleClick):
            self.show_window()

    def quit_app(self):
        self.show_window()
        self.close()

    def showEvent(self, e):
        super().showEvent(e)
        dark_titlebar(self)

    def changeEvent(self, e):
        # minimize -> hide to tray
        if (e.type() == QEvent.WindowStateChange and self.isMinimized()
                and self.tray and self.tray.isVisible()):
            QTimer.singleShot(0, self.hide)
        super().changeEvent(e)

    def closeEvent(self, e):
        if self.mode != IDLE and ConfirmDialog(self, ACTIONS[self.action]["verb"]).exec() != QDialog.Accepted:
            e.ignore()
            return
        keep_awake(False)
        if self.tray:
            self.tray.hide()
        e.accept()
        QApplication.quit()
        


if __name__ == "__main__":
    # If another instance is already running → ask it to show and exit
    if is_already_running():
        sys.exit(0)

    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)

    start_in_tray = "--tray" in sys.argv or "--minimized" in sys.argv

    w = SleepTimer()
    start_single_instance_server(w)          # ← start listening

    if start_in_tray:
        if w.tray:
            w.tray.show()
            w.update_tray(w.remaining, "")
    else:
        w.show()

    sys.exit(app.exec())
