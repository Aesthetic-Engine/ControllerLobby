"""Independent TOML check of the Xemu session rules (M). Runs XemuChecks with --dump, then parses every
corpus input and every generated session file with Python's tomllib, which shares no code with the adapter.

Usage: python -I -B harness/test_xemu_toml_oracle.py <dotnet> <new empty output dir>
Every input the adapter qualifies must be valid TOML; every generated file must be valid TOML with ports 1-4 = the
players' GUIDs then '', auto_bind = false, the owner's driver values unchanged, and every table and key outside
input.bindings and input.auto_bind equal to the owner's. Inputs tomllib rejects must be refused by the adapter.
"""
import json, subprocess, sys, tomllib
from pathlib import Path
dotnet, out = sys.argv[1], Path(sys.argv[2]).resolve()
if out.exists(): raise SystemExit("Output exists; choose a new directory")
out.mkdir(parents=True)
root = Path(__file__).resolve().parent
dump = out / "corpus.json"
r = subprocess.run([dotnet, "run", "--project", str(root / "XemuChecks" / "XemuChecks.csproj"), "-c", "Release", "--", "--dump", str(dump)], capture_output=True, text=True)
# The dump is judged on its own: XemuChecks' own result does not gate this independent check.
if not dump.is_file():
    print(r.stdout[-2000:], r.stderr[-2000:]); raise SystemExit("XemuChecks failed")
cases, failed = 0, 0
def check(name, ok, detail=""):
    global cases, failed
    cases += 1; failed += 0 if ok else 1
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else ": " + detail))
def parse(text):
    try: return tomllib.loads(text), None
    except tomllib.TOMLDecodeError as e: return None, str(e)
def rest(doc):
    d = json.loads(json.dumps(doc))
    d.get("input", {}).pop("bindings", None); d.get("input", {}).pop("auto_bind", None)
    if d.get("input") == {}: d.pop("input")
    return d
rows = json.loads(dump.read_text(encoding="utf-8"))
check("corpus-not-empty", len(rows) >= 20, str(len(rows)))
for row in rows:
    owner, err = parse(row["text"])
    if not row["qualified"]:
        # A narrow grammar may refuse valid TOML; it must refuse everything tomllib rejects (checked below for accepted rows).
        check("refused " + row["name"], True); continue
    check("accepted-input-is-valid-toml " + row["name"], owner is not None, str(err))
    if owner is None: continue
    mapped, merr = parse(row["mapped"])
    check("mapped-output-is-valid-toml " + row["name"], mapped is not None, str(merr))
    if mapped is None: continue
    b = mapped.get("input", {}).get("bindings", {}); g = row["guids"]
    check("mapped-ports " + row["name"], [b.get("port%d" % i) for i in range(1, 5)] == g + [""] * (4 - len(g)), json.dumps(b))
    check("mapped-auto-bind-false " + row["name"], mapped["input"].get("auto_bind") is False)
    ob = owner.get("input", {}).get("bindings", {})
    check("owner-drivers-unchanged " + row["name"], all(b.get(k) == v for k, v in ob.items() if k.endswith("_driver")), json.dumps(ob))
    check("everything-else-unchanged " + row["name"], rest(owner) == rest(mapped), json.dumps(rest(owner))[:200] + " vs " + json.dumps(rest(mapped))[:200])
must_refuse = {"decimal-port", "invalid-inline-table", "leading-zero", "implicit-parent", "items-without-comma", "nul-in-string", "nul-in-comment", "repeated-key", "dotted-key", "ws-line-of-only-nbsp", "ws-nbsp-before-comment", "ws-nbsp-in-inline-item"}
must_accept = {"ws-unicode-in-quoted-value", "ws-unicode-in-comment"}
named = {row["name"]: row for row in rows}
for name in sorted(must_refuse):
    check("known-bad-input-refused " + name, name in named and not named[name]["qualified"], json.dumps(named.get(name, {}))[:200])
for name in sorted(must_accept):
    check("known-good-input-accepted " + name, name in named and named[name]["qualified"], json.dumps(named.get(name, {}))[:200])
print(f"Cases={cases} Passed={cases - failed} Failed={failed}; M independent tomllib check of the Xemu rules; no emulator or controller")
sys.exit(1 if failed else 0)
