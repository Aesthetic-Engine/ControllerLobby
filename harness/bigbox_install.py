"""Portable Beta 1 BigBox installer and recovery adapter for the shim transaction.
Supports Schema 1 (legacy synthetic two-entry fixtures) and Schema 2 (portable Beta 1
discovery, install-time manifest generation, XInput reader provider probe, persistent
ControllerLobby.Setup management copy, explicit repair, and post-update uninstall).
"""
from pathlib import Path
import ctypes, hashlib, importlib.util, json, os, re, shutil, struct, subprocess, sys, tempfile, uuid
from ctypes import wintypes

sys.dont_write_bytecode=True
HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("transaction",HERE/"shim_install.py")
tx=importlib.util.module_from_spec(spec);spec.loader.exec_module(tx)

# Synthetic Schema 1 fixtures only; Schema 2 discovers emulator IDs dynamically.
ALLOWED_IDS=(
    "11111111-1111-4111-8111-111111111111",
    "22222222-2222-4222-8222-222222222222",
)
ALLOWED_KINDS=("retroarch","mame")
SETUP_DIR_NAME="ControllerLobby.Setup"


def safe(root,relative):
    root=Path(root).resolve();p=root/relative
    if Path(relative).is_absolute() or any(x in ("..","") for x in Path(relative).parts) or ":" in relative:raise tx.Refused("Relative package path required")
    for ancestor in (p,*p.parents):
        if ancestor.exists() or ancestor.is_symlink():
            if ancestor.is_symlink():raise tx.Refused("Symlink package destination")
            if getattr(os.lstat(ancestor),"st_file_attributes",0)&0x400 or getattr(ancestor.stat(),"st_file_attributes",0)&0x400:raise tx.Refused("Reparse package destination")
        if ancestor==root:break
    if not p.resolve().is_relative_to(root):raise tx.Refused("Package path escapes root")
    return str(p)


def probe_xinput_reader():
    """Read-only load/export/provider probe matching PinnedXInputReader.Open. Never invokes XInputGetState."""
    if struct.calcsize("P")!=8:raise tx.Refused("64-bit Python (win-amd64) required for XInput reader probe")
    k=ctypes.WinDLL("kernel32",use_last_error=True)
    k.GetSystemDirectoryW.argtypes=[wintypes.LPWSTR,wintypes.UINT]
    k.GetSystemDirectoryW.restype=wintypes.UINT
    k.LoadLibraryExW.argtypes=[wintypes.LPCWSTR,wintypes.HANDLE,wintypes.DWORD]
    k.LoadLibraryExW.restype=wintypes.HMODULE
    k.GetProcAddress.argtypes=[wintypes.HMODULE,ctypes.c_char_p]
    k.GetProcAddress.restype=ctypes.c_void_p
    k.GetModuleHandleExW.argtypes=[wintypes.DWORD,ctypes.c_void_p,ctypes.POINTER(wintypes.HMODULE)]
    k.GetModuleHandleExW.restype=wintypes.BOOL
    k.GetModuleFileNameW.argtypes=[wintypes.HMODULE,wintypes.LPWSTR,wintypes.DWORD]
    k.GetModuleFileNameW.restype=wintypes.DWORD
    k.FreeLibrary.argtypes=[wintypes.HMODULE]
    k.FreeLibrary.restype=wintypes.BOOL
    buf=ctypes.create_unicode_buffer(32768)
    n=k.GetSystemDirectoryW(buf,len(buf))
    if not (0<n<len(buf)):raise tx.Refused("GetSystemDirectoryW failed")
    path=buf.value+"\\XInput1_4.dll"
    if not os.path.isfile(path):raise tx.Refused("System XInput1_4.dll missing: "+path)
    before=tx.sha(path)
    module=k.LoadLibraryExW(path,None,0x100|0x800)
    if not module:raise tx.Refused("LoadLibraryExW failed for "+path)
    try:
        address=k.GetProcAddress(module,b"XInputGetState")
        if not address:raise tx.Refused("XInputGetState export missing in "+path)
        provider=wintypes.HMODULE()
        if not k.GetModuleHandleExW(0x4|0x2,address,ctypes.byref(provider)):raise tx.Refused("GetModuleHandleExW failed for XInputGetState")
        pbuf=ctypes.create_unicode_buffer(32768)
        pn=k.GetModuleFileNameW(provider,pbuf,len(pbuf))
        if not (0<pn<len(pbuf)):raise tx.Refused("GetModuleFileNameW failed for XInputGetState provider")
        actual=pbuf.value
        if tx.TEST_INJECT=="reader-provider-mismatch":actual=buf.value+"\\ForeignXInput.dll"
        if actual.casefold()!=path.casefold():raise tx.Refused("XInputGetState provider mismatch: "+actual)  # guard:reader-provider
        after=tx.sha(path)
        if before!=after:raise tx.Refused("XInput1_4.dll changed during probe")
    finally:
        k.FreeLibrary(module)
    if tx.sha(path)!=before:raise tx.Refused("XInput1_4.dll changed after FreeLibrary")
    return {"LibraryPath":path,"LibrarySha256":before,"Export":0,"FunctionModulePath":actual,"FunctionModuleSha256":after}


def parse_emulators_xml(root):
    xml_path=tx.xml_path_for_root(root)
    raw=open(xml_path,"rb").read()
    text=raw.decode("utf-8")
    records=[]
    for m in re.finditer(r"<Emulator>(?=\s*<).*?</Emulator>",text,re.S):
        block=m.group(0)
        id_m=re.search(r"<ID>(.*?)</ID>",block,re.S)
        title_m=re.search(r"<Title>(.*?)</Title>",block,re.S)
        app_m=re.search(r"<ApplicationPath>(.*?)</ApplicationPath>",block,re.S)
        if not id_m or not app_m:continue
        eid=id_m.group(1).strip()
        title=(title_m.group(1).strip() if title_m else eid)
        app=app_m.group(1).strip().replace("&amp;","&").replace("&lt;","<").replace("&gt;",">")
        norm_app=app.replace("/","\\")
        exe_name=os.path.basename(norm_app).lower()
        folder=os.path.dirname(norm_app)
        if exe_name=="retroarch.exe":kind="retroarch"
        elif exe_name in ("mame.exe","mame64.exe"):kind="mame"
        elif "shim" in exe_name or "controllerlobby" in exe_name or any(k in title.lower() for k in ("retroarch","mame")):kind="wrapper"
        else:kind="excluded"
        records.append({"Id":eid,"Title":title,"ApplicationPath":norm_app,"Folder":folder,"OriginalExe":os.path.basename(norm_app),"Kind":kind})
    return xml_path,raw,records


def discover_entries(root,selected_ids=None):
    xml_path,raw,records=parse_emulators_xml(root)
    by_id={}
    for r in records:
        if r["Id"] in by_id:raise tx.Refused("Duplicate emulator ID in Emulators.xml: "+r["Id"])
        by_id[r["Id"]]=r
    if selected_ids:
        wanted=[x.strip() for x in selected_ids.split(",") if x.strip()]
        if not wanted or len(set(wanted))!=len(wanted):raise tx.Refused("Invalid or duplicate --entries selection")
        chosen=[];seen_chosen_ids=set()
        for eid in wanted:
            if eid in by_id:
                r=by_id[eid]
            else:
                matches=[x for x in records if x["Title"].lower()==eid.lower()]
                if len(matches)>1:raise tx.Refused("Ambiguous emulator title in --entries; pass exact ID: "+eid)
                if not matches:raise tx.Refused("Selected emulator ID not found in Emulators.xml: "+eid)
                r=matches[0]
            if r["Id"] in seen_chosen_ids:raise tx.Refused("Duplicate emulator entry selected: "+r["Id"])
            seen_chosen_ids.add(r["Id"])
            if r["Kind"]=="wrapper":raise tx.Refused("Emulator entry "+r["Id"]+" points to wrapper/non-standard executable: "+r["ApplicationPath"])
            if r["Kind"] not in ALLOWED_KINDS:raise tx.Refused("Emulator entry "+r["Id"]+" ("+r["Title"]+") is excluded from Beta 1; only retroarch and mame are supported")
            chosen.append(r)
    else:
        wrappers=[r for r in records if r["Kind"]=="wrapper"]
        if wrappers:raise tx.Refused("Emulator entry "+wrappers[0]["Id"]+" points to wrapper or unknown target: "+wrappers[0]["ApplicationPath"])
        def is_live_in_root(r):
            app=r["ApplicationPath"]
            if not r["Folder"] or Path(app).is_absolute() or any(x in ("..","") for x in Path(app).parts) or ":" in app:
                return False
            safe(root,r["Folder"])
            exe_abs=safe(root,app)
            return os.path.isfile(exe_abs)
        by_kind={k:[r for r in records if r["Kind"]==k] for k in ALLOWED_KINDS}
        live_by_kind={k:[r for r in by_kind[k] if is_live_in_root(r)] for k in ALLOWED_KINDS}  # guard:ignore-dead-duplicate-entries
        for k,live in live_by_kind.items():
            if len(live)>1:raise tx.Refused("Ambiguous "+k+" entries in Emulators.xml; pass --entries <ID> explicitly")
        chosen=[live[0] for k in ALLOWED_KINDS for live in [live_by_kind[k]] if live]
        if not chosen:
            for k in ALLOWED_KINDS:
                if len(by_kind[k])>1:raise tx.Refused("Ambiguous "+k+" entries in Emulators.xml; pass --entries <ID> explicitly")
            chosen=[items[0] for k in ALLOWED_KINDS for items in [by_kind[k]] if items]
    if not chosen:raise tx.Refused("No eligible RetroArch or MAME entries found in Emulators.xml")
    seen_folders=set()
    for r in chosen:
        if not r["Folder"]:raise tx.Refused("Emulator entry "+r["Id"]+" must reside in a subfolder under LaunchBox root")
        safe(root,r["Folder"])
        exe_abs=safe(root,r["ApplicationPath"])
        if not os.path.isfile(exe_abs):raise tx.Refused("Configured emulator executable missing: "+exe_abs)
        fc=os.path.normcase(r["Folder"])
        if fc in seen_folders:raise tx.Refused("Multiple selected entries share emulator folder: "+r["Folder"])
        seen_folders.add(fc)
        r["ExecutablePath"]=exe_abs
        r["ExecutableSha256"]=tx.sha(exe_abs)
    return xml_path,raw,chosen,records


