"""Read-only discovery for the guided setup. Mutations remain in bigbox_install."""
from pathlib import Path
import importlib.util,json,sys
spec=importlib.util.spec_from_file_location('installer',Path(__file__).with_name('bigbox_install.py'))
installer=importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)

def inspect(root):
    installer.tx.xml_path_for_root(root)
    if (Path(root)/installer.SETUP_DIR_NAME/'active-plan.json').is_file():
        return {'Installed':True,'Entries':[]}
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
    return {'Installed':False,'Entries':entries}

if __name__=='__main__':
    try:
        if len(sys.argv)!=2:raise ValueError('Choose the folder that contains LaunchBox.exe')
        print(json.dumps(inspect(str(Path(sys.argv[1]).resolve()))))
    except Exception as error:
        print(str(error),file=sys.stderr)
        sys.exit(3)
