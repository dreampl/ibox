"""Account-scoped IBOX device control; installed beside jacki_ibox, not an admin proxy."""
import copy
import datetime as dt
import json
import os
import re
import secrets
import threading
import time

_lock = threading.RLock()
_family_lock = threading.RLock()
_rate = {}
ONLINE_SECONDS = 50
COMMAND_SECONDS = 120

def text(value, limit=160):
    return ''.join(c for c in str(value or '') if c >= ' ' and c != '\x7f')[:limit]

def iso(timestamp):
    return dt.datetime.fromtimestamp(timestamp, dt.timezone.utc).isoformat()

def sessions(ibox, uid):
    with ibox._lk:
        return {x['sid']: copy.deepcopy(x) for x in ibox.doc()['sessions'].values()
                if x.get('uid') == uid and time.time() - x.get('seen', x.get('at', 0)) < ibox.TTL_SESSION}

def load(ibox):
    path = os.path.join(ibox.D['DATA'], 'ibox-control.json')
    try:
        with open(path, encoding='utf-8') as f:
            data = json.load(f)
        return path, data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return path, {}

def save(path, data):
    temp = path + '.new'
    fd = os.open(temp, os.O_CREAT | os.O_TRUNC | os.O_WRONLY, 0o600)
    with os.fdopen(fd, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, separators=(',', ':'))
        f.flush()
        os.fsync(f.fileno())
    os.replace(temp, path)

def view(data, owned, me):
    now = time.time()
    devices = []
    for sid, session in owned.items():
        row = copy.deepcopy(data.get(sid) or {})
        row.update(sid=sid, name=row.get('name') or session.get('dev') or 'IBOX',
                   me=sid == me, online=now - row.get('last_seen', 0) < ONLINE_SECONDS)
        row.setdefault('platform', '')
        row.setdefault('state', 'Disconnected')
        row.setdefault('configs', [])
        own_commands = [command for command in row.pop('commands', []) if command.get('sender') == me]
        row['last_command_result'] = own_commands[-1].get('result', 'queued') if own_commands else None
        devices.append(row)
    return {'devices': sorted(devices, key=lambda row: (not row['me'], not row['online'], row['name'])), 'server_time': iso(now)}

def authorize_rate(uid):
    now = time.time()
    history = [stamp for stamp in _rate.get(uid, []) if now - stamp < 60]
    if len(history) >= 20:
        return False
    _rate[uid] = history + [now]
    return True

def handle(ibox, operation, req, key, session):
    """Caller must first authenticate Bearer with jacki_ibox.session; never accepts a UID from the request."""
    uid, me = session['uid'], session['sid']
    if operation == 'family':
        with _family_lock:
            return family(ibox, session['user'], req)
    owned = sessions(ibox, uid)
    with _lock:
        path, data = load(ibox)
        if operation == 'state':
            return 200, view(data, owned, me)
        if operation == 'pulse':
            configs = req.get('configs') or []
            if not isinstance(configs, list) or len(configs) > 200:
                return 400, {'error': 'Too many device configs'}
            row = data.setdefault(me, {})
            row.update(name=text(req.get('name'), 100), platform=text(req.get('platform'), 24), version=text(req.get('version'), 32),
                       allow_remote=req.get('allow_remote') is True, state=text(req.get('state'), 24),
                       profile_id=text(req.get('profile_id'), 100) or None, profile_name=text(req.get('profile_name')) or None,
                       connected_since=text(req.get('connected_since'), 48) or None, last_seen=time.time(), uid=uid,
                       configs=[{'id': text(c.get('id'), 100), 'name': text(c.get('name'))} for c in configs if isinstance(c, dict) and c.get('id')])
            acknowledgements = req.get('acknowledgements') or []
            if not isinstance(acknowledgements, list) or len(acknowledgements) > 128:
                return 400, {'error': 'Invalid acknowledgements'}
            commands = row.setdefault('commands', [])
            for ack in acknowledgements:
                if not isinstance(ack, dict):
                    continue
                for command in commands:
                    if command['id'] == ack.get('id'):
                        command['result'] = text(ack.get('result'), 40)
            now = time.time()
            row['commands'] = [command for command in commands if now - command['created'] < 3600][-100:]
            result = view(data, owned, me)
            result['commands'] = [{k: v for k, v in command.items() if k != 'sender'} for command in row['commands']
                                  if command.get('result') is None and now < command['created'] + COMMAND_SECONDS and command['sender'] in owned]
            # Bound disk retention after revoked/expired sessions, without exposing other accounts in the response.
            for sid in list(data):
                if now - data[sid].get('last_seen', 0) > 7 * 86400:
                    data.pop(sid)
            save(path, data)
            if hasattr(ibox, '_people'):
                with ibox._lk:
                    person = ibox._people().get(str(uid)) or {}
                    if time.time() - person.get('last_seen', 0) >= 60:
                        ibox._remember(session['user'], row['name'], person.get('via') or 'app')
                        ibox.save()
            return 200, result
        if operation == 'command':
            if not authorize_rate(uid):
                return 429, {'error': 'Too many commands; try again in a minute'}
            target = text(req.get('sid'), 100)
            if target not in owned:
                return 404, {'error': 'Device not found on your account'}
            row = data.get(target) or {}
            if not row.get('allow_remote'):
                return 403, {'error': 'Enable remote control on the target device first'}
            if time.time() - row.get('last_seen', 0) >= ONLINE_SECONDS:
                return 409, {'error': 'Device is offline; no command queued'}
            action, profile = req.get('action'), text(req.get('profile_id'), 100) or None
            if action not in ('connect', 'disconnect'):
                return 400, {'error': 'Unsupported device action'}
            if action == 'connect' and profile not in {c['id'] for c in row.get('configs', [])}:
                return 400, {'error': 'Choose an existing config on that device'}
            now = time.time()
            command = dict(id=secrets.token_urlsafe(18), sender=me, action=action, profile_id=profile, created=now, expires=iso(now + COMMAND_SECONDS))
            queued = row.setdefault('commands', [])
            if sum(c.get('result') is None and now < c['created'] + COMMAND_SECONDS for c in queued) >= 3:
                return 409, {'error': 'Wait for the pending device command'}
            queued.append(command)
            save(path, data)
            ibox.D['audit']('ibox_device_command', 'ibox', str(uid), action=action, device=target)
            return 202, {'id': command['id'], 'status': 'queued', 'expires': command['expires']}
        return 404, {'error': 'Unsupported account control operation'}