def run_verify_only(exe_path):
    """Execute final-location --verify-only (package/file validation only; does not load XInput)."""
    if tx.TEST_FAIL_AT=="verify-only":
        raise RuntimeError("test: injected verify-only failure")
    data=open(exe_path,"rb").read(64)
    cwd=os.path.dirname(exe_path)
    if data.startswith(b"#!verify-only-exit:"):
        code=int(data.splitlines()[0].split(b":",1)[1].strip())
        if code!=0:raise RuntimeError("final-location --verify-only failed (exit %d): %s"%(code,exe_path))
        normal_p=os.path.join(cwd,"normal.json");bigbox_p=os.path.join(cwd,"bigbox.json")
        if not os.path.isfile(normal_p) or not os.path.isfile(bigbox_p):raise RuntimeError("manifest missing in "+cwd)
        b_cfg=json.loads(open(bigbox_p,"rb").read());n_raw=open(normal_p,"rb").read();n_cfg=json.loads(n_raw)
        if tx.sha_bytes(n_raw)!=b_cfg.get("ManifestSha256"):raise RuntimeError("bigbox.json ManifestSha256 mismatch")
        if n_cfg.get("Kind") not in ALLOWED_KINDS:raise RuntimeError("invalid Kind in normal.json")
        for p in n_cfg.get("Payload",[]):
            fp=os.path.join(cwd,p["Path"])
            if not os.path.isfile(fp) or tx.sha(fp)!=p["Sha256"]:raise RuntimeError("payload pin mismatch in "+fp)
        return
    if data.startswith(b"MZ"):
        r=subprocess.run([exe_path,"--verify-only"],cwd=cwd,capture_output=True,timeout=30)
        if r.returncode!=0:
            raise RuntimeError("final-location --verify-only failed (exit %d): %s"%(r.returncode,exe_path))
        return
    # Synthetic non-PE test payload validation
    normal_p=os.path.join(cwd,"normal.json");bigbox_p=os.path.join(cwd,"bigbox.json")
    if not os.path.isfile(normal_p) or not os.path.isfile(bigbox_p):raise RuntimeError("manifest missing in "+cwd)
    b_cfg=json.loads(open(bigbox_p,"rb").read());n_raw=open(normal_p,"rb").read()
    if tx.sha_bytes(n_raw)!=b_cfg.get("ManifestSha256"):raise RuntimeError("bigbox.json ManifestSha256 mismatch")


def generate_entry_manifests(kind,exe_path,exe_sha,reader_info,payload_files):
    normal_obj={
        "Schema":1,
        "Kind":kind,
        "Executable":exe_path,
        "ExecutableSha256":exe_sha,
        "Reader":reader_info,
        "ReferenceArguments":[],
        "RetroArch":None,
        "Payload":[{"Path":f["Name"],"Sha256":f["Sha256"]} for f in payload_files],
        "ReferenceBinaries":[],
        "ReferenceConfiguration":None,
    }
    normal_bytes=(json.dumps(normal_obj,indent=2)+"\n").encode("utf-8")
    normal_sha=tx.sha_bytes(normal_bytes)
    bigbox_obj={
        "Kind":kind,
        "ManifestSha256":normal_sha,
        "LegacyShim":"",
        "Content":"",
        "RomDirectory":"",
        "LegacyFiles":[],
        "Mode":"normal",
    }
    bigbox_bytes=(json.dumps(bigbox_obj,indent=2)+"\n").encode("utf-8")
    bigbox_sha=tx.sha_bytes(bigbox_bytes)
    return normal_bytes,normal_sha,bigbox_bytes,bigbox_sha


def setup_cmd_bytes(action,pin):
    pin_sub=pin.upper()[:16]
    if action=="remove":
        body=(
            '@echo off\r\n'
            'setlocal\r\n'
            'set "SETUP_DIR=%~dp0."\r\n'
            'set "REC_FILE=%~dp0install-record.txt"\r\n'
            'set "LB_ROOT=%~dp0.."\r\n'
            'set "REC_RUNNER=%LOCALAPPDATA%\\ControllerLobby\\RecoveryRunner\\'+pin_sub+'"\r\n'
            'set "TMP_RUN=%TEMP%\\cl-uninstall-%RANDOM%-%RANDOM%"\r\n'
            'mkdir "%TMP_RUN%" || exit /b 3\r\n'
            'if exist "%~dp0python\\python.exe" (\r\n'
            '  xcopy /E /I /Q /Y "%~dp0python" "%TMP_RUN%\\python" >nul || exit /b 3\r\n'
            '  set "PY_EXE=%TMP_RUN%\\python\\python.exe"\r\n'
            ') else if exist "%REC_RUNNER%\\python\\python.exe" (\r\n'
            '  xcopy /E /I /Q /Y "%REC_RUNNER%\\python" "%TMP_RUN%\\python" >nul || exit /b 3\r\n'
            '  set "PY_EXE=%TMP_RUN%\\python\\python.exe"\r\n'
            ') else (\r\n'
            '  echo Error: Bundled Python recovery runtime missing.\r\n'
            '  exit /b 3\r\n'
            ')\r\n'
            'if exist "%~dp0bigbox_install.py" (\r\n'
            '  copy /Y "%~dp0bigbox_install.py" "%TMP_RUN%\\bigbox_install.py" >nul || exit /b 3\r\n'
            ') else if exist "%REC_RUNNER%\\bigbox_install.py" (\r\n'
            '  copy /Y "%REC_RUNNER%\\bigbox_install.py" "%TMP_RUN%\\bigbox_install.py" >nul || exit /b 3\r\n'
            ') else (\r\n'
            '  echo Error: Recovery script bigbox_install.py missing.\r\n'
            '  exit /b 3\r\n'
            ')\r\n'
            'if exist "%~dp0shim_install.py" (\r\n'
            '  copy /Y "%~dp0shim_install.py" "%TMP_RUN%\\shim_install.py" >nul || exit /b 3\r\n'
            ') else if exist "%REC_RUNNER%\\shim_install.py" (\r\n'
            '  copy /Y "%REC_RUNNER%\\shim_install.py" "%TMP_RUN%\\shim_install.py" >nul || exit /b 3\r\n'
            ') else (\r\n'
            '  echo Error: Recovery script shim_install.py missing.\r\n'
            '  exit /b 3\r\n'
            ')\r\n'
            '(\r\n'
            '  echo @echo off\r\n'
            '  echo "%PY_EXE%" -I -B "%TMP_RUN%\\bigbox_install.py" remove --receipt "%SETUP_DIR%" --record "%REC_FILE%" --root "%LB_ROOT%" --package-sha256 '+pin+'\r\n'
            '  echo set "result=%%errorlevel%%"\r\n'
            '  echo if "%%CONTROLLERLOBBY_NO_PAUSE%%"=="" pause\r\n'
            '  echo exit /b %%result%%\r\n'
            ') > "%TMP_RUN%\\run_remove.cmd"\r\n'
            'cd /d "%TEMP%"\r\n'
            '"%TMP_RUN%\\run_remove.cmd"\r\n'
        )
        return body.encode("utf-8")
    body=(
        '@echo off\r\n'
        'setlocal\r\n'
        'set "REC_RUNNER=%LOCALAPPDATA%\\ControllerLobby\\RecoveryRunner\\'+pin_sub+'"\r\n'
        'if exist "%~dp0python\\python.exe" (\r\n'
        '  set "PY_EXE=%~dp0python\\python.exe"\r\n'
        ') else if exist "%REC_RUNNER%\\python\\python.exe" (\r\n'
        '  set "PY_EXE=%REC_RUNNER%\\python\\python.exe"\r\n'
        ') else (\r\n'
        '  echo Error: Bundled Python runtime missing.\r\n'
        '  exit /b 3\r\n'
        ')\r\n'
        '"%PY_EXE%" -I -B "%~dp0bigbox_install.py" '+action+' --receipt "%~dp0." --record "%~dp0install-record.txt" --root "%~dp0.." --package-sha256 '+pin+'\r\n'
        'set "result=%errorlevel%"\r\n'
        'if "%CONTROLLERLOBBY_NO_PAUSE%"=="" pause\r\n'
        'exit /b %result%\r\n'
    )
    return body.encode("utf-8")


