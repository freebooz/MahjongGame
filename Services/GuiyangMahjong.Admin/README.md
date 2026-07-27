# Guiyang Mahjong Admin

Independent player, room, and Dedicated Server monitoring application with a
controlled, audited management workflow.

The complete player-management security design and phased delivery boundary are
documented in `../../Docs/PLAYER_MONITORING_ADMIN_DESIGN.md`.

## Current scope

- Room totals and grouping by lifecycle, game mode, and cluster.
- Room, rules, player membership, cluster, node, and server instance details.
- Dedicated Server process, route, build, registration, and heartbeat status.
- Realtime room telemetry: current round, game start time, server tick, server FPS,
  process memory, cumulative RPC count, player connection state, and latency.
- Room lifecycle and player connection timeline.
- Player account directory, online presence, current lobby/room/server, latency,
  masked device and IP data, active-session count, and login history.
- Per-player session, known-device, room, and disconnect history. Session IDs are
  shortened references and access/refresh tokens are never exposed.
- Player search by player ID or display name.
- Separate read-only credentials for Admin-to-Lobby and Admin-to-Allocator calls.
- Role-scoped room management requests with a mandatory second confirmation.
- Role-scoped player actions for logout/session reset, sanctions, risk tagging,
  compensation/reward reversal, replay access, and support-ticket creation.
- Separate-person approval, state-sequence conflict detection, expiry, and
  SHA-256 hash-chained audit records.
- Player actions use a SHA-256 state fingerprint over masked account, session,
  online, room, and server state; confirmation or approval fails if it changes.
- Approved requests create one durable command Outbox record in the same
  PostgreSQL transaction as the approval and audit entry.
- A lease-based dispatcher supports competing consumers, expired-lease
  recovery, bounded exponential retry, terminal failure, and atomic final
  action/Outbox/audit updates.
- `ForceLogoutPlayer` and `ResetAbnormalPlayerSession` now have idempotent Auth
  and Lobby adapters. Auth revokes refresh sessions, while Lobby rejects access
  tokens issued before the command and disconnects matching WebSockets across
  replicas.
- `TemporaryFreezePlayer`, `PermanentBanPlayer`, `LiftPlayerBan`,
  `MutePlayer`, `UnmutePlayer`, and `MarkRiskAccount` use a versioned,
  idempotent Auth control ledger. Freeze and ban atomically revoke refresh
  sessions, block new login/session creation, and trigger cross-replica Lobby
  disconnection.
- Player monitoring reports the effective account state, control version,
  freeze/mute expiry, active risk labels, and append-only control history.
  Control-history reasons, operator identities, tickets, and TraceIds are
  redacted unless the viewer is a sanction/risk operator, player approver, or
  auditor.
- `MarkRoomAbnormal` and `ProhibitNewPlayers` have idempotent Lobby adapters.
  They require the approved room state sequence; stale commands fail without
  mutation. Admission control permits existing members to reconnect but rejects
  new room members.
- `ForceDissolveRoom` transitions an approved non-terminal room to `Failed`,
  clears its live route, blocks new joins, marks it abnormal, publishes the
  room event, and requests allocator drain. Repeated delivery is idempotent.
- `TerminateAbnormalServer` routes to the allocator that owns the approved
  cluster/node snapshot. The allocator re-checks the expected instance state,
  stops the process or Agones resource, releases the port, and accepts retries
  after the instance is already `Stopped`.
- Allocator mutations use a dedicated management credential that is rejected
  on monitoring and ordinary allocation paths. The web console exposes server
  termination only to `infrastructure.operator`.
- Other high-privilege Lobby/Auth/Allocator command adapters are not configured
  yet, so production command execution remains hard-blocked during staged
  rollout.
- Match-result mutation is absent from the action type model and rejected by API binding.

## Local configuration

Set values through environment variables or user secrets. Never commit real tokens.

