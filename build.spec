# -*- mode: python ; coding: utf-8 -*-
# PyInstaller 配置：单文件、无控制台。blivedm_lib 通过 pathex 收集。
a = Analysis(
    ['main.py'],
    pathex=['.', 'blivedm_lib'],
    binaries=[],
    datas=[],
    hiddenimports=['qasync', 'aiohttp', 'pure_protobuf', 'blivedm'],
    hookspath=[],
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='B站弹幕助手',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,            # 无控制台窗口
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