def verify_distribution(base,data,expected):
    if data.get("Architecture")!="win-x64":raise tx.Refused("Expected win-x64 package architecture")
    if tuple(data.get("AllowedKinds",[]))!=ALLOWED_KINDS:raise tx.Refused("Beta 1 package must allow only retroarch and mame")
    payload=data.get("PayloadFiles",[])
    if not payload or not any(f.get("Name")=="ControllerLobby.BigBox.exe" for f in payload):
        raise tx.Refused("Package missing ControllerLobby.BigBox.exe payload")
    if not any(f.get("Name")=="source-manifest.json" for f in payload):
        raise tx.Refused("Package missing source-manifest.json payload")
    expected_payload={}
    for f in payload:
        name=f["Name"]
        if "/" in name or "\\" in name or name in ("normal.json","bigbox.json","trial.json") or name.lower().endswith(".pdb"):
            raise tx.Refused("Invalid or forbidden payload entry: "+name)
        if name in expected_payload:
            raise tx.Refused("Duplicate payload entry in package.json: "+name)
        src=safe(base,f["Source"])
        if not os.path.isfile(src) or tx.sha(src)!=f["Sha256"]:raise tx.Refused("Payload file missing or hash mismatch: "+f["Source"])
        expected_payload[name]=f["Sha256"]
    payload_dir=Path(safe(base,"payload"))
    if not payload_dir.is_dir():raise tx.Refused("Payload directory missing: "+str(payload_dir))
    actual_payload={}
    for directory,dirs,files in os.walk(payload_dir,followlinks=False):
        for entry_name in dirs+files:
            ep=Path(directory)/entry_name
            if ep.is_symlink() or (getattr(os.lstat(ep),"st_file_attributes",0)&0x400):
                raise tx.Refused("Payload reparse point refused: "+str(ep))
        for fname in files:
            fp=Path(directory)/fname
            actual_payload[fp.relative_to(payload_dir).as_posix()]=tx.sha(str(fp))
    if any("/" in rel for rel in actual_payload) or any(p.is_dir() for p in payload_dir.iterdir()):  # guard:payload-flat
        raise tx.Refused("Nested payload entries refused: flat runtime required")
    if actual_payload!=expected_payload:  # guard:payload-equality
        raise tx.Refused("Payload file-set/hash equality failed")
    for df in data.get("DistributionFiles",[]):
        src=safe(base,df["Path"])
        if not os.path.isfile(src) or tx.sha(src)!=df["Sha256"]:raise tx.Refused("Distribution file missing or hash mismatch: "+df["Path"])


def configure_portable_from_active_plan(root,expected,include_management=True):
    setup_dir=Path(safe(root,SETUP_DIR_NAME))
    plan_path=setup_dir/"active-plan.json"
    if not plan_path.is_file():raise tx.Refused("Active installation plan missing: "+str(plan_path))
    active=json.loads(plan_path.read_bytes())
    if active.get("Schema")!=2 or active.get("PackageSha256","").upper()!=expected.upper():
        raise tx.Refused("BigBox package metadata changed")
    gen_paths={os.path.normcase(os.path.abspath(str(plan_path)))}
    for e in active["Entries"]:
        for fn in ("normal.json","bigbox.json"):
            gen_paths.add(os.path.normcase(os.path.abspath(safe(root,e["Folder"]+"/ControllerLobby.BigBox/"+fn))))
    tx.ALLOWED_GEN_PATHS=gen_paths
    tx.ENTRIES=[(e["Id"],e["Folder"],e["OriginalExe"]) for e in active["Entries"]]
    tx.SHIM_MANIFEST_PIN=tx.LOBBY_MANIFEST_PIN=expected.upper()
    tx.pre_redirect_check=None
    def plan(receipt,root_arg):
        files=[];edits=[]
        for e in active["Entries"]:
            for f in e["Files"]:
                dst=safe(root_arg,e["Folder"]+"/ControllerLobby.BigBox/"+f["Name"])
                src=str(setup_dir/"payload"/f["Name"]) if f["Role"]=="payload" else ("bytes",b"")
                files.append((src,dst,f["Sha256"]))
            edits.append((e["Id"],e["OriginalApplicationPath"],e["RedirectedApplicationPath"]))
        if include_management:
            for mf in active.get("ManagementFiles",[]):
                dst=safe(root_arg,SETUP_DIR_NAME+"/"+mf["RelativePath"])
                files.append((("bytes",b""),dst,mf["Sha256"]))
            plan_bytes=plan_path.read_bytes()
            files.append((("bytes",plan_bytes),str(plan_path),tx.sha_bytes(plan_bytes)))
        return files,edits
    tx.plan=plan
    return active


def configure(receipt, expected, root=None, selected_ids=None, action=None):
    base=Path(receipt).resolve()
    if root is not None and action in ("verify","remove","repair"):
        setup_plan=Path(root)/SETUP_DIR_NAME/"active-plan.json"
        if setup_plan.is_file():
            tx.xml_path_for_root(root)
            return configure_portable_from_active_plan(root,expected,include_management=(action!="remove"))
    pkg_file=base/"package.json"
    if not pkg_file.is_file():raise tx.Refused("Package metadata missing: "+str(pkg_file))
    raw=pkg_file.read_bytes()
    if hashlib.sha256(raw).hexdigest().upper()!=expected.upper():raise tx.Refused("BigBox package metadata changed")
    data=json.loads(raw)
    if data["Schema"]==1:
        if len(data["Entries"])<2:raise tx.Refused("Expected two private test entries")
        tx.ALLOWED_GEN_PATHS=set()
        tx.ENTRIES=[(e["Id"],e["Folder"],e.get("OriginalExe","ControllerLobby.Shim.exe")) for e in data["Entries"]]
        tx.SHIM_MANIFEST_PIN=tx.LOBBY_MANIFEST_PIN=expected.upper()
        tx.pre_redirect_check=None
        def plan(receipt_arg,root_arg):
            files=[];edits=[];seen=set()
            for entry in data["Entries"]:
                if entry["Id"] not in ALLOWED_IDS or entry["Id"] in seen:raise tx.Refused("Unexpected emulator entry")
                seen.add(entry["Id"])
                folder=entry["Folder"]
                orig=entry.get("OriginalExe","ControllerLobby.Shim.exe")
                for f in entry["Files"]:
                    files.append((safe(base,f["Source"]),safe(root_arg,folder+"/ControllerLobby.BigBox/"+f["Name"]),f["Sha256"]))
                edits.append((entry["Id"],folder.replace("/","\\")+"\\"+orig,folder.replace("/","\\")+"\\ControllerLobby.BigBox\\ControllerLobby.BigBox.exe"))
            if len({os.path.normcase(d) for _,d,_ in files})!=len(files):raise tx.Refused("Duplicate package destination")
            return files,edits
        tx.plan=plan
        return data
    if data["Schema"]!=2:raise tx.Refused("Unsupported package schema")
    verify_distribution(base,data,expected)
    if root is None:return data
    _,raw_xml,chosen,_=discover_entries(root,selected_ids)
    reader_info=probe_xinput_reader()
    payload_files=data["PayloadFiles"]
    tx.ALLOWED_GEN_PATHS=set()
    tx.ENTRIES=[(r["Id"],r["Folder"],r["OriginalExe"]) for r in chosen]
    tx.SHIM_MANIFEST_PIN=tx.LOBBY_MANIFEST_PIN=expected.upper()

    def build_plan_objects(root_arg):
        files=[];edits=[];active_entries=[]
        for r in chosen:
            folder=r["Folder"]
            orig=r["OriginalExe"]
            normal_bytes,normal_sha,bigbox_bytes,bigbox_sha=generate_entry_manifests(
                r["Kind"],r["ExecutablePath"],r["ExecutableSha256"],reader_info,payload_files
            )
            entry_files=[]
            for pf in payload_files:
                src=safe(base,pf["Source"])
                dst=safe(root_arg,folder+"/ControllerLobby.BigBox/"+pf["Name"])
                files.append((src,dst,pf["Sha256"]))
                entry_files.append({"Name":pf["Name"],"Sha256":pf["Sha256"],"Role":"payload"})
            n_dst=safe(root_arg,folder+"/ControllerLobby.BigBox/normal.json")
            b_dst=safe(root_arg,folder+"/ControllerLobby.BigBox/bigbox.json")
            files.append((("bytes",normal_bytes),n_dst,normal_sha))
            files.append((("bytes",bigbox_bytes),b_dst,bigbox_sha))
            entry_files.append({"Name":"normal.json","Sha256":normal_sha,"Role":"generated"})
            entry_files.append({"Name":"bigbox.json","Sha256":bigbox_sha,"Role":"generated"})
            orig_rel=folder.replace("/","\\")+"\\"+orig
            new_rel=folder.replace("/","\\")+"\\ControllerLobby.BigBox\\ControllerLobby.BigBox.exe"
            edits.append((r["Id"],orig_rel,new_rel))
            active_entries.append({
                "Id":r["Id"],
                "Kind":r["Kind"],
                "Title":r["Title"],
                "Folder":folder,
                "OriginalExe":orig,
                "ExecutablePath":r["ExecutablePath"],
                "ExecutableSha256":r["ExecutableSha256"],
                "OriginalApplicationPath":orig_rel,
                "RedirectedApplicationPath":new_rel,
                "NormalJsonSha256":normal_sha,
                "BigBoxJsonSha256":bigbox_sha,
                "Files":entry_files,
            })
        mgmt_files=[]
        def add_mgmt_file(src_tuple_or_path,rel_path,sha_val):
            dst=safe(root_arg,SETUP_DIR_NAME+"/"+rel_path)
            files.append((src_tuple_or_path,dst,sha_val))
            mgmt_files.append({"RelativePath":rel_path.replace("\\","/"),"Sha256":sha_val})
        add_mgmt_file(str(pkg_file),"package.json",expected.upper())
        for pf in payload_files:
            add_mgmt_file(safe(base,pf["Source"]),"payload/"+pf["Name"],pf["Sha256"])
        for df in data.get("DistributionFiles",[]):
            if df["Path"].lower().endswith(".cmd"):continue
            add_mgmt_file(safe(base,df["Path"]),df["Path"].replace("\\","/"),df["Sha256"])
        for title,act in [("Verify installation","verify"),("Upgrade or Repair","repair"),("Uninstall","remove")]:
            cb=setup_cmd_bytes(act,expected.upper())
            add_mgmt_file(("bytes",cb),title+".cmd",tx.sha_bytes(cb))
        active_plan={
            "Schema":2,
            "Generation":1,
            "PackageSha256":expected.upper(),
            "Reader":reader_info,
            "Entries":active_entries,
            "ManagementFiles":mgmt_files,
        }
        active_bytes=(json.dumps(active_plan,indent=2)+"\n").encode("utf-8")
        active_sha=tx.sha_bytes(active_bytes)
        files.append((("bytes",active_bytes),safe(root_arg,SETUP_DIR_NAME+"/active-plan.json"),active_sha))
        if len({os.path.normcase(d) for _,d,_ in files})!=len(files):raise tx.Refused("Duplicate package destination")
        return files,edits,active_plan

    cached=build_plan_objects(root)
    def plan(receipt_arg,root_arg):
        return cached[0],cached[1]
    def pre_redirect(receipt_arg,record_arg,root_arg):
        if tx.TEST_INJECT=="local-input-race":
            with open(chosen[0]["ExecutablePath"],"ab") as f:f.write(b"race")
        for r in chosen:
            exe_deployed=safe(root_arg,r["Folder"]+"/ControllerLobby.BigBox/ControllerLobby.BigBox.exe")
            run_verify_only(exe_deployed)  # guard:verify-only-gate
            if tx.sha(r["ExecutablePath"])!=r["ExecutableSha256"]:  # guard:local-input-recheck
                raise RuntimeError("configured emulator changed before redirection: "+r["ExecutablePath"])
        reader_now=probe_xinput_reader()
        if reader_now["LibrarySha256"]!=reader_info["LibrarySha256"] or reader_now["FunctionModuleSha256"]!=reader_info["FunctionModuleSha256"]:
            raise RuntimeError("system XInput1_4.dll changed before redirection")
    tx.plan=plan
    tx.pre_redirect_check=pre_redirect
    return cached[2]


