"""Run focused M checks against a build, using synthetic scratch roots only."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--build', type=Path, required=True)
    ap.add_argument('--python-embed', type=Path, required=True)
    ap.add_argument('--output', type=Path, required=True)
    ap.add_argument('--dotnet', default='dotnet')
    ap.add_argument('--cc', default='gcc', help='MinGW-w64 x64 GCC')
    a = ap.parse_args()
    output, build = a.output.resolve(), a.build.resolve()
    if output.exists():
        raise SystemExit('Output exists; choose a new receipt directory')
    manifest_path = build / 'runtime/payload/source-manifest.json'
    manifest = json.loads(manifest_path.read_bytes())
    for name, expected in manifest['source_tree'].items():
        if hashlib.sha256((ROOT / name).read_bytes()).hexdigest().upper() != expected:
            raise SystemExit('Build/source mismatch: ' + name)
    output.mkdir(parents=True)
    env = dict(os.environ, DOTNET_CLI_HOME=str(output / 'dotnet-home'), DOTNET_CLI_TELEMETRY_OPTOUT='1',
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false',
        CONTROLLERLOBBY_TEST_RUNTIME=str(build / 'runtime'))
    compiler = Path(shutil.which(a.cc) or a.cc).resolve()
    # GCC's helper processes also need the selected toolchain's DLL directory.
    env['PATH'] = str(compiler.parent) + os.pathsep + env.get('PATH', '')
    results = {}

    def run(name, command, expected=0):
        result = subprocess.run(command, cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (output / (name + '.log')).write_bytes(result.stdout)
        results[name] = {'returncode': result.returncode, 'expected': expected, 'passed': result.returncode == expected}
        print(('PASS ' if results[name]['passed'] else 'FAIL ') + name, flush=True)
        return results[name]['passed']

    native = output / 'native'
    native.mkdir()
    for name, source, flags in (
        ('fixture.dll', 'xinput_fixture.c', ['-shared']),
    ):
        if not run('compile-' + name, [str(compiler), str(ROOT / 'tests/native' / source), '-o', str(native / name),
                                     '-static-libgcc', *flags]):
            raise SystemExit('Native stand-in compilation failed; see receipt log')
    for name, args in (
        ('NormalPlayChecks', []), ('BigBoxWindowChecks', []),
        ('LobbyWakeChecks', [str(native / 'fixture.dll')]), ('MameProfileChecks', []), ('RetroArchChecks', []),
    ):
        run(name, [a.dotnet, 'run', '--project', str(ROOT / 'harness' / name / (name + '.csproj')),
                   '-c', 'Release', '--', *args])
    py = [sys.executable, '-I', '-B']
    run('publication', [*py, str(ROOT / 'harness/test_publication.py')])
    run('removal-status', [*py, str(ROOT / 'harness/test_bigbox_removal_status.py')])
    run('schema1', [*py, str(ROOT / 'harness/test_bigbox_install.py'), str(output / 'schema1')])
    run('portable', [*py, str(ROOT / 'harness/test_portable_beta_install.py'), str(output / 'portable'),
                     '', str(a.python_embed.resolve())])
    # Remove the required-root guard in a scratch copy. The same test must go red.
    mutation = output / 'root-mutation/harness'
    mutation.mkdir(parents=True)
    text = (ROOT / 'harness/shim_install.py').read_text(encoding='utf-8')
    guard = '        if not opts.get("--root"):\n            raise Refused("--root is required")  # guard:explicit-root\n'
    if text.count(guard) != 1:
        raise SystemExit('Root guard mutation is not applicable')
    (mutation / 'shim_install.py').write_text(text.replace(guard, ''), encoding='utf-8')
    shutil.copyfile(ROOT / 'harness/test_publication.py', mutation / 'test_publication.py')
    shutil.copyfile(ROOT / 'harness/package_normal.py', mutation / 'package_normal.py')
    run('root-guard-mutation-red', [*py, str(mutation / 'test_publication.py')], expected=1)
    binding_mutation = output / 'binding-mutation/harness'
    binding_mutation.mkdir(parents=True)
    for name in ('test_publication.py', 'shim_install.py'):
        shutil.copyfile(ROOT / 'harness' / name, binding_mutation / name)
    text = (ROOT / 'harness/package_normal.py').read_text(encoding='utf-8')
    guard = 'if not source.is_file() or sha(source) != expected:  # guard:source-tree-binding'
    if text.count(guard) != 1:
        raise SystemExit('Source binding mutation is not applicable')
    (binding_mutation / 'package_normal.py').write_text(text.replace(guard, 'if False:'), encoding='utf-8')
    run('source-binding-mutation-red', [*py, str(binding_mutation / 'test_publication.py')], expected=1)
    for label, guard in (
        ('source-required-inputs', 'if not required.issubset(inputs):'),
        ('source-path-confinement', 'if rel.is_absolute() or ".." in rel.parts or ":" in name or not source.is_relative_to(repo):'),
    ):
        directory = output / (label + '-mutation/harness')
        directory.mkdir(parents=True)
        for name in ('test_publication.py', 'shim_install.py'):
            shutil.copyfile(ROOT / 'harness' / name, directory / name)
        if text.count(guard) != 1:
            raise SystemExit(label + ' mutation is not applicable')
        (directory / 'package_normal.py').write_text(text.replace(guard, 'if False:'), encoding='utf-8')
        run(label + '-mutation-red', [*py, str(directory / 'test_publication.py')], expected=1)
    run('dead-duplicate-guard-mutation-red', [*py, str(ROOT / 'harness/test_portable_beta_install.py'),
                                              str(output / 'dead-duplicate-mutation'),
                                              'excluded-platforms-wrappers-and-ambiguous-entries-refused',
                                              str(a.python_embed.resolve())], expected=1)
    receipt = {'proof': 'M/synthetic; no live LaunchBox XML, controllers or real emulator',
               'source_commit': manifest['base_commit'],
               'source_manifest_sha256': hashlib.sha256(manifest_path.read_bytes()).hexdigest().upper(),
               'results': results}
    (output / 'check-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    raise SystemExit(0 if all(item['passed'] for item in results.values()) else 1)


if __name__ == '__main__':
    main()
