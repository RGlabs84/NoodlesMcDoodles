"""Build the Hexium upload zip for NoodlesMcDoodles.

The zip holds HexiumDist's package files and the built DLL: flat, forward slashes, 1980-01-01 entry dates (the same
rules as BetterContinents10/tools/pack_hexium.py). The same zip is the tester/client package, and the DLL inside it
is the one to greylist in AzuAntiCheat (Greylist/Wubarrk-NoodlesMcDoodles-<version>/).

Before running: bump the version in src/Core/PluginInfo.cs, NoodlesMcDoodles.csproj and HexiumDist (manifest.json,
README badge line, CHANGELOG heading), then build Release.

Usage: python3 tools/pack_hexium.py [path/to/NoodlesMcDoodles.dll]
With a path, the DLL is first copied to HexiumDist/plugins/NoodlesMcDoodles.dll.
"""
import hashlib
import json
import pathlib
import shutil
import sys
import zipfile

REPO = pathlib.Path(__file__).resolve().parent.parent
DIST = REPO / 'HexiumDist'
DLL = DIST / 'plugins/NoodlesMcDoodles.dll'
EPOCH = (1980, 1, 1, 0, 0, 0)
GUID, NAME = b'wubarrk.noodlesmcdoodles', b'NoodlesMcDoodles'

if len(sys.argv) > 1:
    shutil.copyfile(sys.argv[1], DLL)
if not DLL.is_file():
    sys.exit(f'{DLL} not found: pass the built DLL (usage: top of tools/pack_hexium.py)')

manifest = json.loads((DIST / 'manifest.json').read_text(encoding='utf-8'))
version = manifest['version_number']
if len(manifest['description']) >= 250:
    sys.exit(f'manifest description is {len(manifest["description"])} characters: keep it under 250')

# [BepInPlugin(GUID, NAME, VERSION)] is stored as length-prefixed UTF-8 strings in the attribute blob.
data = DLL.read_bytes()
blob = b''.join(bytes([len(s)]) + s for s in (GUID, NAME, version.encode()))
if blob not in data:
    sys.exit(f'{DLL} is not NoodlesMcDoodles {version}: rebuild it after bumping PluginInfo.PLUGIN_VERSION')
for doc in ('README.md', 'CHANGELOG.md'):
    if version not in (DIST / doc).read_text(encoding='utf-8'):
        sys.exit(f'HexiumDist/{doc} does not mention {version}: update it')

members = {name: (DIST / name).read_bytes() for name in (
    'manifest.json', 'README.md', 'CHANGELOG.md', 'LICENSE.md', 'icon.png')}
members['plugins/NoodlesMcDoodles.dll'] = data

out = DIST / f'NoodlesMcDoodles-v{version}-hexium.zip'
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name, content in members.items():
        info = zipfile.ZipInfo(name, EPOCH)
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, content)
print(f'{out}: {out.stat().st_size / 1e3:.0f} kB, DLL sha256 {hashlib.sha256(data).hexdigest()}')