```text
Admin__ReadOnlyAccessToken=<32+ random characters>
Admin__Management__Enabled=false
Admin__Management__ExecutionEnabled=false
Admin__Management__PollIntervalMilliseconds=1000
Admin__Management__LeaseSeconds=30
Admin__Management__MaxAttempts=5
Admin__Management__RetryBaseSeconds=5
Admin__Management__AuthCommandToken=<dedicated 32+ Auth command token>
Admin__Management__LobbyCommandToken=<different dedicated 32+ Lobby command token>
Admin__Management__CommandTimeoutSeconds=5
Admin__Management__TemporaryFreezeHours=24
Admin__Management__MuteHours=24
Admin__Management__RiskLabelTtlDays=30
Admin__Management__PersistenceMode=Postgres
Admin__Management__PostgresConnectionString=<secret PostgreSQL connection string>
Admin__Principals__0__OperatorId=<stable enterprise operator id>
Admin__Principals__0__AccessToken=<32+ operator token>
Admin__Principals__0__Roles__0=room.viewer
Admin__Principals__0__Roles__1=room.operator
Admin__Principals__1__OperatorId=<different approver id>
Admin__Principals__1__AccessToken=<different 32+ approver token>
Admin__Principals__1__Roles__0=room.approver
Admin__Principals__1__Roles__1=audit.viewer
Admin__Principals__2__OperatorId=<player operator id>
Admin__Principals__2__AccessToken=<different 32+ player operator token>
Admin__Principals__2__Roles__0=player.viewer
Admin__Principals__2__Roles__1=player.operator
Admin__Principals__3__OperatorId=<different player approver id>
Admin__Principals__3__AccessToken=<different 32+ player approver token>
Admin__Principals__3__Roles__0=player.viewer
Admin__Principals__3__Roles__1=player.approver
Admin__Principals__4__OperatorId=<infrastructure operator id>
Admin__Principals__4__AccessToken=<different 32+ infrastructure operator token>
Admin__Principals__4__Roles__0=room.viewer
Admin__Principals__4__Roles__1=infrastructure.operator
Admin__Principals__5__OperatorId=<sanction operator id>
Admin__Principals__5__AccessToken=<different 32+ sanction operator token>
Admin__Principals__5__Roles__0=player.viewer
Admin__Principals__5__Roles__1=sanction.operator
Admin__Principals__6__OperatorId=<risk analyst id>
Admin__Principals__6__AccessToken=<different 32+ risk analyst token>
Admin__Principals__6__Roles__0=player.viewer
Admin__Principals__6__Roles__1=risk.analyst
Admin__Auth__MonitoringToken=<read-only token configured on Auth>
Admin__Lobby__MonitoringToken=<different 32+ random characters>
Admin__Allocators__0__MonitoringToken=<same read-only monitoring token configured on Allocator>
Admin__Allocators__0__ManagementCommandToken=<dedicated 32+ token configured on that Allocator>
```

The web console is served from `/`; health endpoints are `/health/live` and
`/health/ready`. Monitoring APIs are under `/admin/v1` and require the Admin
read-only Bearer token.

## Security boundary

The Admin process does not receive Auth signing material, plaintext device
installation IDs, player access/refresh tokens, the Lobby internal settlement credential, or
the Allocator service token used by allocation and drain commands. The
monitoring credentials can only read explicitly scoped internal monitoring
endpoints; attempts to use them for mutation operations are rejected.

Account state and player controls are sourced from the Auth control ledger.
Payment, assets, rewards, chat content, reports, replay evidence, and support
ticket systems remain outside this delivery stage. Mute state is authoritative
and visible to downstream services, but enforcement awaits the introduction of
the chat service.

The deployment configuration keeps management disabled by default. PostgreSQL
mode transactionally persists the request transition, separate approval,
hash-chained audit entry, and one unique pending command Outbox record. Service
readiness now includes the management store.

The command dispatcher is implemented but disabled by default. It claims
commands with `FOR UPDATE SKIP LOCKED`, assigns a renewable ownership lease,
reclaims expired work, and records each retry or terminal outcome in the audit
chain. Every domain adapter must use `OutboxId` as its idempotency key and
re-check the room sequence or player state fingerprint before changing domain
state.

Room dissolution and instance termination are deliberately separate controls:
dissolving the authoritative room requests a normal allocator drain, while the
infrastructure-only termination action provides a separately approved recovery
path for an abnormal server. Neither action can alter match results.

The player-session adapter sends the same `OutboxId` to Auth and Lobby. Auth
stores a durable command receipt in PostgreSQL in the same transaction that
revokes refresh sessions. Lobby stores a monotonic access-token revocation
cutoff in Redis, removes online presence, and broadcasts a targeted disconnect
to every Lobby replica. Tokens issued after that cutoff remain valid so the
player can authenticate again unless a separate account sanction applies.

Player-control commands carry the control version captured before confirmation.
Auth locks the player identity, re-checks that version, updates the projection,
appends the domain event, and revokes sessions in one PostgreSQL transaction.
Re-delivery with the same Outbox ID returns the original result; reusing that ID
with different parameters is rejected.

Before enabling command execution, replace development tokens with enterprise
OIDC/MFA identities, replicate the audit chain to WORM storage, and attach
idempotent domain adapters using dedicated least-privilege command credentials.
Production startup currently rejects `ExecutionEnabled=true`; remove that
safety gate only when those adapters and their integration tests are present.
Production startup also rejects enabled management unless PostgreSQL
persistence is configured.

Additional least-privilege player roles are `sanction.operator`,
`risk.analyst`, `support.operator`, and `compensation.operator`. Do not assign
them to the same person by default; provision only the role required for the
person's job function.
