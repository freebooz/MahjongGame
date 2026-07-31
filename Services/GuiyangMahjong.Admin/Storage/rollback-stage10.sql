-- 阶段10回滚脚本：仅删除 Admin 自有的浏览器会话与登录安全事件，不触碰业务 Schema。
-- 执行前应先关闭 Admin:WebSecurity:BrowserSessionEnabled 并确认所有管理台已回退 Bearer 模式。
BEGIN;
DROP TABLE IF EXISTS admin_monitor.admin_login_security_events;
DROP TABLE IF EXISTS admin_monitor.admin_sessions;
COMMIT;