def is_record_or_preimage_name(name):
    return (
        name=="install-record.txt"
        or bool(re.fullmatch(r".*\.preimage-[0-9A-F]{16}\.xml",name))
        or bool(re.fullmatch(r".*\.genpre-[0-9A-F]{16}\.bin",name))
    )


def sync_setup_record(record,root):
    setup_dir=Path(root)/SETUP_DIR_NAME
    if not setup_dir.is_dir() or not os.path.isfile(record):return
    rec_abs=Path(record).resolve()
    target_rec=(setup_dir/"install-record.txt").resolve()
    if rec_abs!=target_rec:
        shutil.copyfile(rec_abs,target_rec)
    for pat in (rec_abs.name+".preimage-*.xml",rec_abs.name+".genpre-*.bin","install-record.txt.preimage-*.xml","install-record.txt.genpre-*.bin"):
        for pre in rec_abs.parent.glob(pat):
            suffix=pre.name.split(".preimage-",1)[-1] if ".preimage-" in pre.name else pre.name.split(".genpre-",1)[-1]
            kind_tag=".preimage-" if ".preimage-" in pre.name else ".genpre-"
            dst_pre=setup_dir/("install-record.txt"+kind_tag+suffix)
            if pre.resolve()!=dst_pre.resolve():
                shutil.copyfile(pre,dst_pre)
        for pre in setup_dir.glob(pat):
            suffix=pre.name.split(".preimage-",1)[-1] if ".preimage-" in pre.name else pre.name.split(".genpre-",1)[-1]
            kind_tag=".preimage-" if ".preimage-" in pre.name else ".genpre-"
            dst_pre=rec_abs.parent/(rec_abs.name+kind_tag+suffix)
            if pre.resolve()!=dst_pre.resolve():
                shutil.copyfile(pre,dst_pre)


def write_gen_preimage(setup_dir,record,old_bytes,old_sha):
    tag=old_sha.upper()[:16]
    rec_abs=Path(record).resolve()
    targets=[setup_dir/f"install-record.txt.genpre-{tag}.bin"]
    alt=rec_abs.parent/f"{rec_abs.name}.genpre-{tag}.bin"
    if alt.resolve()!=targets[0].resolve():
        targets.append(alt)
    for t in targets:
        if not t.is_file() or tx.sha(str(t))!=old_sha:
            with open(t,"wb") as f:
                f.write(old_bytes);f.flush();os.fsync(f.fileno())
        if tx.sha(str(t))!=old_sha:
            raise RuntimeError("failed to persist verified generation preimage: "+str(t))


def find_gen_preimage(setup_dir,record,old_sha):
    tag=old_sha.upper()[:16]
    rec_abs=Path(record).resolve()
    for cand in (
        setup_dir/f"install-record.txt.genpre-{tag}.bin",
        rec_abs.parent/f"{rec_abs.name}.genpre-{tag}.bin",
    ):
        if cand.is_file() and tx.sha(str(cand))==old_sha:
            return cand
    return None


def parse_repair_generations(record):
    if not os.path.isfile(record):return {}
    gens={}
    with open(record,encoding="utf-8",errors="replace") as f:
        for raw in f:
            line=raw.rstrip("\r\n")
            m=(
                re.fullmatch(r"GEN (PENDING|STAGED) (\d+) ([0-9a-f]{32}) ([0-9A-F]{64}) ([0-9A-F]{64}) \*(.+)",line)
                or re.fullmatch(r"GEN (ABANDONED) (\d+) ([0-9a-f]{32}) ([0-9A-F]{64}) \*(.+)",line)
                or re.fullmatch(r"GEN (DONE) (\d+) ([0-9A-F]{64}) \*(.+)",line)
            )
            if not m:continue
            groups=m.groups()
            kind=groups[0];g=int(groups[1]);path=groups[-1]
            k=os.path.normcase(os.path.abspath(path))
            gmap=gens.setdefault(g,{})
            if kind in ("PENDING","STAGED"):
                if kind=="PENDING" and gmap and all(info["state"]=="ABANDONED" for info in gmap.values()):
                    gmap.clear()
                nonce,old_sha,new_sha=groups[2],groups[3],groups[4]
                gmap[k]={"gen":g,"path":path,"nonce":nonce,"old_sha":old_sha,"new_sha":new_sha,"state":kind}
            elif kind=="ABANDONED":
                nonce,old_sha=groups[2],groups[3]
                prev=gmap.get(k,{"gen":g,"path":path,"nonce":nonce,"old_sha":old_sha,"new_sha":old_sha})
                prev["state"]="ABANDONED"
                gmap[k]=prev
            elif kind=="DONE":
                new_sha=groups[2]
                if k in gmap:
                    gmap[k]["state"]="DONE"
    return gens


