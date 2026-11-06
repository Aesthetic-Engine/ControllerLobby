"""Build an immutable portable package from a clean, curated Git checkout."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
PROJECT = 'integration/ControllerLobby.BigBox/ControllerLobby.BigBox.csproj'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--python-embed', required=True, type=Path)
    ap.add_argument('--output', required=True, type=Path)
    ap.add_argument('--dotnet', default='dotnet')
    a = ap.parse_args()
    output = a.output.resolve()
    if output.exists():
        raise SystemExit('Output exists; choose a new directory')
    if not a.python_embed.is_file():
        raise SystemExit('Pass the official Python embeddable ZIP')
    git = lambda *args: subprocess.check_output(['git', '-C', str(ROOT), *args], text=True).strip()
    if git('status', '--porcelain'):
        raise SystemExit('Build requires a clean curated source checkout')
    commit = git('rev-parse', 'HEAD')
    names = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files', '-z']).decode('utf-8').split('\0')
    output.mkdir(parents=True)
    snapshot = output / 'source'
    snapshot.mkdir()
    inputs = {}
    for name in sorted(n for n in names if n):
        rel = Path(name)
        source = ROOT / rel
        if rel.is_absolute() or '..' in rel.parts or source.is_symlink() or not source.is_file():
            raise SystemExit('Source must contain regular tracked files only')
        target = snapshot / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        inputs[rel.as_posix()] = sha(source)
        shutil.copyfile(source, target)
        if sha(target) != inputs[rel.as_posix()]:
            raise SystemExit('Source changed during snapshot')
    env = dict(os.environ, DOTNET_CLI_HOME=str(output / 'dotnet-home'),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               DOTNET_GENERATE_ASPNET_CERTIFICATE='false')
    logs = output / 'logs'
    logs.mkdir()

    def run(label, command):
        result = subprocess.run(command, cwd=snapshot, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (logs / (label + '.log')).write_bytes(result.stdout)
        if result.returncode:
            raise SystemExit(f'{label} failed; see {logs / (label + ".log")}')
        return result.stdout.decode('utf-8-sig')

    sdk = run('sdk', [a.dotnet, '--version']).strip()
    if sdk != '10.0.403':
        raise SystemExit('Expected .NET SDK 10.0.403')
    projects, runtime_inputs = set(), set()

    def evaluate(project):
        project = project.resolve()
        key = project.relative_to(snapshot).as_posix()
        if key in projects:
            return
        projects.add(key)
        runtime_inputs.add(key)
        info = json.loads(run('evaluate-' + str(len(projects)), [a.dotnet, 'msbuild', str(project),
            '-nologo', '-getItem:Compile,ProjectReference,EmbeddedResource,Content,None']))['Items']
        for kind, items in info.items():
            for item in items:
                path = Path(item['FullPath']).resolve()
                if kind == 'ProjectReference':
                    evaluate(path)
                elif path.is_file() and not any(x in ('bin', 'obj') for x in path.relative_to(snapshot).parts):
                    runtime_inputs.add(path.relative_to(snapshot).as_posix())

    evaluate(snapshot / PROJECT)
    runtime = output / 'runtime'
    payload = runtime / 'payload'
    run('publish', [a.dotnet, 'publish', str(snapshot / PROJECT), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-o', str(payload), '-nologo',
        '-p:SatelliteResourceLanguages=en', '-p:DebugType=None', '-p:DebugSymbols=false',
        '-p:ContinuousIntegrationBuild=true', '-p:SourceRevisionId=' + commit])
    manifest = {
        'schema': 2, 'base_commit': commit, 'source_tree': inputs,
        'runtime_projects': sorted(projects), 'runtime_inputs': sorted(runtime_inputs),
        'configuration': {'sdk': sdk, 'configuration': 'Release', 'rid': 'win-x64',
                          'self_contained': True, 'satellite_languages': 'en', 'debug_type': 'None'},
        'scope': 'curated source, installer, documentation, build and verification tools',
    }
    write_json(payload / 'source-manifest.json', manifest)
    sys.path.insert(0, str(snapshot / 'harness'))
    import package_normal
    inventory = package_normal.require_flat_payload(payload)
    write_json(runtime / 'runtime-payload-hashes.json', inventory)
    package = output / 'package'
    run('package', [sys.executable, '-I', '-B', str(snapshot / 'harness/package_normal.py'),
        '--repo', str(snapshot), '--build', str(runtime), '--output', str(package),
        '--python-embed', str(a.python_embed.resolve()),
        '--expected-inventory-sha256', sha(runtime / 'runtime-payload-hashes.json'),
        '--expected-source-manifest-sha256', sha(payload / 'source-manifest.json')])
    receipt = {'commit': commit, 'source_files': len(inputs), 'runtime_projects': len(projects),
        'runtime_inputs': len(runtime_inputs), 'runtime_files': len(inventory),
        'package_json_sha256': sha(package / 'package.json'),
        'package_hashes_sha256': sha(package / 'PACKAGE_HASHES.json'),
        'source_manifest_sha256': sha(payload / 'source-manifest.json'),
        'runtime_inventory_sha256': sha(runtime / 'runtime-payload-hashes.json'),
        'proof': 'M/build only; no physical controllers or real emulators'}
    write_json(output / 'build-receipt.json', receipt)
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
