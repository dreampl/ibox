import copy
import json
import sys
import tempfile
import threading
import time
import types
import unittest
from pathlib import Path
import ibox_account_control as api

class AccountControlTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.a = {'uid': 1, 'sid': 'a', 'user': {'id': 1}, 'dev': 'Laptop', 'at': time.time()}
        self.b = {'uid': 1, 'sid': 'b', 'user': {'id': 1}, 'dev': 'Phone', 'at': time.time()}
        self.other = {'uid': 2, 'sid': 'other', 'user': {'id': 2}, 'at': time.time()}
        self.doc = {'sessions': {'hash-a': self.a, 'hash-b': self.b, 'hash-other': self.other}}
        self.ibox = types.SimpleNamespace(_lk=threading.Lock(), TTL_SESSION=10000, doc=lambda: self.doc,
                         D={'DATA': self.temp.name, 'audit': lambda *args, **kwargs: None})
        api._rate.clear()
    def tearDown(self):
        self.temp.cleanup()
    def call(self, operation, request=None, session=None):
        return api.handle(self.ibox, operation, request or {}, 'unused-token-hash', session or self.a)
    def pulse(self, session=None, allowed=True):
        return self.call('pulse', {'allow_remote': allowed, 'state': 'Connected', 'profile_id': 'p1', 'profile_name': 'Test config', 'configs': [{'id': 'p1', 'name': 'Test config', 'password': 'must-not-leak'}]}, session or self.b)
    def test_state_is_account_scoped_and_never_exposes_session_tokens(self):
        self.pulse()
        status, state = self.call('state')
        self.assertEqual(status, 200)
        self.assertEqual({d['sid'] for d in state['devices']}, {'a', 'b'})
        serialized = json.dumps(state)
        self.assertNotIn('hash-a', serialized)
        self.assertNotIn('must-not-leak', serialized)
        self.assertNotIn('password', serialized)
    def test_other_account_cannot_target_a_device(self):
        self.pulse()
        self.assertEqual(self.call('command', {'sid': 'b', 'action': 'disconnect'}, self.other)[0], 404)
    def test_remote_control_requires_device_opt_in(self):
        self.pulse(allowed=False)
        self.assertEqual(self.call('command', {'sid': 'b', 'action': 'disconnect'})[0], 403)
    def test_connect_rejects_configs_not_on_the_target(self):
        self.pulse()
        self.assertEqual(self.call('command', {'sid': 'b', 'action': 'connect', 'profile_id': 'foreign'})[0], 400)
    def test_command_delivery_ack_and_expiration(self):
        self.pulse()
        status, command = self.call('command', {'sid': 'b', 'action': 'connect', 'profile_id': 'p1'})
        self.assertEqual(status, 202)
        _, response = self.pulse()
        self.assertEqual([c['id'] for c in response['commands']], [command['id']])
        _, response = self.call('pulse', {'acknowledgements': [{'id': command['id'], 'result': 'done'}]}, self.b)
        self.assertEqual(response['commands'], [])
    def test_revoked_sender_cannot_deliver_a_queued_command(self):
        self.pulse()
        self.call('command', {'sid': 'b', 'action': 'disconnect'})
        self.doc['sessions'].pop('hash-a')
        self.assertEqual(self.pulse()[1]['commands'], [])
    def test_offline_device_does_not_accumulate_future_commands(self):
        self.pulse()
        path, data = api.load(self.ibox)
        data['b']['last_seen'] = time.time() - 100
        api.save(path, data)
        self.assertEqual(self.call('command', {'sid': 'b', 'action': 'disconnect'})[0], 409)
    def test_expired_commands_are_not_delivered(self):
        self.pulse()
        self.call('command', {'sid': 'b', 'action': 'disconnect'})
        path, data = api.load(self.ibox)
        data['b']['commands'][0]['created'] -= 300
        api.save(path, data)
        self.assertEqual(self.pulse()[1]['commands'], [])
    def test_unknown_actions_are_rejected(self):
        self.pulse()
        self.assertEqual(self.call('command', {'sid': 'b', 'action': 'run_shell'})[0], 400)
    def test_pending_command_queue_is_bounded(self):
        self.pulse()
        for _ in range(3): self.assertEqual(self.call('command', {'sid': 'b', 'action': 'disconnect'})[0], 202)
        self.assertEqual(self.call('command', {'sid': 'b', 'action': 'disconnect'})[0], 409)
    def test_family_members_cannot_call_admin_or_create_actions(self):
        family = {'id': 'f1', 'owner': 'head', 'members': [{'key': 'head'}, {'key': 'child'}]}
        module = types.SimpleNamespace(families_view=lambda: [copy.deepcopy(family)], doc=lambda: {'families': {'f1': {'app_permissions': {'owner_telegram_id': 10, 'member_telegram_ids': [1]}}}}, person_parts=lambda name: [('nl', 10)])
        self.ibox.D['sp_api_as'] = lambda *args: (200, {'subs': [{'data': {'username': 'child'}}]})
        old = sys.modules.get('jacki_nex')
        sys.modules['jacki_nex'] = module
        try:
            status, body = self.call('family')
            self.assertEqual(status, 200)
            self.assertFalse(body['families'][0]['permissions']['manage'])
            self.assertEqual(self.call('family', {'action': 'create_account', 'id': 'f1'})[0], 403)
            self.assertEqual(self.call('family', {'action': 'delete', 'id': 'f1'})[0], 403)
        finally:
            if old is None: sys.modules.pop('jacki_nex')
            else: sys.modules['jacki_nex'] = old
    def test_administrator_grant_has_a_bounded_budget_and_preserves_other_family_actions(self):
        doc = {'families': {'f1': {}}}
        calls = []
        module = types.SimpleNamespace(doc=lambda: doc, _lk=threading.RLock(), save_quiet=lambda: None,
                   family_admin=lambda request: calls.append(request) or 'original', families_view=lambda: [{'id': 'f1'}])
        api.install_family_permission_api(module)
        with self.assertRaises(ValueError):
            module.family_admin({'action': 'app_permissions', 'id': 'f1', 'permissions': {'create_account': True, 'owner_telegram_id': 1, 'max_members': 8, 'max_member_gb': 0}})
        module.family_admin({'action': 'app_permissions', 'id': 'f1', 'permissions': {'create_account': True, 'owner_telegram_id': 1, 'max_members': 8, 'max_member_gb': 25}})
        self.assertEqual(module.families_view()[0]['app_permissions']['max_member_gb'], 25)
        self.assertEqual(module.family_admin({'action': 'rename'}), 'original')
        self.assertEqual(len(calls), 1)

    def test_matching_usernames_never_grant_family_access_to_another_telegram_identity(self):
        family = {'id': 'f1', 'owner': 'same-name', 'members': [{'key': 'same-name'}]}
        module = types.SimpleNamespace(families_view=lambda: [copy.deepcopy(family)], doc=lambda: {'families': {'f1': {'app_permissions': {'owner_telegram_id': 99}}}})
        self.ibox.D['sp_api_as'] = lambda *args: (200, {'subs': [{'data': {'username': 'same-name'}}]})
        old = sys.modules.get('jacki_nex')
        sys.modules['jacki_nex'] = module
        try:
            self.assertEqual(self.call('family')[1]['families'], [])
            self.assertEqual(self.call('family', {'action': 'cap', 'id': 'f1', 'username': 'same-name', 'cap_gb': 0})[0], 403)
        finally:
            if old is None: sys.modules.pop('jacki_nex')
            else: sys.modules['jacki_nex'] = old

    def test_family_head_cannot_create_without_explicit_grant(self):
        module = types.SimpleNamespace(doc=lambda: {'families': {'f1': {}}})
        with self.assertRaisesRegex(ValueError, 'not been granted'):
            api.create_family_account(module, {'id': 'f1'}, {})

if __name__ == '__main__': unittest.main()
