#!/usr/bin/env python3
# Embeds handover-merge.js into FreebuffController.cs as Base64:
# replaces the content inside the quotes of the HandoverMergeJsB64 constant
# (idempotent, safe to re-run).
# build.bat / release.sh call it before compiling; after editing the JS, just
# rebuild.
# Reads and writes bytes so the C# file keeps its original CRLF line endings.
import base64
import os
import re
import sys

here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)
cs_path = os.path.join(root, "FreebuffController.cs")
js_path = os.path.join(root, "handover-merge.js")

with open(js_path, "rb") as f:
    b64 = base64.b64encode(f.read()).decode("ascii")

with open(cs_path, "rb") as f:
    cs = f.read().decode("utf-8")

pattern = re.compile(r'(HandoverMergeJsB64 = ")(?:[^"]*)(")')
new_cs, n = pattern.subn(lambda m: m.group(1) + b64 + m.group(2), cs)
if n == 0:
    print("ERROR: HandoverMergeJsB64 constant not found in FreebuffController.cs", file=sys.stderr)
    sys.exit(1)

if new_cs != cs:
    with open(cs_path, "wb") as f:
        f.write(new_cs.encode("utf-8"))
print("embedded handover-merge.js (%d bytes -> base64 %d chars)" % (
    os.path.getsize(js_path), len(b64)))
