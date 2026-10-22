#!/usr/bin/env python3
"""Portable preflight for the public source candidate; this is not a compiler."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
from xml.etree import ElementTree as ET
import hashlib
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
ERRORS: list[str] = []


def require_file(relative: str) -> Path | None:
    path = ROOT / relative
    if not path.is_file():
        ERRORS.append(f"missing required file: {relative}")
        return None
    return path


class ImageSourceParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.sources: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag.lower() == "img":
            for name, value in attrs:
                if name.lower() == "src" and value:
                    self.sources.append(value)


def image_references(text: str) -> list[str]:
    parser = ImageSourceParser()
    parser.feed(text)
    references = parser.sources[:]
    markdown_image = re.compile(r"!\[[^\]]*\]\(\s*(?:<([^>]+)>|([^\s)]+))")
    references.extend(match.group(1) or match.group(2) for match in markdown_image.finditer(text))
    return references


def check_readme_images(relative: str) -> int:
    path = require_file(relative)
    if path is None:
        return 0
    count = 0
    for reference in image_references(path.read_text(encoding="utf-8")):
        parsed = urlsplit(reference)
        if parsed.scheme or parsed.netloc:
            continue
        local_path = unquote(parsed.path)
        if not local_path:
            continue
        target = (ROOT / local_path).resolve()
        try:
            target.relative_to(ROOT.resolve())
        except ValueError:
            ERRORS.append(f"README image escapes candidate: {relative} -> {reference}")
            continue
        if not target.is_file():
            ERRORS.append(f"README image is missing: {relative} -> {reference}")
        count += 1
    return count


PROJECTS = (
    "src/Curtain.App/Curtain.App.csproj",
    "src/Curtain.Core/Curtain.Core.csproj",
    "tests/Curtain.Core.Tests/Curtain.Core.Tests.csproj",
    "tests/Curtain.Windows.Fixture/Curtain.Windows.Fixture.csproj",
)
REQUIRED_FILES = (
    "LICENSE",
    "licenses/third-party/SOURCES.md",
    "src/Curtain.App/App.xaml",
    "src/Curtain.App/MainWindow.xaml",
    "src/Curtain.App/MainWindow.xaml.cs",
    "src/Curtain.App/app.manifest",
    "src/Curtain.App/Services/CurtainManager.cs",
    "src/Curtain.App/Services/CurtainSession.cs",
    "src/Curtain.Core/PrivacyProfile.cs",
    "tests/Curtain.Core.Tests/Program.cs",
    "tests/Curtain.Windows.Fixture/Program.cs",
    "tests/Curtain.Windows.Fixture/app.manifest",
    "docs/THIRD-PARTY-NOTICES.md",
    "THIRD-PARTY-NOTICES.md",
    "licenses/third-party/velopack/1.2.161/LICENSE",
    ".config/dotnet-tools.json",
)

xml_paths = [ROOT / path for path in PROJECTS]
for folder in ("src", "tests"):
    xml_paths.extend(path for pattern in ("*.xaml", "*.manifest")
                     for path in (ROOT / folder).rglob(pattern)
                     if not {"bin", "obj", ".git"}.intersection(path.relative_to(ROOT).parts))
xml_paths = list(dict.fromkeys(xml_paths))
for relative in PROJECTS + REQUIRED_FILES:
    require_file(relative)
for path in xml_paths:
    try:
        ET.parse(path)
    except (ET.ParseError, OSError) as error:
        ERRORS.append(f"invalid XML {path.relative_to(ROOT)}: {error}")

tools_path = ROOT / ".config/dotnet-tools.json"
if tools_path.is_file():
    try:
        json.loads(tools_path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError) as error:
        ERRORS.append(f"invalid JSON .config/dotnet-tools.json: {error}")

license_root = ROOT / "licenses/third-party"
license_files = list(license_root.rglob("*")) if license_root.is_dir() else []
if not license_root.is_dir() or not any(path.is_file() for path in license_files):
    ERRORS.append("third-party license archive is missing or empty")
sources_path = ROOT / "licenses/third-party/SOURCES.md"
if sources_path.is_file():
    sources_text = sources_path.read_text(encoding="utf-8")
    source_rows = re.findall(
        r"^\| (licenses/third-party/[^|]+) \|[^|]*\|[^|]*\| ([A-Fa-f0-9]{64}) \|$",
        sources_text, re.MULTILINE,
    )
    recorded = {relative: digest.upper() for relative, digest in source_rows}
    archived = {
        path.relative_to(ROOT).as_posix()
        for path in license_files
        if path.is_file() and path != sources_path
    }
    if len(source_rows) != 25 or set(recorded) != archived:
        ERRORS.append("third-party source and hash records do not cover all 25 archived texts")
    for relative, expected in recorded.items():
        path = ROOT / relative
        if path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest().upper() != expected:
            ERRORS.append(f"third-party SHA-256 mismatch: {relative}")
else:
    ERRORS.append("third-party source and hash index is missing")

for relative in ("README.md", "README.zh-CN.md"):
    readme = ROOT / relative
    if readme.is_file():
        readme_text = readme.read_text(encoding="utf-8")
        if "GPL-3.0-only" not in readme_text or "LICENSE" not in readme_text or "THIRD-PARTY-NOTICES.md" not in readme_text:
            ERRORS.append(f"license links are missing from {relative}")
attributes_path = ROOT / ".gitattributes"
if attributes_path.is_file() and "LICENSE -text" not in attributes_path.read_text(encoding="utf-8"):
    ERRORS.append(".gitattributes must preserve the official LICENSE bytes")
notice_docs = ROOT / "docs/THIRD-PARTY-NOTICES.md"
notice_root = ROOT / "THIRD-PARTY-NOTICES.md"
if notice_docs.is_file() and notice_root.is_file():
    if notice_docs.read_bytes() != notice_root.read_bytes():
        ERRORS.append("root and docs THIRD-PARTY-NOTICES.md differ")
    notice_text = notice_docs.read_text(encoding="utf-8")
    for expected_path in (
        "候选源码路径：`licenses/third-party/velopack/1.2.161/LICENSE`",
        "打包后路径：`licenses/velopack/1.2.161/LICENSE`",
    ):
        if expected_path not in notice_text:
            ERRORS.append(f"third-party notice is missing the Velopack path distinction: {expected_path}")

readme_image_count = sum(check_readme_images(path) for path in ("README.md", "README.zh-CN.md"))

EVENT_NAMES = {
    "Click", "SelectionChanged", "Toggled", "Checked", "Unchecked", "ValueChanged",
    "TextChanged", "Loaded", "Unloaded", "SizeChanged", "GotFocus", "LostFocus",
    "Activated", "Deactivated", "Closing", "Closed", "PointerEntered", "PointerExited",
    "PointerPressed", "PointerReleased", "PointerMoved", "PointerWheelChanged",
    "PointerCaptureLost", "Tapped", "DoubleTapped", "RightTapped", "KeyDown", "KeyUp",
    "PreviewKeyDown", "ItemClick", "Drop", "DragOver", "DragLeave",
}
EVENT_SUFFIXES = ("Changed", "Click", "Toggled", "Checked", "Unchecked", "Loaded",
                  "Unloaded", "Entered", "Exited", "Pressed", "Released", "Moved",
                  "Tapped", "KeyDown", "KeyUp", "GotFocus", "LostFocus", "Closing",
                  "Closed", "Activated", "Deactivated", "Drop", "DragOver", "DragLeave")
event_count = 0
for xaml_path in (path for path in xml_paths if path.suffix.lower() == ".xaml"):
    code_path = xaml_path.with_suffix(".xaml.cs")
    handlers: set[str] = set()
    try:
        tree = ET.parse(xaml_path)
    except (ET.ParseError, OSError):
        continue
    for element in tree.iter():
        for raw_name, value in element.attrib.items():
            name = raw_name.rsplit("}", 1)[-1]
            if name in EVENT_NAMES or name.endswith(EVENT_SUFFIXES):
                if value and re.fullmatch(r"[A-Za-z_]\w*", value):
                    handlers.add(value)
    if handlers and not code_path.is_file():
        ERRORS.append(f"XAML code-behind is missing: {code_path.relative_to(ROOT)}")
        continue
    if handlers:
        code = code_path.read_text(encoding="utf-8")
        for handler in sorted(handlers):
            event_count += 1
            declaration = re.compile(rf"\b(?:async\s+)?void\s+{re.escape(handler)}\s*\(")
            if not declaration.search(code):
                ERRORS.append(f"XAML event handler missing: {xaml_path.relative_to(ROOT)} -> {handler}")

pack_script = ROOT / "scripts/B3-Pack.ps1"
if pack_script.is_file():
    pack_text = pack_script.read_text(encoding="utf-8")
    if "licenses/third-party" not in pack_text or "-Destination $licenseDestination -Recurse -Force" not in pack_text:
        ERRORS.append("B3-Pack does not recursively copy the third-party license archive")
    if "docs/THIRD-PARTY-NOTICES.md" not in pack_text:
        ERRORS.append("B3-Pack no longer copies docs/THIRD-PARTY-NOTICES.md")
    if "$projectLicense = Join-Path $repo 'LICENSE'" not in pack_text or "Destination (Join-Path $publish 'LICENSE')" not in pack_text:
        ERRORS.append("B3-Pack no longer copies the project LICENSE")
workflow = ROOT / ".github/workflows/core-tests.yml"
if workflow.is_file():
    workflow_text = workflow.read_text(encoding="utf-8")
    if "actions/setup-python@" not in workflow_text or "python scripts/static-check.py" not in workflow_text:
        ERRORS.append("GitHub Actions is missing the Python static preflight step")

legacy_paths = [
    "release/" + "latest.example.json",
    *("docs/" + name for name in (
        "01-PRODUCT-SPEC.md", "02-ARCHITECTURE.md", "03-TEST-MATRIX.md",
        "04-ROADMAP.md", "05-UPDATE-R2.md", "06-DECISIONS.md",
        "07-CODEX-TASK-CARD.md",
    )),
]
text_suffixes = {".md", ".py", ".ps1", ".yml", ".yaml", ".json", ".csproj"}
for path in ROOT.rglob("*"):
    if not path.is_file() or path == Path(__file__).resolve():
        continue
    if path.suffix.lower() not in text_suffixes:
        continue
    if {"bin", "obj", ".git"}.intersection(path.relative_to(ROOT).parts):
        continue
    try:
        content = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        continue
    for legacy in legacy_paths:
        if legacy in content:
            ERRORS.append(f"stale candidate path reference in {path.relative_to(ROOT)}: {legacy}")

if ERRORS:
    for error in ERRORS:
        print(f"FAIL: {error}", file=sys.stderr)
    sys.exit(1)

print(f"XML valid: {len(xml_paths)} project/XAML/manifest files")
print("JSON valid: .config/dotnet-tools.json")
print(f"README local images valid: {readme_image_count}")
print(f"XAML event handlers found in code-behind: {event_count}")
print(f"Required source and license paths present: {len(REQUIRED_FILES) + len(PROJECTS)}")
print("NOTICE copies match; recursive Velopack license packaging and workflow step verified")
print("No obsolete release/ or legacy docs path references")
print("Static preflight passed; this check does not replace a compiler or runtime test.")