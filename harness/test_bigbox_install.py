"""Exercise the actual package adapter and transaction in isolated synthetic roots."""
from pathlib import Path
import hashlib,json,os,shutil,subprocess,sys
repo=Path(__file__).resolve().parent.parent
out=Path(sys.argv[1]);out.mkdir(parents=True)
mutation=sys.argv[2] if len(sys.argv)>2 else None
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def tree(p):return {str(f.relative_to(p)):sha(f) for f in p.rglob('*') if f.is_file()}
ids=['11111111-1111-4111-8111-111111111111','22222222-2222-4222-8222-222222222222'];folders=[r'Emulators\RetroArch',r'Emulators\MAME 0.279']
def fixture(name):
 case=out/name;root=case/'LaunchBox';data=root/'Data';data.mkdir(parents=True);package=case/'package';package.mkdir()
 xml='<LaunchBox>\r\n'+''.join('<Emulator><ID>'+eid+'</ID><Title>Test</Title><ApplicationPath>'+folder+'\\ControllerLobby.Shim.exe</ApplicationPath></Emulator>\r\n' for eid,folder in zip(ids,folders))+'<Emulator><ID>unrelated</ID><Title>Keep Me</Title><ApplicationPath>other.exe</ApplicationPath></Emulator>\r\n</LaunchBox>'
 (data/'Emulators.xml').write_bytes(xml.encode())
 entries=[]
 for i,(eid,folder) in enumerate(zip(ids,folders)):
  old=root/folder;old.mkdir(parents=True);(old/'ControllerLobby.Shim.exe').write_bytes(b'old diagnostic')
  payload=package/str(i);payload.mkdir();files=[]
  for name in ['ControllerLobby.BigBox.exe','trial.json','source-manifest.json']:
   p=payload/name;p.write_bytes(('synthetic '+name).encode());files.append({'Source':str(p.relative_to(package)),'Name':name,'Sha256':sha(p)})
  entries.append({'Id':eid,'Folder':folder,'Files':files})
 (package/'package.json').write_text(json.dumps({'Schema':1,'Entries':entries}),encoding='utf-8')
 for script in ['bigbox_install.py','shim_install.py']:shutil.copyfile(repo/'harness'/script,package/script)
 if mutation:
  p=package/'bigbox_install.py';s=p.read_text(encoding='utf-8')
  if mutation=='metadata-pin':s=s.replace('if hashlib.sha256(raw).hexdigest().upper()!=expected.upper():','if False:')
  elif mutation=='path-escape':s=s.replace('if Path(relative).is_absolute() or any(x in ("..","") for x in Path(relative).parts) or ":" in relative:','if False:').replace('if not p.resolve().is_relative_to(root):','if False:')
  elif mutation=='duplicate-entry':s=s.replace(' or entry["Id"] in seen:',' :')
  p.write_text(s,encoding='utf-8')
 return case,root,package
def run(case,root,package,action,expected=0,envextra=None,pin=None):
 env=dict(os.environ);env.update({'SHIM_INSTALL_TEST_FRONTEND':'closed','SHIM_INSTALL_TEST_FAIL_AT':'','SHIM_INSTALL_TEST_INJECT':''});env.update(envextra or {})
 cmd=[sys.executable,'-I',str(package/'bigbox_install.py'),action,'--receipt',str(package),'--record',str(case/'record.txt'),'--root',str(root),'--package-sha256',pin or sha(package/'package.json')]
 p=subprocess.run(cmd,env=env,capture_output=True,timeout=30);log=case/(action+'-'+str(len(list(case.glob('*.log'))))+'.log');log.write_bytes(p.stdout+p.stderr)
 assert p.returncode==expected,(action,p.returncode,expected,p.stdout.decode(errors='replace'))
 return p
def check(name,fn):
 try:fn(*fixture(name));print('PASS '+name,flush=True)
 except Exception as e:print('FAIL '+name+': '+str(e),flush=True);raise
def roundtrip(c,r,p):
 before=tree(r);run(c,r,p,'preview');assert tree(r)==before and not (c/'record.txt').exists();run(c,r,p,'install');run(c,r,p,'verify');run(c,r,p,'remove');assert tree(r)==before