def family(ibox, user, req):
    import jacki_nex as nex
    install_family_permission_api(nex)
    status, app = ibox.D['sp_api_as']('app', {}, user, '')
    if status != 200:
        return status, app
    names = {str((sub.get('data') or {}).get('username') or '').lower() for sub in app.get('subs', [])}
    names.discard('')
    def visible(f):
        grant = nex.doc().get('families', {}).get(f['id'], {}).get('app_permissions') or {}
        assigned = user['id'] == grant.get('owner_telegram_id') or user['id'] in (grant.get('member_telegram_ids') or [])
        return assigned and any(m.get('key') in names for m in f['members'])
    allowed = [f for f in nex.families_view() if visible(f)]
    action = req.get('action')
    if action:
        fid = text(req.get('id'), 40)
        f = next((f for f in allowed if f['id'] == fid), None)
        grant = nex.doc().get('families', {}).get(fid, {}).get('app_permissions') or {}
        if not f or f['owner'] not in names or grant.get('owner_telegram_id') != user['id']:
            return 403, {'error': 'Only the linked family head can manage this family'}
        if not authorize_rate(user['id']):
            return 429, {'error': 'Too many family changes'}
        if action not in ('rename', 'cap', 'remove', 'add', 'create_account', 'member_link'):
            return 403, {'error': 'This action requires the service administrator'}
        target = str(req.get('username') or '').lower()
        if action in ('cap', 'remove', 'member_link') and target not in {m['key'] for m in f['members']}:
            return 403, {'error': 'Member is not in your family'}
        if action == 'add' and target not in names:
            return 403, {'error': 'Only another Nex account linked to you may be added'}
        try:
            if action == 'member_link':
                parts = nex.person_parts(target)
                if not parts:
                    return 404, {'error': 'Member subscription was not found'}
                backend, account_id = parts[0]
                link = nex.combo_url(backend, account_id)
                if not link:
                    links = (nex.D['subs_call'](backend, 'POST', '/action', {'id': account_id, 'action': 'link'}) or {}).get('links') or {}
                    link = links.get('iran') or links.get('panel')
                if not isinstance(link, str) or not link.startswith('https://'):
                    return 404, {'error': 'Subscription link is unavailable'}
                return 200, {'link': link}
            if action == 'create_account':
                create_family_account(nex, f, req)
            else:
                safe = {k: req[k] for k in ('action', 'id', 'name', 'username', 'cap_gb') if k in req}
                nex.family_admin(safe)
            nex.family_sync()
            ibox.D['audit']('ibox_family', 'ibox', str(user['id']), action=action, family=fid)
        except (ValueError, RuntimeError) as error:
            return 400, {'error': str(error)[:200]}
        allowed = [f for f in nex.families_view() if visible(f)]
    for f in allowed:
        grant = nex.doc().get('families', {}).get(f['id'], {}).get('app_permissions') or {}
        head = f['owner'] in names and grant.get('owner_telegram_id') == user['id']
        f.pop('app_permissions', None)
        f['permissions'] = {'manage': head, 'create_account': head and grant.get('create_account') is True,
                            'max_members': min(int(grant.get('max_members') or 16), 16),
                            'max_member_gb': max(0, float(grant.get('max_member_gb') or 0)),
                            'backends': [b for b, _ in nex.person_parts(f['owner'])] if head else []}
    return 200, {'families': allowed}

