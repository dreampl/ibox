# IBOX account control 2.3.5

This module extends the existing authenticated IBOX session API. It is not a dashboard-admin API and accepts no user-selected Telegram identity. `jacki_ibox.session` authenticates the Bearer token; all device operations are scoped to that session's Telegram UID.

## Endpoints

All requests are JSON POSTs under `/sub/_jp/api/ibox/control/`:

| Endpoint | Purpose |
| --- | --- |
| `state` | Devices belonging to the authenticated account, including online state, local config labels and connection start time. |
| `pulse` | Device metadata/status, remote-control opt-in, bounded config catalog and command acknowledgements. Returns pending commands for this session only. |
| `command` | `{sid, action: connect|disconnect, profile_id?}`. Only online devices that opted in accept commands; a connect must reference an ID in that device's advertised config catalog. |
| `family` | Lists linked families. Authorized family heads can rename, set member caps, remove members, add another Nex account linked to their identity, and obtain member subscription links. |

Session revocation continues to use the existing `sessions` endpoint with `{revoke: sid}`. The client detects a revoked session on its next account pulse and disconnects its active tunnel.

The client polls every 15 seconds. Devices are considered offline after 50 seconds without a pulse. Commands expire after 120 seconds, are bounded to three outstanding requests per target, and are rejected after the issuing session is revoked. Clients persist acknowledgement IDs before execution to avoid replay after a restart. Android must remain running with network access; this does not wake an app killed or suspended by the OS. VPN permission must already have been granted on Android.

## Identity and stored data

The existing sign-in service already records the Telegram identity verified by Telegram. Its owner-facing list remains `GET /dashboard/api/ibox/people`. The new pulse refreshes its last-seen/device metadata using the server-verified session identity, never a client-provided identity claim.

The control service stores device labels, platform/version, connection state and timestamps, config IDs/names, and bounded command history in `DATA/ibox-control.json` with mode `0600`. It does not upload config URIs, private keys, passwords, music tokens or Telegram session credentials. Phone sharing remains the existing optional Telegram contact flow. Telegram does not provide arbitrary private account data through this login.

## Nex Family permissions

Families and usage enforcement remain in `jacki_nex`; shared family caps remain authoritative. Family access requires explicitly assigned Telegram IDs in `app_permissions`, in addition to subscription membership returned by the authenticated `sp_api_as('app', ...)` view. Assigned members can read their family; only `owner_telegram_id` can manage it. Matching usernames on different backends never grant access by themselves. Creating families, increasing purchased total volume and deleting a family remain administrator operations.

Creating new service accounts requires an explicit administrator grant on the family's stored record in `nex.json`:

```json
{
  "app_permissions": {
    "owner_telegram_id": 123456789,
    "member_telegram_ids": [234567890],
    "create_account": true,
    "max_members": 8,
    "max_member_gb": 25
  }
}
```

Unassigned Telegram IDs have no client access. Obtain the head/member numeric IDs from the verified IBOX Identities list, not from unverified client text. The defaults do not grant creation. Slots are capped at 16, and new accounts can only be created on a backend already present in the family head's service. Group assignment and expiry follow that head's account; requested volume must be positive and within `max_member_gb`. A failed family assignment disables the newly provisioned account. Backend enforcement also applies to direct API requests, independently of button visibility.

The existing administrator-only `POST /dashboard/api/nex/family` route accepts `{action: "app_permissions", id, permissions: {...}}` after the account-control family adapter is loaded. The Nex admin response includes each family's `app_permissions`. The normal IBOX `control/family` route explicitly rejects this action.

The ongoing `panel-next` Family UI can expose these fields for administrators; it must keep them out of normal member writes. No admin credential is bundled with the client.

## Deployment

Install `ibox_account_control.py` as `/usr/local/lib/jacki/jacki_ibox_control.py` and retain a copy under `/root/nex-work/` for deployment backups. Run the installer against the active IBOX module and main request allowlist:

```sh
python3 install_account_control.py /root/nex-work/jacki_ibox.py /usr/local/lib/jacki/jacki_ibox.py /usr/local/sbin/jacki-dash.py
systemctl restart jacki-dash
```

The installer backs up each changed file, checks compilation, rejects an unexpected dispatcher shape, and checks for concurrent edits before replacement. Keep this integration step in future panel deployment chains so a panel rebuild retains the four new authenticated routes. A deployment can update UI files without touching this module.

## Verification

```sh
python -m unittest discover -s server -p 'test_*.py' -v
```

Tests cover cross-account isolation, absent local opt-in, foreign configs, offline devices, queue limits, expiration, acknowledgements, revoked issuing sessions and family authorization. Production smoke checks use read-only `state`, `family`, the existing `sessions` endpoint, and an invalid-target command that must be rejected. Do not exercise destructive actions against real customer accounts as a smoke test.
