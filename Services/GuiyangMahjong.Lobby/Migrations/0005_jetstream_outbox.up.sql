\set ON_ERROR_STOP on
-- Lobby 的 canonical schema 采用幂等 DDL；升级脚本复用它，确保既有投影与阶段9事务 Outbox 按同一顺序安装。
\ir ../Storage/schema.sql

