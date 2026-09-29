# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['sleep timer.py'],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=['tkinter', 'matplotlib', 'numpy', 'pandas', 'scipy', 'PIL', 'PySide6.QtWebEngine', 'PySide6.QtWebEngineCore', 'PySide6.QtWebEngineWidgets', 'PySide6.Qt3DCore', 'PySide6.Qt3DRender', 'PySide6.QtCharts', 'PySide6.QtDataVisualization', 'PySide6.QtMultimedia', 'PySide6.QtMultimediaWidgets', 'PySide6.QtNetwork', 'PySide6.QtSql', 'PySide6.QtTest', 'PySide6.QtXml', 'PySide6.QtQuick', 'PySide6.QtQml', 'PySide6.QtOpenGL', 'PySide6.QtOpenGLWidgets', 'PySide6.QtPrintSupport', 'PySide6.QtPositioning', 'PySide6.QtLocation', 'PySide6.QtBluetooth', 'PySide6.QtNfc', 'PySide6.QtSensors', 'PySide6.QtSerialPort', 'PySide6.QtWebSockets', 'PySide6.QtWebChannel', 'PySide6.QtPdf', 'PySide6.QtPdfWidgets'],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='Sleep Timer',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['SleepTimer.ico'],
)
