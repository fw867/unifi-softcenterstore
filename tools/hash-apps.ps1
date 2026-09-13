#!/usr/bin/env pwsh
# 刷新 apps.json 里各插件 Files[].Sha256
# 用法: powershell -File tools/hash-apps.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$appsJsonPath = Join-Path $root "apps/apps.json"

$py = @'
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
with open(apps_json, "w", encoding="utf-8", newline="\n") as f:
    json.dump(apps, f, ensure_ascii=False, indent=4)
    f.write("\n")
print(f"updated {apps_json}")
'@

$tmp = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($tmp, $py, (New-Object System.Text.UTF8Encoding($false)))
    python $tmp $root $appsJsonPath
    if ($LASTEXITCODE -ne 0) { throw "hash refresh failed" }
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}