def changed_payload(c,r,p):
 (p/'0/trial.json').write_bytes(b'changed');before=tree(r);run(c,r,p,'install',3);assert tree(r)==before
def bad_pin(c,r,p):run(c,r,p,'install',3,pin='0'*64)
def frontend(c,r,p):run(c,r,p,'install',3,{'SHIM_INSTALL_TEST_FRONTEND':'running'})
def wrong_entry(c,r,p):
 f=r/'Data/Emulators.xml';f.write_bytes(f.read_bytes().replace(b'ControllerLobby.Shim.exe',b'owner.exe',1));before=tree(r);run(c,r,p,'install',3);assert tree(r)==before
def existing(c,r,p):
 d=r/folders[0]/'ControllerLobby.BigBox';d.mkdir();(d/'trial.json').write_bytes(b'owner');before=tree(r);run(c,r,p,'install',3);assert tree(r)==before
def rollback(c,r,p,step):
 before=tree(r);run(c,r,p,'install',3,{'SHIM_INSTALL_TEST_FAIL_AT':step});assert tree(r)==before
def changed_installed(c,r,p):
 run(c,r,p,'install');f=r/folders[0]/'ControllerLobby.BigBox/trial.json';f.write_bytes(b'owner edit');run(c,r,p,'remove',5);assert f.read_bytes()==b'owner edit'
def xml_conflict(c,r,p):
 run(c,r,p,'install');f=r/'Data/Emulators.xml';f.write_bytes(f.read_bytes().replace(b'ControllerLobby.BigBox\\ControllerLobby.BigBox.exe',b'owner.exe',1));run(c,r,p,'remove',5);assert b'owner.exe' in f.read_bytes();assert (r/folders[0]/'ControllerLobby.BigBox/trial.json').exists()
def owner_race(c,r,p):
 run(c,r,p,'install',3,{'SHIM_INSTALL_TEST_INJECT':'owner-edit:after-token'});assert b'Owner Edit after-token' in (r/'Data/Emulators.xml').read_bytes();assert not (r/folders[0]/'ControllerLobby.BigBox/trial.json').exists()
def path_escape(c,r,p):
 f=p/'package.json';d=json.loads(f.read_bytes());d['Entries'][0]['Files'][0]['Name']='../../../escaped.exe';f.write_text(json.dumps(d),encoding='utf-8');before=tree(r);run(c,r,p,'install',3);assert tree(r)==before
def duplicate(c,r,p):
 f=p/'package.json';d=json.loads(f.read_bytes());d['Entries'][1]['Id']=d['Entries'][0]['Id'];f.write_text(json.dumps(d),encoding='utf-8')
 code='import importlib.util; s=importlib.util.spec_from_file_location("adapter",'+repr(str(p/'bigbox_install.py'))+'); m=importlib.util.module_from_spec(s); s.loader.exec_module(m); m.configure('+repr(str(p))+','+repr(sha(f))+'); m.tx.plan('+repr(str(p))+','+repr(str(r))+')'
 result=subprocess.run([sys.executable,'-I','-c',code],capture_output=True);(c/'plan.log').write_bytes(result.stdout+result.stderr)
 assert result.returncode!=0 and b'Unexpected emulator entry' in result.stderr,'duplicate entry accepted by plan'
cases=[('roundtrip-and-readonly-preview',roundtrip),('payload-tamper',changed_payload),('metadata-pin',bad_pin),('frontend-open',frontend),('owner-path-conflict',wrong_entry),('existing-file',existing),('rollback-after-files',lambda*c:rollback(*c,'after-files')),('rollback-after-first-xml',lambda*c:rollback(*c,'after-first-xml')),('changed-installed-kept',changed_installed),('xml-conflict-keeps-files',xml_conflict),('owner-race-preserved',owner_race),('path-escape',path_escape),('duplicate-entry',duplicate)]
if mutation:cases=[(n,f)for n,f in cases if n==mutation]
for name,fn in cases:check(name,fn)
print(str(len(cases))+'/'+str(len(cases))+' scratch installer checks; no live LaunchBox writes')
