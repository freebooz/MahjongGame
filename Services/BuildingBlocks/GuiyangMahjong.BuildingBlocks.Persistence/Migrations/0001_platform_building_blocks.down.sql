-- 阶段 2 基础构件逆向迁移模板。
-- 执行前必须停止消费和写请求并完成数据保留审批；删除 Inbox 会失去历史去重证据。
-- 只删除本基础构件拥有的表，保留消费服务 Schema 和业务表。

DROP TABLE IF EXISTS "__SCHEMA__".platform_idempotency;
DROP TABLE IF EXISTS "__SCHEMA__".platform_inbox;
DROP TABLE IF EXISTS "__SCHEMA__".platform_outbox_archive;
DROP TABLE IF EXISTS "__SCHEMA__".platform_outbox;
