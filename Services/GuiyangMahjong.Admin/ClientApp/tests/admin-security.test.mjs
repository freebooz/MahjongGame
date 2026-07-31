import test from "node:test";
import assert from "node:assert/strict";
import {authenticationFailureMessage,requiresCsrf} from "../src/app/admin-console/admin-security.ts";
import {settlePanel} from "../src/app/admin-console/panel-degradation.ts";

// Cookie 认证的读取不要求 CSRF，但所有写方法必须默认保护，防止新增端点遗漏。
test("CSRF 方法分类默认拒绝写操作",()=>{
  assert.equal(requiresCsrf("GET"),false);
  assert.equal(requiresCsrf("HEAD"),false);
  assert.equal(requiresCsrf("POST"),true);
  assert.equal(requiresCsrf("DELETE"),true);
});

// UI 只显示受控错误，不传播后端响应正文或内部异常堆栈。
test("管理员认证错误使用受控文案",()=>{
  assert.equal(authenticationFailureMessage(401),"管理员身份、MFA 或授权范围验证失败");
  assert.equal(authenticationFailureMessage(503),"管理会话建立失败（503）");
});

// 两个面板并行刷新时，一个数据源失败只能替换自己的降级值，不能吞掉另一个面板的成功结果。
test("管理面板按数据源独立降级",async()=>{
  const [rooms,players]=await Promise.all([
    settlePanel(true,async()=>{throw new Error("房间源暂时不可用");},["cached-room"]),
    settlePanel(true,async()=>["live-player"],[])
  ]);
  assert.deepEqual(rooms.value,["cached-room"]);
  assert.equal(rooms.error,"房间源暂时不可用");
  assert.deepEqual(players.value,["live-player"]);
  assert.equal(players.error,null);
});