def is_valid_generated_json(root,dst,raw_bytes,expected,gen,base_active,pkg_data):
    try:
        text=raw_bytes.decode("utf-8")
        obj=json.loads(text)
    except Exception:
        return False
    if not isinstance(obj,dict):
        return False
    if (json.dumps(obj,indent=2)+"\n").encode("utf-8")!=raw_bytes:
        return False
    strict_manifest_schema=False
    if True:  # guard:reject-unknown-manifest-fields
        strict_manifest_schema=True
    dst_norm=os.path.normcase(os.path.abspath(dst))
    plan_norm=os.path.normcase(os.path.abspath(str(Path(safe(root,SETUP_DIR_NAME))/"active-plan.json")))
    reader_keys=("LibraryPath","LibrarySha256","Export","FunctionModulePath","FunctionModuleSha256")
    plan_keys=("Schema","Generation","PackageSha256","Reader","Entries","ManagementFiles")
    entry_keys=("Id","Kind","Title","Folder","OriginalExe","ExecutablePath","ExecutableSha256","OriginalApplicationPath","RedirectedApplicationPath","NormalJsonSha256","BigBoxJsonSha256","Files")
    file_keys=("Name","Sha256","Role")
    mgmt_keys=("RelativePath","Sha256")
    normal_keys=("Schema","Kind","Executable","ExecutableSha256","Reader","ReferenceArguments","RetroArch","Payload","ReferenceBinaries","ReferenceConfiguration")
    bigbox_keys=("Kind","ManifestSha256","LegacyShim","Content","RomDirectory","LegacyFiles","Mode")
    allowed_readers=[]
    if isinstance(base_active.get("Reader"),dict):
        allowed_readers.append(base_active["Reader"])
    try:
        probed_reader=probe_xinput_reader()
        if probed_reader not in allowed_readers:
            allowed_readers.append(probed_reader)
    except Exception:
        pass
    if dst_norm==plan_norm:
        if obj.get("Schema")!=2 or obj.get("Generation")!=gen or obj.get("PackageSha256","").upper()!=expected.upper():
            return False
        if obj.get("ManagementFiles")!=base_active.get("ManagementFiles"):
            return False
        if not isinstance(obj.get("Reader"),dict) or not obj["Reader"].get("LibrarySha256") or not obj["Reader"].get("FunctionModuleSha256"):
            return False
        entries_new=obj.get("Entries")
        entries_base=base_active.get("Entries",[])
        if not isinstance(entries_new,list) or len(entries_new)!=len(entries_base):
            return False
        for en,eb in zip(entries_new,entries_base):
            if not isinstance(en,dict):
                return False
            for k in ("Id","Kind","Title","Folder","OriginalExe","ExecutablePath","OriginalApplicationPath","RedirectedApplicationPath"):
                if en.get(k)!=eb.get(k):
                    return False
        if strict_manifest_schema:
            if tuple(obj.keys())!=plan_keys or tuple(obj["Reader"].keys())!=reader_keys or obj["Reader"] not in allowed_readers:
                return False
            for mf in obj.get("ManagementFiles",[]):
                if not isinstance(mf,dict) or tuple(mf.keys())!=mgmt_keys:
                    return False
            for en,eb in zip(entries_new,entries_base):
                if tuple(en.keys())!=entry_keys:
                    return False
                allowed_exe_shas={eb.get("ExecutableSha256")}
                if os.path.isfile(eb.get("ExecutablePath","")):
                    allowed_exe_shas.add(tx.sha(eb["ExecutablePath"]))
                if en.get("ExecutableSha256") not in allowed_exe_shas:
                    return False
                _,exp_n_sha,_,exp_b_sha=generate_entry_manifests(
                    eb["Kind"],eb["ExecutablePath"],en["ExecutableSha256"],obj["Reader"],pkg_data.get("PayloadFiles",[])
                )
                if en.get("NormalJsonSha256")!=exp_n_sha or en.get("BigBoxJsonSha256")!=exp_b_sha:
                    return False
                exp_files=[]
                for bf in eb.get("Files",[]):
                    if not isinstance(bf,dict) or tuple(bf.keys())!=file_keys:
                        return False
                    fc=dict(bf)
                    if fc["Name"]=="normal.json":fc["Sha256"]=exp_n_sha
                    elif fc["Name"]=="bigbox.json":fc["Sha256"]=exp_b_sha
                    exp_files.append(fc)
                if en.get("Files")!=exp_files:
                    return False
        return True
    expected_payload=[{"Path":f["Name"],"Sha256":f["Sha256"]} for f in pkg_data.get("PayloadFiles",[])]
    for eb in base_active.get("Entries",[]):
        n_norm=os.path.normcase(os.path.abspath(safe(root,eb["Folder"]+"/ControllerLobby.BigBox/normal.json")))
        b_norm=os.path.normcase(os.path.abspath(safe(root,eb["Folder"]+"/ControllerLobby.BigBox/bigbox.json")))
        allowed_exe_shas={eb.get("ExecutableSha256")}
        if os.path.isfile(eb.get("ExecutablePath","")):
            allowed_exe_shas.add(tx.sha(eb["ExecutablePath"]))
        if dst_norm==n_norm:
            base_ok=(
                obj.get("Schema")==1
                and obj.get("Kind")==eb["Kind"]
                and obj.get("Executable")==eb["ExecutablePath"]
                and isinstance(obj.get("ExecutableSha256"),str)
                and bool(re.fullmatch(r"[0-9A-F]{64}",obj["ExecutableSha256"]))
                and isinstance(obj.get("Reader"),dict)
                and bool(obj["Reader"].get("LibrarySha256"))
                and bool(obj["Reader"].get("FunctionModuleSha256"))
                and obj.get("ReferenceArguments")==[]
                and obj.get("RetroArch") is None
                and obj.get("Payload")==expected_payload
                and obj.get("ReferenceBinaries")==[]
                and obj.get("ReferenceConfiguration") is None
            )
            if not base_ok:
                return False
            if strict_manifest_schema:
                if tuple(obj.keys())!=normal_keys or tuple(obj["Reader"].keys())!=reader_keys:
                    return False
                if obj["Reader"] not in allowed_readers or obj["ExecutableSha256"] not in allowed_exe_shas:
                    return False
                exp_n_bytes,_,_,_=generate_entry_manifests(
                    eb["Kind"],eb["ExecutablePath"],obj["ExecutableSha256"],obj["Reader"],pkg_data.get("PayloadFiles",[])
                )
                if raw_bytes!=exp_n_bytes:
                    return False
            return True
        if dst_norm==b_norm:
            base_ok=(
                obj.get("Kind")==eb["Kind"]
                and isinstance(obj.get("ManifestSha256"),str)
                and bool(re.fullmatch(r"[0-9A-F]{64}",obj["ManifestSha256"]))
                and obj.get("LegacyShim")==""
                and obj.get("Content")==""
                and obj.get("RomDirectory")==""
                and obj.get("LegacyFiles")==[]
                and obj.get("Mode")=="normal"
            )
            if not base_ok:
                return False
            if strict_manifest_schema:
                if tuple(obj.keys())!=bigbox_keys:
                    return False
                matched_b=False
                for r_cand in allowed_readers:
                    for exe_cand in allowed_exe_shas:
                        if not exe_cand:continue
                        _,exp_n_sha,exp_b_bytes,_=generate_entry_manifests(
                            eb["Kind"],eb["ExecutablePath"],exe_cand,r_cand,pkg_data.get("PayloadFiles",[])
                        )
                        if obj["ManifestSha256"]==exp_n_sha and raw_bytes==exp_b_bytes:
                            matched_b=True
                            break
                    if matched_b:
                        break
                if not matched_b:
                    return False
            return True
    return False


