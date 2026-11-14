"""Read-only discovery for the guided setup. Mutations remain in bigbox_install."""
from pathlib import Path
import importlib.util,json,sys
spec=importlib.util.spec_from_file_location('installer',Path(__file__).with_name('bigbox_install.py'))
installer=importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)

def inspect(root):
    installer.tx.xml_path_for_root(root)
    plan=Path(root)/installer.SETUP_DIR_NAME/'active-plan.json'
    if plan.is_file():
        # Report which package and emulator entries are installed, so a newer Setup can offer an upgrade
        # that keeps the same emulator choices. Read-only; an unreadable plan reports no pin.
        pin,ids='',[]
        try:
            active=json.loads(plan.read_bytes())
            pin=str(active.get('PackageSha256','')).upper()
            ids=[str(e['Id']) for e in active.get('Entries',[]) if isinstance(e,dict) and 'Id' in e]
        except (OSError,ValueError,TypeError,KeyError):
            pass
        return {'Installed':True,'Entries':[],'InstalledPackagePin':pin,'InstalledEntryIds':ids}
    _,_,records=installer.parse_emulators_xml(root)
    entries=[]
    for record in records:
        if record['Kind'] not in installer.ALLOWED_KINDS:
            continue
        reason=''
        try:
            installer.discover_entries(root,record['Id'])
        except (installer.tx.Refused,OSError,ValueError) as error:
            reason=str(error)
        entries.append({key:record[key] for key in ('Id','Title','Kind','ApplicationPath')}|
                       {'Eligible':not reason,'Reason':reason})
    # Without an active plan, any file left in ControllerLobby.Setup (a kept record, preimages, files) is install
    # state that a failed rollback left behind; a completed removal or full rollback leaves no such folder.
    setup=Path(root)/installer.SETUP_DIR_NAME
    pending=setup.is_dir() and any(p.is_file() or p.is_symlink() for p in setup.rglob('*'))
    return {'Installed':False,'Entries':entries,'RecoveryPending':pending}

if __name__=='__main__':
    try:
        if len(sys.argv)!=2:raise ValueError('Choose the folder that contains LaunchBox.exe')
        print(json.dumps(inspect(str(Path(sys.argv[1]).resolve()))))
    except Exception as error:
        print(str(error),file=sys.stderr)
        sys.exit(3)
