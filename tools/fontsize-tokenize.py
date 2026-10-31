"""把 MainWindow.xaml 里散落的字号换成 App.xaml 的六档 token。

只替换明确落在六档里的值（含就近归并：8.5/9→Sm，10/10.5→Base，11/11.5→Md，
12/12.5/13/13.5/14→Lg，15/15.5→Xl，17→Xxl）。图标字号 7.5/8 与超大号
22/26/31 保持硬编码，它们不参与排版节奏。
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "src" / "DynamicIsland" / "MainWindow.xaml"

MAP = [
    ("8.5", "FontSizeSm"),
    ("9", "FontSizeSm"),
    ("10", "FontSizeBase"),
    ("10.5", "FontSizeBase"),
    ("11", "FontSizeMd"),
    ("11.5", "FontSizeMd"),
    ("12", "FontSizeLg"),
    ("12.5", "FontSizeLg"),
    ("13", "FontSizeLg"),
    ("13.5", "FontSizeLg"),
    ("14", "FontSizeLg"),
    ("15", "FontSizeXl"),
    ("15.5", "FontSizeXl"),
    ("17", "FontSizeXxl"),
]

text = TARGET.read_text(encoding="utf-8-sig")
total = 0
for literal, token in MAP:
    needle = f'FontSize="{literal}"'
    replacement = f'FontSize="{{StaticResource {token}}}"'
    count = text.count(needle)
    if count:
        text = text.replace(needle, replacement)
        total += count
        print(f"{literal:>5} -> {token:<14} {count}")

TARGET.write_text("\ufeff" + text, encoding="utf-8")
print(f"total replaced: {total}")
left = re.findall(r'FontSize="[0-9.]+"', text)
print("remaining hardcoded:", sorted(set(left)))