def reconcile_interrupted_repair(receipt,record,root,expected,for_remove=False):
    setup_dir=Path(safe(root,SETUP_DIR_NAME))
    plan_path=setup_dir/"active-plan.json"
    plan_norm=os.path.normcase(os.path.abspath(str(plan_path)))
    pre_validated=False
    validated_stages=set()
    if True:  # guard:validate-journal-before-reconcile
        tx.xml_path_for_root(root)
        tx.check_no_reparse_under_root(root,setup_dir)
        tx.check_no_reparse_under_root(root,plan_path)
        if os.path.normcase(os.path.abspath(record)).startswith(os.path.normcase(os.path.abspath(root))+os.sep):
            tx.check_no_reparse_under_root(root,record)
        active=configure_portable_from_active_plan(root,expected,include_management=True)
        pkg_file=setup_dir/"package.json"
        tx.check_no_reparse_under_root(root,pkg_file)
        if not pkg_file.is_file() or tx.sha(str(pkg_file)).upper()!=expected.upper():
            raise tx.Refused("BigBox package metadata changed")
        pkg_data=json.loads(pkg_file.read_bytes())
        entries_rec,xml_rec,open_write=tx.read_record(receipt,record,root)
        if open_write and not for_remove:
            raise tx.Refused("Unfinished XML write in record; cannot repair")
        pre_validated=True
    gens=parse_repair_generations(record)
    for g in sorted(gens):
        gmap=gens[g]
        if all(info["state"]=="ABANDONED" for info in gmap.values()):
            continue
        plan_done=(plan_norm in gmap and gmap[plan_norm]["state"]=="DONE")
        all_done=(plan_done and (tx.ALLOWED_GEN_PATHS is None or set(gmap.keys())==set(tx.ALLOWED_GEN_PATHS)) and all(info["state"]=="DONE" for info in gmap.values()))
        if all_done:
            continue
        # Generation g was interrupted before full journal completion.
        # 1. Verify confinement, planned ownership, stage integrity, and target hashes before modifying anything.
        if pre_validated:
            for k,info in gmap.items():
                if tx.ALLOWED_GEN_PATHS is not None and k not in tx.ALLOWED_GEN_PATHS:
                    raise tx.Refused("Unplanned generation path in record: "+info["path"])
                tx.check_no_reparse_under_root(root,info["path"])
                tx.check_no_reparse_under_root(root,tx.stage_path(info["path"],info["nonce"]))
            if any(info["state"]=="ABANDONED" for info in gmap.values()):
                raise tx.Refused(
                    "Rollback of generation %d was interrupted; automatic recovery is unavailable. "
                    "All user files, journal, and preimages are preserved in ControllerLobby.Setup; "
                    "follow the manual recovery procedure in docs/BETA_SETUP_AND_RECOVERY.md."%g
                )
            all_staged=(
                tx.ALLOWED_GEN_PATHS is not None
                and set(gmap.keys())==set(tx.ALLOWED_GEN_PATHS)
                and all(info["state"] in ("STAGED","DONE") for info in gmap.values())
            )
            base_active=active
            if int(active.get("Generation",1))==g:
                if not all_staged or plan_norm not in gmap or tx.sha(str(plan_path))!=gmap[plan_norm]["new_sha"]:
                    raise tx.Refused("Interrupted active-plan generation does not match journal state")
                pre_plan_p=find_gen_preimage(setup_dir,record,gmap[plan_norm]["old_sha"])
                if not pre_plan_p:
                    raise tx.Refused("Missing active-plan preimage for interrupted generation")
                pre_raw=pre_plan_p.read_bytes()
                pre_active=json.loads(pre_raw)
                if pre_active.get("Schema")!=2 or int(pre_active.get("Generation",0))!=g-1 or pre_active.get("PackageSha256","").upper()!=expected.upper():
                    raise tx.Refused("Invalid active-plan preimage for interrupted generation")
                if (json.dumps(pre_active,indent=2)+"\n").encode("utf-8")!=pre_raw:
                    raise tx.Refused("Non-canonical active-plan preimage for interrupted generation")
                if not is_valid_generated_json(root,str(plan_path),plan_path.read_bytes(),expected,g,pre_active,pkg_data):
                    raise tx.Refused("Invalid active-plan content for interrupted generation")
                base_active=pre_active
            elif int(active.get("Generation",1))!=g-1:
                raise tx.Refused("Active installation plan generation out of sync with journal")
            expected_old_by_norm={plan_norm:tx.sha_bytes((json.dumps(base_active,indent=2)+"\n").encode("utf-8")) if base_active is not active else tx.sha(str(plan_path))}
            for eb in base_active.get("Entries",[]):
                for fn,sk in (("normal.json","NormalJsonSha256"),("bigbox.json","BigBoxJsonSha256")):
                    p_norm=os.path.normcase(os.path.abspath(safe(root,eb["Folder"]+"/ControllerLobby.BigBox/"+fn)))
                    expected_old_by_norm[p_norm]=eb[sk]
            for k,info in gmap.items():
                dst=info["path"];nonce=info["nonce"];old_sha=info["old_sha"];new_sha=info["new_sha"];state=info["state"]
                tx.check_no_reparse_under_root(root,dst)
                stage=tx.stage_path(dst,nonce)
                tx.check_no_reparse_under_root(root,stage)
                if k not in expected_old_by_norm or old_sha!=expected_old_by_norm[k]:
                    raise tx.Refused("Generation old_sha does not match verified active-plan: "+dst)
                if os.path.lexists(stage):
                    if not os.path.islink(stage) and os.path.isfile(stage):
                        if (
                            state in ("STAGED","DONE")
                            and tx.sha(stage)==old_sha
                            and old_sha!=new_sha
                            and os.path.isfile(dst)
                            and not os.path.islink(dst)
                            and tx.sha(dst)==new_sha
                            and find_gen_preimage(setup_dir,record,old_sha) is not None
                        ):
                            raise tx.Refused(
                                "Rollback of generation %d was interrupted; automatic recovery is unavailable. "
                                "All user files, journal, and preimages are preserved in ControllerLobby.Setup; "
                                "follow the manual recovery procedure in docs/BETA_SETUP_AND_RECOVERY.md."%g
                            )
                    if os.path.islink(stage) or not os.path.isfile(stage) or tx.sha(stage)!=new_sha:
                        raise tx.Refused("Foreign or invalid repair stage file: "+stage)
                    if not is_valid_generated_json(root,dst,open(stage,"rb").read(),expected,g,base_active,pkg_data):
                        raise tx.Refused("Foreign or invalid repair stage file: "+stage)
                    validated_stages.add(stage)
                valid_hashes={old_sha,new_sha} if (all_staged and state in ("STAGED","DONE")) else {old_sha}
                dst_ok=os.path.isfile(dst) and not os.path.islink(dst) and tx.sha(dst) in valid_hashes
                if dst_ok and tx.sha(dst)==new_sha and new_sha!=old_sha:
                    if not is_valid_generated_json(root,dst,open(dst,"rb").read(),expected,g,base_active,pkg_data):
                        dst_ok=False
                if not dst_ok:  # guard:repair-reconcile-foreign
                    if not for_remove:
                        raise tx.Refused("Foreign modification or missing file during interrupted repair: "+dst)
                if os.path.isfile(dst) and tx.sha(dst)==new_sha and new_sha!=old_sha:
                    pre_p=find_gen_preimage(setup_dir,record,old_sha)
                    if not pre_p:
                        if not for_remove:
                            raise tx.Refused("Missing or corrupted generation preimage for rollback: "+dst)
        # 2. Remove verified owned journaled stage files, restore any activated files to old_sha, and journal GEN ABANDONED.
        for info in gmap.values():
            dst=info["path"];nonce=info["nonce"];old_sha=info["old_sha"];new_sha=info["new_sha"];state=info["state"]
            stage=tx.stage_path(dst,nonce)
            if os.path.isfile(stage):
                if stage in validated_stages or (not pre_validated and state=="PENDING"):
                    os.remove(stage)
            if os.path.isfile(dst) and tx.sha(dst)==new_sha and new_sha!=old_sha:
                pre_p=find_gen_preimage(setup_dir,record,old_sha)
                if pre_p:
                    with open(stage,"wb") as rf:
                        rf.write(pre_p.read_bytes());rf.flush();os.fsync(rf.fileno())
                    os.replace(stage,dst)
            if state!="ABANDONED":
                tx.append(record,"GEN ABANDONED %d %s %s *%s"%(g,nonce,old_sha,dst))
        sync_setup_record(record,root)


def verify_portable(receipt,record,root,expected):
    active=configure(receipt,expected,root=root,action="verify")
    if active.get("Schema")==2:
        gens=parse_repair_generations(record)
        for g,gmap in sorted(gens.items()):
            if all(info["state"]=="ABANDONED" for info in gmap.values()):
                continue
            if any(info["state"]=="ABANDONED" for info in gmap.values()):
                return (
                    "MISMATCH Rollback of generation %d was interrupted; automatic recovery is unavailable. "
                    "All user files, journal, and preimages are preserved in ControllerLobby.Setup; "
                    "follow the manual recovery procedure in docs/BETA_SETUP_AND_RECOVERY.md."%g
                )
            for info in gmap.values():
                stage=tx.stage_path(info["path"],info["nonce"])
                if (
                    info["state"] in ("STAGED","DONE")
                    and os.path.isfile(stage)
                    and not os.path.islink(stage)
                    and tx.sha(stage)==info["old_sha"]
                    and info["old_sha"]!=info["new_sha"]
                ):
                    return (
                        "MISMATCH Rollback of generation %d was interrupted; automatic recovery is unavailable. "
                        "All user files, journal, and preimages are preserved in ControllerLobby.Setup; "
                        "follow the manual recovery procedure in docs/BETA_SETUP_AND_RECOVERY.md."%g
                    )
    res=tx.verify(receipt,record,root)
    if res!="verified":return res
    if active.get("Schema")!=2:return res
    gens=parse_repair_generations(record)
    plan_norm=os.path.normcase(os.path.abspath(str(Path(root)/SETUP_DIR_NAME/"active-plan.json")))
    for g,gmap in gens.items():
        if all(info["state"]=="ABANDONED" for info in gmap.values()):
            continue
        if plan_norm not in gmap or gmap[plan_norm]["state"]!="DONE" or any(info["state"]!="DONE" for info in gmap.values()):
            return "MISMATCH interrupted repair generation %d in install-record.txt; run repair or remove to reconcile"%g
    try:
        reader_now=probe_xinput_reader()
    except tx.Refused as r:
        return "MISMATCH "+str(r)
    rec_reader=active.get("Reader",{})
    if reader_now["LibrarySha256"]!=rec_reader.get("LibrarySha256") or reader_now["FunctionModuleSha256"]!=rec_reader.get("FunctionModuleSha256"):
        return "MISMATCH system XInput1_4.dll changed since installation; run repair to revalidate"
    for e in active["Entries"]:
        exe_p=e["ExecutablePath"]
        if not os.path.isfile(exe_p) or tx.sha(exe_p)!=e["ExecutableSha256"]:
            return "MISMATCH emulator binary changed since installation ("+exe_p+"); run repair to revalidate"
        deployed_exe=safe(root,e["Folder"]+"/ControllerLobby.BigBox/ControllerLobby.BigBox.exe")
        try:
            run_verify_only(deployed_exe)
        except Exception as ex:
            return "MISMATCH "+str(ex)
    return "verified"


def recovery_runner_dir(expected):
    base=Path(os.environ.get("LOCALAPPDATA",tempfile.gettempdir()))
    return base/"ControllerLobby"/"RecoveryRunner"/expected.upper()[:16]


def stage_recovery_runner(setup_dir,record,expected,allowed):
    if tx.TEST_INJECT=="skip-recovery-runner":return None
    runner_dir=recovery_runner_dir(expected)
    runner_dir.mkdir(parents=True,exist_ok=True)
    for p in sorted([x for x in setup_dir.rglob("*") if x.is_file() and not x.is_symlink()]):
        rel=p.relative_to(setup_dir)
        top=rel.parts[0].lower()
        if is_record_or_preimage_name(p.name):
            dst=runner_dir/p.name
            shutil.copyfile(p,dst)
            continue
        if top=="python" or rel.as_posix() in ("bigbox_install.py","shim_install.py","package.json","active-plan.json","Uninstall.cmd"):
            k=os.path.normcase(str(p.resolve()))
            if k in allowed and tx.sha(str(p))==allowed[k]:
                dst=runner_dir/rel
                dst.parent.mkdir(parents=True,exist_ok=True)
                if not dst.is_file() or tx.sha(str(dst))!=allowed[k]:
                    shutil.copyfile(p,dst)
    if os.path.isfile(record):
        shutil.copyfile(record,runner_dir/"install-record.txt")
    return runner_dir


