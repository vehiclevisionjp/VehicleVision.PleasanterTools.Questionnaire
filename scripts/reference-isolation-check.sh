#!/usr/bin/env bash
# デュアルライセンスを守る既存検査に、既定先・共有先の取り込み検査を補う。
set -euo pipefail
cd "$(dirname "$0")/.."

if command -v python3 > /dev/null 2>&1; then
    python=python3
elif command -v python > /dev/null 2>&1; then
    python=python
else
    echo 'Reference isolation check requires Python.' >&2
    exit 1
fi

"$python" - <<'PY'
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path.cwd()
if not (root / "Directory.Build.props").is_file():
    raise SystemExit("Reference isolation scan failed: Directory.Build.props is missing.")
patterns = ["_reference"]
shared = os.environ.get("PLEASANTER_REFERENCE_DIR")
if shared:
    reference = Path(shared).resolve()
    if reference == Path(reference.anchor):
        raise SystemExit("PLEASANTER_REFERENCE_DIR must not be the filesystem root.")
    patterns.append(reference.as_posix().casefold())
    if os.name == "nt" and reference.drive:
        patterns.append(("/" + reference.drive[0] + reference.as_posix()[2:]).casefold())
    normalized_shared = shared.replace("\\", "/").casefold()
    if normalized_shared.startswith("/") or re.match(r"^[a-z]:/", normalized_shared):
        patterns.append(normalized_shared.rstrip("/"))
    # Git Bash の絶対パス指定も Windows 側のパスへ正規化する。
    if os.name == "nt" and re.match(r"^/[a-zA-Z]/", shared):
        patterns.append((shared[1] + ":" + shared[2:]).casefold())

def has_reference(value):
    normalized = value.replace("\\", "/").casefold()
    return any(pattern in normalized for pattern in patterns)

failures = []
build_files = set()
for directory in ("src", "tests"):
    for path in (root / directory).rglob("*"):
        if {"bin", "obj", "node_modules", "wwwroot"} & set(path.relative_to(root).parts):
            continue
        if path.suffix == ".cs":
            for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
                if has_reference(line):
                    failures.append(f"{path.relative_to(root)}:{number}")
        elif path.suffix in (".csproj", ".props", ".targets"):
            build_files.add(path)
for pattern in ("*.props", "*.targets", "*.slnx", "*.sln"):
    build_files.update(root.glob(pattern))

for path in sorted(build_files):
    if path.suffix == ".sln":
        values = [line for line in path.read_text(encoding="utf-8-sig").splitlines()
                  if line.lstrip().startswith("Project(")]
    else:
        # XML コメント、Remove/Exclude、DefaultItemExcludes は取り込みではない。
        tree = ET.parse(path)
        values = []
        for element in tree.iter():
            values.extend(value for key, value in element.attrib.items()
                          if key in ("Include", "Update", "Project"))
            if element.tag.rsplit("}", 1)[-1] not in (
                "DefaultItemExcludes", "DefaultExcludesInProjectFolder",
                "DefaultItemExcludesInProjectFolder",
            ):
                values.append(element.text or "")
    if any(has_reference(value) or "implem." in value.casefold() for value in values):
        failures.append(str(path.relative_to(root)))

if failures:
    raise SystemExit("Reference source must not be imported:\n" + "\n".join(failures))
print("Reference isolation check passed.")
PY
