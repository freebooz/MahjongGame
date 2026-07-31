-- 阶段 3 升级入口（psql 执行）。
-- 使用 \ir 相对当前脚本定位权威 Schema，保证新装和原位升级采用完全相同的幂等定义。
-- 必须由 mahjong_migration 或等价迁移身份执行，Auth 运行账号不得执行本文件。
\set ON_ERROR_STOP on
\ir ../Storage/schema.sql