def restore_interrupted_management_state(root,record,expected):
    if not expected or tx.TEST_INJECT=="skip-recovery-runner":return
    setup_dir=Path(root)/SETUP_DIR_NAME
    runner_dir=recovery_runner_dir(expected)
    if not setup_dir.is_dir() or not runner_dir.is_dir():return
    tx.check_no_reparse_under_root(root,setup_dir)
    for name in ("active-plan.json","package.json","install-record.txt"):
        src=runner_dir/name
        dst=setup_dir/name
        if src.is_file() and not dst.is_file():
            shutil.copyfile(src,dst)
    for pat in ("*.preimage-*.xml","*.genpre-*.bin"):
        for src in runner_dir.glob(pat):
            dst=setup_dir/src.name
            if not dst.is_file():
                shutil.copyfile(src,dst)
    rec_p=Path(record)
    if not rec_p.is_file() and (runner_dir/"install-record.txt").is_file():
        rec_p.parent.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(runner_dir/"install-record.txt",rec_p)


def cleanup_management_copy(root,record,active,expected):
    setup_dir=Path(safe(root,SETUP_DIR_NAME))
    if not setup_dir.exists():return None
    allowed={}
    for mf in active.get("ManagementFiles",[]):
        p=(setup_dir/mf["RelativePath"]).resolve()
        allowed[os.path.normcase(str(p))]=mf["Sha256"]
    plan_p=(setup_dir/"active-plan.json").resolve()
    if plan_p.is_file():
        allowed[os.path.normcase(str(plan_p))]=tx.sha(str(plan_p))
    kept=[]
    all_files=sorted([p for p in setup_dir.rglob("*") if p.is_file() or p.is_symlink()],key=lambda x:len(x.parts),reverse=True)
    record_files=[]
    for p in all_files:
        if is_record_or_preimage_name(p.name):
            record_files.append(p)
            continue
        k=os.path.normcase(str(p.resolve()))
        if p.is_symlink() or k not in allowed or tx.sha(str(p))!=allowed[k]:
            kept.append(str(p))
    if kept:
        return tx.incomplete(record,"management files not created by this install or changed since, kept: "+"; ".join(kept))
    running_in_setup=Path(sys.executable).resolve().is_relative_to(setup_dir.resolve())
    if running_in_setup:
        raise tx.Refused("Cannot delete locked management Python while running from ControllerLobby.Setup; use Uninstall.cmd or an external interpreter")
    runner_dir=None
    if True:  # guard:preserve-recovery-runner
        runner_dir=stage_recovery_runner(setup_dir,record,expected,allowed)
    if True:  # guard:precheck-delete-lock
        k32=ctypes.WinDLL("kernel32",use_last_error=True)
        k32.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
        k32.CreateFileW.restype=wintypes.HANDLE
        k32.CloseHandle.argtypes=[wintypes.HANDLE]
        k32.CloseHandle.restype=wintypes.BOOL
        invalid_h=ctypes.c_void_p(-1).value
        for p in all_files:
            h=k32.CreateFileW(str(p),0x00010000,7,None,3,0x80,None)
            if h in (None,0,invalid_h):
                msg=tx.incomplete(record,"management file in use and cannot be deleted: "+str(p))
                sync_setup_record(record,root)
                if runner_dir and os.path.isfile(record):
                    shutil.copyfile(record,runner_dir/"install-record.txt")
                return msg
            k32.CloseHandle(h)
    def delete_priority(p):
        rel=p.relative_to(setup_dir).as_posix()
        top=rel.split("/",1)[0].lower()
        if top in ("payload","docs","notices"):return (0,-len(p.parts),rel)
        if top=="python":return (1,0 if p.name.lower()=="python.exe" else 1,-len(p.parts),rel)
        if rel in ("Verify installation.cmd","Upgrade or Repair.cmd"):return (2,-len(p.parts),rel)
        if rel in ("bigbox_install.py","shim_install.py","package.json","active-plan.json"):return (3,-len(p.parts),rel)
        if rel=="Uninstall.cmd":return (5,-len(p.parts),rel)
        return (4,-len(p.parts),rel)
    non_record=[p for p in all_files if p not in record_files and p.name!="Uninstall.cmd"]
    non_record.sort(key=delete_priority)
    uninstall_Launcher=[p for p in all_files if p.name=="Uninstall.cmd"]
    deleted_backups=[]
    try:
        for p in non_record:
            raw_b=p.read_bytes()
            os.remove(p)
            deleted_backups.append((p,raw_b))
            if tx.TEST_INJECT=="exit-cleanup-after-python" and p.name.lower()=="python.exe":
                os._exit(79)
    except OSError as ex:
        if runner_dir is not None and tx.TEST_INJECT!="skip-recovery-runner":
            for bp,bdata in reversed(deleted_backups):
                bp.parent.mkdir(parents=True,exist_ok=True)
                with open(bp,"wb") as bf:bf.write(bdata);bf.flush();os.fsync(bf.fileno())
        msg=tx.incomplete(record,"management file could not be removed ("+str(ex)+")")
        sync_setup_record(record,root)
        if runner_dir and os.path.isfile(record):
            shutil.copyfile(record,runner_dir/"install-record.txt")
        return msg
    # Archive record + preimages to LocalAppData history before removing record_files and Uninstall.cmd
    hist_root=Path(os.environ.get("LOCALAPPDATA",tempfile.gettempdir()))/"ControllerLobby"/"SetupHistory"/uuid.uuid4().hex
    hist_root.mkdir(parents=True,exist_ok=True)
    for rf in record_files:
        shutil.copyfile(rf,hist_root/rf.name)
        os.remove(rf)
    for ul in uninstall_Launcher:
        if ul.exists():os.remove(ul)
    for d in sorted([p for p in setup_dir.rglob("*") if p.is_dir()],key=lambda x:len(x.parts),reverse=True):
        if not os.listdir(d):os.rmdir(d)
    if setup_dir.is_dir() and not os.listdir(setup_dir):
        os.rmdir(setup_dir)
    if setup_dir.exists():
        return "RECOVERY INCOMPLETE: ControllerLobby.Setup not empty after removal"
    if runner_dir and runner_dir.exists():
        shutil.rmtree(runner_dir,ignore_errors=True)
    return None


