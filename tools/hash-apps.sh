#!/bin/sh
# 刷新 apps.json 里各插件 Files[].Sha256（Linux/macOS）
# 用法: ./tools/hash-apps.sh
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APPS_JSON="$ROOT/apps/apps.json"

python3 - "$ROOT" "$APPS_JSON" <<'PY'
import json, hashlib, sys, os
root, apps_json = sys.argv[1], sys.argv[2]
with open(apps_json, encoding="utf-8") as f:
    apps = json.load(f)
for app in apps:
    for item in app.get("Files") or []:
        path = os.path.join(root, item["Path"])
        if not os.path.isfile(path):
            print(f"missing: {item['Path']} ({app.get('Id')})", file=sys.stderr)
            continue
        h = hashlib.sha256(open(path, "rb").read()).hexdigest()
        item["Sha256"] = h
        print(f"{app.get('Id')}/{item['Name']} {h}")
with open(apps_json, "w", encoding="utf-8") as f:
    json.dump(apps, f, ensure_ascii=False, indent=4)
    f.write("\n")
print(f"updated {apps_json}")
PY
