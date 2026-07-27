# GuiyangMahjong.PlayerData

PlayerData is the authoritative player wallet/reward ledger and the sanitized
evidence ingress for the administration application.

It provides separate least-privilege credentials for:

- reward, payment, report, and replay source ingestion;
- approved Admin wallet commands;
- chat send authorization;
- balance monitoring;
- evidence projection to Admin.

Reward claims, compensation, reward reversal, balance version updates, evidence
creation, and projection Outbox creation are committed atomically in PostgreSQL.
Every source event and Admin command is idempotent. Compensation requires a
different requester and approver; reward reversal must reference an existing
claimed reward and cannot make the balance negative. There is no match-result
mutation endpoint.

Production requires PostgreSQL, all six distinct 32+ character credentials, and
Admin evidence projection. Chat authorization queries the current Auth control
state and fails closed if Auth is unavailable.

Main internal endpoints:

- `POST /internal/sources/reward-claims`
- `POST /internal/sources/payment-orders`
- `POST /internal/sources/reports`
- `POST /internal/sources/replays`
- `POST /internal/admin/wallet-operations`
- `POST /internal/chat/messages/authorize`
- `GET /internal/monitoring/players/{playerId}/balances`

Use `Deploy/linux/compose.yaml` or `Deploy/kubernetes/player-data.yaml` as the
deployment baseline. Replace every example credential before enabling Admin
command execution.