def repair_portable(receipt,record,root,expected):
    tx.require_frontend_closed("repairing")
    xml_path=tx.xml_path_for_root(root)
    setup_dir=Path(safe(root,SETUP_DIR_NAME))
    plan_path=setup_dir/"active-plan.json"
    if not plan_path.is_file():raise tx.Refused("Active installation plan missing; cannot repair")
    if True:  # guard:repair-reconcile-interrupted
        reconcile_interrupted_repair(receipt,record,root,expected,for_remove=False)
    active=configure_portable_from_active_plan(root,expected,include_management=True)
    entries_rec,xml_rec,open_write=tx.read_record(receipt,record,root)
    if open_write:raise tx.Refused("Unfinished XML write in record; cannot repair")
    pkg_data=json.loads((setup_dir/"package.json").read_bytes())
    verify_distribution(setup_dir,pkg_data,expected)
    text=open(xml_path,encoding="utf-8").read()
    for e in active["Entries"]:
        if tx.find_entry(text,e["Id"])[2]!=tx.xml_escape(e["RedirectedApplicationPath"]):
            raise tx.Refused("Emulators.xml entry "+e["Id"]+" is not redirected to ControllerLobby.BigBox; refusing repair")
    # Inspect every entry folder for foreign files or modified immutable payloads before changing anything
    payload_by_name={f["Name"]:f["Sha256"] for f in pkg_data["PayloadFiles"]}
    restores=[]
    for e in active["Entries"]:
        bb_dir=Path(safe(root,e["Folder"]+"/ControllerLobby.BigBox"))
        if not bb_dir.is_dir():raise tx.Refused("Deployed directory missing: "+str(bb_dir))
        expected_files={f["Name"]:(f["Sha256"],f["Role"]) for f in e["Files"]}
        for existing_p in bb_dir.iterdir():
            if existing_p.name not in expected_files:
                raise tx.Refused("Foreign file in deployed directory: "+str(existing_p))
        for fname,(fsha,frole) in expected_files.items():
            fp=bb_dir/fname
            if frole=="payload":
                if not fp.exists() or tx.sha(str(fp))!=fsha:
                    if fp.exists():  # guard:repair-foreign
                        raise tx.Refused("Foreign modification at deployed payload file: "+str(fp))
                    restores.append((str(setup_dir/"payload"/fname),str(fp),payload_by_name[fname]))
            else:
                if not fp.exists() or tx.sha(str(fp))!=fsha:
                    raise tx.Refused("Foreign modification or missing generated manifest: "+str(fp))
    reader_now=probe_xinput_reader()
    new_gen=int(active.get("Generation",1))+1
    # Restore any missing immutable payload files from verified ControllerLobby.Setup/payload
    for src,dst,h in restores:
        nonce=uuid.uuid4().hex
        stage=tx.stage_path(dst,nonce)
        with open(stage,"xb") as f:
            f.write(open(src,"rb").read());f.flush();os.fsync(f.fileno())
        if tx.sha(stage)!=h:
            os.remove(stage);raise RuntimeError("repair stage hash mismatch: "+dst)
        os.replace(stage,dst)
    try:
        entry_plans=[]
        all_targets=[]
        for e in active["Entries"]:
            exe_abs=safe(root,e["OriginalApplicationPath"])
            if not os.path.isfile(exe_abs):raise tx.Refused("Configured emulator missing: "+exe_abs)
            exe_sha=tx.sha(exe_abs)
            normal_bytes,normal_sha,bigbox_bytes,bigbox_sha=generate_entry_manifests(
                e["Kind"],exe_abs,exe_sha,reader_now,pkg_data["PayloadFiles"]
            )
            bb_dir=Path(safe(root,e["Folder"]+"/ControllerLobby.BigBox"))
            entry_targets=[]
            for fname,new_bytes,new_sha in [("normal.json",normal_bytes,normal_sha),("bigbox.json",bigbox_bytes,bigbox_sha)]:
                dst=str(bb_dir/fname)
                old_bytes=open(dst,"rb").read()
                old_sha=tx.sha_bytes(old_bytes)
                nonce=uuid.uuid4().hex
                t_obj={"dst":dst,"nonce":nonce,"old_bytes":old_bytes,"old_sha":old_sha,"new_bytes":new_bytes,"new_sha":new_sha}
                entry_targets.append(t_obj)
                all_targets.append(t_obj)
            e["ExecutableSha256"]=exe_sha
            e["NormalJsonSha256"]=normal_sha
            e["BigBoxJsonSha256"]=bigbox_sha
            for f in e["Files"]:
                if f["Name"]=="normal.json":f["Sha256"]=normal_sha
                elif f["Name"]=="bigbox.json":f["Sha256"]=bigbox_sha
            entry_plans.append((bb_dir,entry_targets))
        active["Generation"]=new_gen
        active["Reader"]=reader_now
        plan_dst=str(plan_path)
        old_plan_bytes=plan_path.read_bytes()
        old_plan_sha=tx.sha_bytes(old_plan_bytes)
        new_plan_bytes=(json.dumps(active,indent=2)+"\n").encode("utf-8")
        new_plan_sha=tx.sha_bytes(new_plan_bytes)
        plan_target={
            "dst":plan_dst,
            "nonce":uuid.uuid4().hex,
            "old_bytes":old_plan_bytes,
            "old_sha":old_plan_sha,
            "new_bytes":new_plan_bytes,
            "new_sha":new_plan_sha,
        }
        all_targets.append(plan_target)
        # 1. Persist verified durable preimages and GEN PENDING lines for all generation targets before writing stages.
        for t in all_targets:
            write_gen_preimage(setup_dir,record,t["old_bytes"],t["old_sha"])
        for t in all_targets:
            tx.append(record,"GEN PENDING %d %s %s %s *%s"%(new_gen,t["nonce"],t["old_sha"],t["new_sha"],t["dst"]))
        sync_setup_record(record,root)
        # 2. Write stage files and journal GEN STAGED for all generation targets before any os.replace activation.
        for idx,t in enumerate(all_targets):
            stage=tx.stage_path(t["dst"],t["nonce"])
            t["stage"]=stage
            with open(stage,"xb") as f:
                f.write(t["new_bytes"]);f.flush();os.fsync(f.fileno())
            if tx.sha(stage)!=t["new_sha"]:raise RuntimeError("repair manifest hash mismatch: "+t["dst"])
            tx.append(record,"GEN STAGED %d %s %s %s *%s"%(new_gen,t["nonce"],t["old_sha"],t["new_sha"],t["dst"]))
            sync_setup_record(record,root)
            if idx==0 and tx.TEST_INJECT=="exit-repair-during-stage":
                os._exit(79)
        tx.maybe_fail("repair-after-staged")
        # 3. Activate entry manifests via os.replace and verify final-location executable before activating active-plan.json.
        for bb_dir,entry_targets in entry_plans:
            for t in entry_targets:
                os.replace(t["stage"],t["dst"])
            tx.maybe_fail("repair-after-gen")
            run_verify_only(str(bb_dir/"ControllerLobby.BigBox.exe"))
        # 4. Activate active-plan.json and record GEN DONE for all targets.
        os.replace(plan_target["stage"],plan_target["dst"])
        if tx.TEST_INJECT=="exit-repair-after-plan-replace":
            os._exit(79)
        for idx,t in enumerate(all_targets):
            tx.append(record,"GEN DONE %d %s *%s"%(new_gen,t["new_sha"],t["dst"]))
            sync_setup_record(record,root)
            if idx==0 and tx.TEST_INJECT=="exit-repair-during-gendone":
                os._exit(79)
    except Exception as ex:
        reconcile_interrupted_repair(receipt,record,root,expected,for_remove=False)
        tx.append(record,"# REPAIR FAILED: "+str(ex))
        sync_setup_record(record,root)
        raise RuntimeError("repair failed ("+str(ex)+"); rolled back to previous generation")
    tx.append(record,"# REPAIRED generation %d"%new_gen)
    sync_setup_record(record,root)
    return "repaired generation %d"%new_gen


def cleanup_rolled_back_setup_dir(root,record):
    setup_dir=Path(root)/SETUP_DIR_NAME
    if not setup_dir.is_dir():return
    if os.path.isfile(record):
        rec_text=open(record,encoding="utf-8",errors="replace").read()
        if "RECOVERY INCOMPLETE" in rec_text:return
    remaining=[p for p in setup_dir.rglob("*") if p.is_file() or p.is_symlink()]
    for p in remaining:
        if not is_record_or_preimage_name(p.name):
            return
    hist_root=Path(os.environ.get("LOCALAPPDATA",tempfile.gettempdir()))/"ControllerLobby"/"SetupHistory"/uuid.uuid4().hex
    hist_root.mkdir(parents=True,exist_ok=True)
    for p in remaining:
        shutil.copyfile(p,hist_root/p.name)
        os.remove(p)
    for d in sorted([p for p in setup_dir.rglob("*") if p.is_dir()],key=lambda x:len(x.parts),reverse=True):
        if not os.listdir(d):os.rmdir(d)
    if setup_dir.is_dir() and not os.listdir(setup_dir):
        os.rmdir(setup_dir)


def main():
    action=sys.argv[1];opts=dict(zip(sys.argv[2::2],sys.argv[3::2]))
    receipt=opts.get("--receipt",str(HERE))
    root=opts.get("--root",str(HERE.parent))
    record=opts.get("--record",os.path.join(root,SETUP_DIR_NAME,"install-record.txt"))
    selected_ids=opts.get("--entries")
    expected=opts.get("--package-sha256")
    if action=="discover":
        tx.xml_path_for_root(root)
        _,_,chosen,all_records=discover_entries(root,selected_ids)
        reader=probe_xinput_reader()
        print(json.dumps({"Selected":chosen,"AllEntries":all_records,"Reader":reader},indent=2))
        return 0
    if not expected:
        ap_path=Path(root)/SETUP_DIR_NAME/"active-plan.json"
        ph_path=Path(receipt)/"PACKAGE_HASHES.json"
        if ap_path.is_file():
            expected=json.loads(ap_path.read_bytes()).get("PackageSha256","")
        elif ph_path.is_file():
            expected=json.loads(ph_path.read_bytes()).get("package.json","")
        if not expected:raise tx.Refused("Missing --package-sha256")
    if action=="install":
        data=configure(receipt,expected,root=root,selected_ids=selected_ids,action="install")
        preview=tx.preview(receipt,record,root);print(preview,flush=True)
        print("Finishing install, please wait...",flush=True)
        token=preview.split("APPROVAL TOKEN: ",1)[1].splitlines()[0]
        try:
            res=tx.install(receipt,record,root,token)
        except Exception:
            if isinstance(data,dict) and data.get("Schema")==2:
                cleanup_rolled_back_setup_dir(root,record)
            raise
        if isinstance(data,dict) and data.get("Schema")==2:
            sync_setup_record(record,root)
        print(res)
    elif action=="preview":
        configure(receipt,expected,root=root,selected_ids=selected_ids,action="preview")
        print(tx.preview(receipt,record,root))
    elif action=="verify":
        result=verify_portable(receipt,record,root,expected);print(result)
        if result!="verified":return 5
    elif action=="repair":
        result=repair_portable(receipt,record,root,expected);print(result)
    elif action=="remove":
        restore_interrupted_management_state(root,record,expected)
        setup_plan_file=Path(root)/SETUP_DIR_NAME/"active-plan.json"
        is_portable=setup_plan_file.is_file()
        if is_portable and os.path.isfile(record):
            reconcile_interrupted_repair(receipt,record,root,expected,for_remove=True)
        active=configure(receipt,expected,root=root,selected_ids=selected_ids,action="remove")
        # In portable mode, read_record needs management files in tx.plan during record validation,
        # while tx.remove deletes entry files first and then cleanup_management_copy deletes setup files.
        if is_portable:
            configure_portable_from_active_plan(root,expected,include_management=True)
            setup_dir_norm=os.path.normcase(os.path.abspath(os.path.join(root,SETUP_DIR_NAME)))
            orig_read_record=tx.read_record
            def read_entry_only(rec_a,record_a,root_a):
                en,xm,ow=orig_read_record(rec_a,record_a,root_a)
                return [e for e in en if not os.path.normcase(os.path.abspath(e["path"])).startswith(setup_dir_norm+os.sep)],xm,ow
            tx.read_record=read_entry_only
        result=tx.remove(receipt,record,root);print(result)
        if result!="removed":
            if is_portable:sync_setup_record(record,root)
            return 5
        if is_portable:
            configure_portable_from_active_plan(root,expected,include_management=False)
        final=tx.check_removed(receipt,record,root)
        print(final)
        if not final.startswith("removed:"):
            if is_portable:sync_setup_record(record,root)
            return 5
        if is_portable:
            err=cleanup_management_copy(root,record,active,expected)
            if err:
                print(err)
                return 5
    else:raise tx.Refused("Unknown package action")
    return 0

if __name__=="__main__":
    try:sys.exit(main())
    except Exception as e:print(type(e).__name__+": "+str(e));sys.exit(3)