def create_family_account(nex, family, request):
    """Provision only when the administrator explicitly grants a family head a bounded account budget."""
    grant = nex.doc().get('families', {}).get(family['id'], {}).get('app_permissions') or {}
    if grant.get('create_account') is not True:
        raise ValueError('Account creation has not been granted to this family')
    if len(family['members']) >= min(int(grant.get('max_members') or 16), 16):
        raise ValueError('Family member slots are full')
    backend = str(request.get('backend') or '')
    parts = nex.person_parts(family['owner'])
    if backend not in {b for b, _ in parts}:
        raise ValueError('Backend is outside this family service')
    volume = request.get('cap_gb')
    maximum = float(grant.get('max_member_gb') or 0)
    if type(volume) not in (int, float) or not 0 < float(volume) <= maximum:
        raise ValueError('Choose a volume within the granted member budget')
    name = 'f' + family['id'] + '_' + secrets.token_hex(4)
    template_id = next(uid for b, uid in parts if b == backend)
    source = next((u for u in nex._users_of(backend) if u.get('id') == template_id), None)
    if not source:
        raise ValueError('Family head account was not found')
    group_ids = [g['id'] for g in nex.D['subs_call'](backend, 'GET', '/meta').get('groups', []) if g.get('name') in (source.get('groups') or [])]
    if not group_ids:
        raise ValueError('No service groups are available for this family')
    body = dict(username=name, group_ids=group_ids, data_limit_gb=float(volume), note='IBOX family ' + family['id'], by='ibox-family')
    expiry = int(source.get('expire') or 0)
    if expiry:
        if expiry <= time.time():
            raise ValueError('Family service expired')
        body.update(expire_mode='date', expire_ts=expiry)
    else:
        body['expire_mode'] = 'never'
    result = nex.D['subs_call'](backend, 'POST', '/create', body) or {}
    if result.get('error') or result.get('detail'):
        raise ValueError(text(result.get('error') or result.get('detail')))
    account = next((u for u in nex._users_of(backend) if str(u.get('username', '')).lower() == name), None)
    if not account:
        raise ValueError('The account was not created')
    try:
        nex.add_member(backend, account['id'], name)
        nex.family_admin({'action': 'add', 'id': family['id'], 'username': name, 'cap_gb': float(volume)})
    except Exception:
        nex.D['subs_call'](backend, 'POST', '/action', {'id': account['id'], 'action': 'disable'})
        raise ValueError('New account was disabled because family assignment failed; contact your provider') from None


def install_family_permission_api(nex):
    """Extend only the existing administrator route; normal app callers cannot select this action."""
    if getattr(nex, '_ibox_permissions_wrapped', False) or not callable(getattr(nex, 'family_admin', None)):
        return
    original_admin, original_view = nex.family_admin, nex.families_view
    def admin(req):
        if req.get('action') != 'app_permissions':
            return original_admin(req)
        fid = str(req.get('id') or '')
        permissions = req.get('permissions') or {}
        if not isinstance(permissions, dict):
            raise ValueError('Invalid IBOX family permissions')
        create = permissions.get('create_account') is True
        owner_uid = permissions.get('owner_telegram_id', 0)
        member_uids = permissions.get('member_telegram_ids') or []
        if type(owner_uid) is not int or owner_uid < 0 or create and owner_uid <= 0:
            raise ValueError('Link the verified Telegram ID of the family head first')
        if not isinstance(member_uids, list) or len(member_uids) > 16 or any(type(uid) is not int or uid <= 0 for uid in member_uids):
            raise ValueError('Invalid family Telegram member IDs')
        maximum = permissions.get('max_member_gb', 0)
        slots = permissions.get('max_members', 16)
        if type(slots) is not int or not 1 <= slots <= 16:
            raise ValueError('Family slots must be between 1 and 16')
        if type(maximum) not in (int, float) or not 0 <= float(maximum) <= 100000 or create and float(maximum) <= 0:
            raise ValueError('Set a positive member budget before granting account creation')
        with nex._lk:
            f = nex.doc().get('families', {}).get(fid)
            if f is None:
                raise ValueError('Family not found')
            f['app_permissions'] = dict(create_account=create, max_members=slots, max_member_gb=float(maximum), owner_telegram_id=owner_uid, member_telegram_ids=sorted(set(member_uids)))
            nex.save_quiet()
        return fid
    def families_view():
        result = original_view()
        for f in result:
            f['app_permissions'] = copy.deepcopy(nex.doc().get('families', {}).get(f['id'], {}).get('app_permissions') or {})
        return result
    nex.family_admin, nex.families_view = admin, families_view
    nex._ibox_permissions_wrapped = True


# The add-on is imported only after the authenticated HTTP dispatch; Nex is already bound then.
try:
    import jacki_nex as _nex
except ImportError:
    _nex = None
if _nex is not None:
    install_family_permission_api(_nex)
