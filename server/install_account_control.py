"""Install the authenticated account-control dispatch without replacing unrelated panel code."""
import datetime
import os
from pathlib import Path
import py_compile
import shutil
import sys

for argument in sys.argv[1:]:
    path = Path(argument)
    original = path.read_bytes()
    source = original.decode('utf-8')
    marker = '        u = s["user"]\n'
    addition = (marker + '        if sub.startswith("control/"):\n'
                + '            import jacki_ibox_control\n'
                + '            return h.send(*jacki_ibox_control.handle(sys.modules[__name__], sub[8:], req, key, s))\n')
    # This module already imports its standard-library dependencies at the top.
    addition = addition.replace('sys.modules[__name__]', '__import__(__name__)')
    if path.name == 'jacki-dash.py':
        marker = '        mi = re.fullmatch(r"/sub/_jp/api/ibox/(login/start|login/poll|oidc/start|music/ticket|logout|sessions|app|req|receipt|accept|cancel|dnsreq|join|nex|nexallow|nexexit)", p)'
        addition = marker.replace('nexexit)', 'nexexit|control/(?:state|pulse|command|family))')
        installed = 'control/(?:state|pulse|command|family)' in source
    else:
        installed = 'jacki_ibox_control.handle' in source
    if installed:
        print(str(path) + ': dispatch already installed')
        continue
    if source.count(marker) != 1:
        raise SystemExit('Panel dispatch changed; review the integration before installing')
    staged = path.with_name(path.name + '.control-new')
    staged.write_text(source.replace(marker, addition, 1), encoding='utf-8')
    py_compile.compile(str(staged), doraise=True)
    if path.read_bytes() != original:
        staged.unlink()
        raise SystemExit('Panel file changed during installation; no replacement made')
    backup = path.with_name(path.name + '.bak-control-' + datetime.datetime.now().strftime('%Y%m%d%H%M%S'))
    shutil.copy2(path, backup)
    shutil.copymode(path, staged)
    os.replace(staged, path)
    print(str(path) + ': account control installed; original backed up')
