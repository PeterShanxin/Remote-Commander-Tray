#!/usr/bin/env python3
"""Render MSIX assets from the same source geometry as the portable app icon."""
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("tray_icon", ROOT / "tools/generate-icon.py")
icon = importlib.util.module_from_spec(spec)
spec.loader.exec_module(icon)
destination = ROOT / "packaging/Assets"
destination.mkdir(parents=True, exist_ok=True)
for name, size in (("StoreLogo", 50), ("Square44x44Logo", 44), ("Square150x150Logo", 150), ("ListingLogo", 300)):
    (destination / f"{name}.png").write_bytes(icon.to_png(size, icon.render(size)))
