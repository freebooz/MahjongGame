/**
 * 管理控制台仪表盘协调层：按面板独立降级刷新，并渲染当前分页快照。
 */
import {
  actionNames,appendPager,byId,date,emptyOverview,esc,hasRole,renderSourceHealth,
  request,setConnection,settle,state,withPage
} from "./admin-console-state";
import {
  closeInvestigationCase,downloadEvidencePackage,openActionDialog,openCaseResult,
  renderAssetOperations,showPlayer,showRoom
} from "./admin-console-management";

// 并行刷新权限允许的各面板；每个请求独立收敛错误，单点故障不会中断整页更新。
export async function refresh(){
  if(!state.authenticated){setConnection(false,"需要管理员身份与 MFA");return;}
  try{
    state.me=await request("/admin/v1/me");
    // /me 水位在分页快照之前读取；SSE 从该序列续传，覆盖快照加载期间发生的变化。
    if(!state.eventId&&state.me.realtime?.currentEventId)
      state.eventId=state.me.realtime.currentEventId;
    byId("identity").textContent=state.me.operatorId;
    const roomQuery=new URLSearchParams();
    if(byId("lifecycle").value)roomQuery.set("lifecycle",byId("lifecycle").value);
    if(byId("search").value)roomQuery.set("search",byId("search").value);
    for(const [elementId,parameter] of [
      ["regionFilter","regionId"],["clusterFilter","clusterId"],
      ["lobbyFilter","lobbyId"],["nodeFilter","nodeId"]]){
      const value=byId(elementId).value.trim();if(value)roomQuery.set(parameter,value);
    }
    withPage(roomQuery,"rooms");
    const playerQuery=new URLSearchParams();
    if(byId("playerSearch").value)playerQuery.set("search",byId("playerSearch").value);
    withPage(playerQuery,"players");
    const instanceQuery=new URLSearchParams();
    for(const [elementId,parameter] of [
      ["regionFilter","regionId"],["clusterFilter","clusterId"],["nodeFilter","nodeId"]]){
      const value=byId(elementId).value.trim();if(value)instanceQuery.set(parameter,value);
    }
    withPage(instanceQuery,"instances");
    const canRooms=hasRole("room.viewer"),canPlayers=hasRole("player.viewer");
    const canAudit=hasRole("audit.viewer");
    const canCases=["room.operator","room.approver","support.operator","player.approver",
      "risk.analyst","compensation.operator","audit.viewer"].some(hasRole);
    const canAssetOperations=["compensation.operator","player.approver","audit.viewer"].some(hasRole);
    const canActions=["room.operator","room.approver","player.operator","player.approver",
      "sanction.operator","risk.analyst","support.operator","infrastructure.operator",
      "compensation.operator","audit.viewer"].some(hasRole);
    const results=await Promise.all([
      settle(canRooms,()=>request("/admin/v1/overview"),emptyOverview()),
      settle(canPlayers,()=>request(`/admin/v1/players?${playerQuery}`),{items:[],hasMore:false,nextCursor:null}),
      settle(canRooms,()=>request(`/admin/v1/rooms?${roomQuery}`),{items:[],hasMore:false,nextCursor:null}),
      settle(canRooms,()=>request(`/admin/v1/instances?${instanceQuery}`),{items:[],hasMore:false,nextCursor:null}),
      settle(canActions&&state.me.managementEnabled,()=>request("/admin/v1/action-requests"),[]),
      settle(canAudit&&state.me.managementEnabled,()=>request("/admin/v1/command-outbox"),[]),
      settle(canCases&&state.me.managementEnabled,()=>request("/admin/v1/cases"),[]),
      settle(canAssetOperations&&state.me.managementEnabled,
        ()=>request("/admin/v1/player-asset-operations"),[]),
      settle(canRooms||canPlayers,()=>request("/admin/v1/source-health"),null)
    ]);
    const [overview,playerPage,roomPage,instancePage,actions,outbox,cases,assetOperations,health]=
      results.map(result=>result.value);
    const players=playerPage.items,rooms=roomPage.items,instances=instancePage.items;
    state.visible={players,rooms,instances};
    state.pages.players.next=playerPage.nextCursor;
    state.pages.rooms.next=roomPage.nextCursor;
    state.pages.instances.next=instancePage.nextCursor;
    state.actions=actions;state.cases=cases;
    byId("totalRooms").textContent=overview.totalRooms;
    byId("activeRooms").textContent=overview.activeRooms;
    byId("abnormalRooms").textContent=overview.abnormalRooms;
    byId("connectedPlayers").textContent=overview.totalConnectedPlayers;
    byId("serverInstances").textContent=overview.dedicatedServerInstances;
    byId("totalPlayers").textContent=players.length;
    byId("pendingCommands").textContent=outbox.filter(item=>item.status==="Pending").length;
    byId("pendingWalletOperations").textContent=assetOperations.filter(
      item=>item.status==="ApprovedPendingWalletExecution").length;
    renderPlayers(players);renderRooms(rooms);renderInstances(instances);renderActions(actions);
    appendPager("playersBody",9,"players",playerPage);
    appendPager("roomsBody",8,"rooms",roomPage);
    appendPager("instancesBody",9,"instances",instancePage);
    renderOutbox(outbox);renderCases(cases);renderAssetOperations(assetOperations);
    renderSourceHealth(health||overview.reliability);
    // 显式固定元组类型，避免面板编号被推断成错误文案的一部分并掩盖独立降级提示。
    const sectionErrors:Array<[string,number,string|null]>=[
      ["playersBody",9,results[1].error],["roomsBody",8,results[2].error],
      ["instancesBody",9,results[3].error]
    ];
    sectionErrors.forEach(([bodyId,columns,error])=>{
      if(error)byId(bodyId).innerHTML=`<tr><td colspan="${columns}" class="empty section-error">${esc(error)}</td></tr>`;
    });
    byId("actionsPanel").hidden=!(canActions&&state.me.managementEnabled);
    byId("outboxPanel").hidden=!(canAudit&&state.me.managementEnabled);
    byId("casesPanel").hidden=!(canCases&&state.me.managementEnabled);
    byId("assetOperationsPanel").hidden=!(canAssetOperations&&state.me.managementEnabled);
    const errors=results.map(result=>result.error).filter(Boolean);
    setConnection(errors.length===0,
      `${errors.length?"降级更新":"已更新"} ${new Date().toLocaleTimeString("zh-CN",{hour12:false})}${errors.length?` · ${errors.length} 个面板不可用`:""}`);
  }catch(error){setConnection(false,error.message);}
}
// 渲染脱敏玩家列表并绑定详情入口，完整令牌和原始 IP 永不进入 DOM。
export function renderPlayers(players){
  byId("playersBody").innerHTML=players.length?players.map(player=>`<tr data-player="${esc(player.playerId)}">
    <td><strong>${esc(player.displayName)}</strong><br><span class="mono">${esc(player.playerId)}</span></td>
    <td><span class="pill">${esc(player.accountStatus)}</span><br>${esc(player.provider)}</td>
    <td><span class="pill ${player.online?"Ready":"Failed"}">${player.online?"在线":"离线"}</span></td>
    <td class="mono">${esc(player.currentDeviceId||"—")}</td><td>${esc(player.currentMaskedIp||"—")}</td>
    <td>${esc(player.lobbyId||"—")}</td><td>${esc(player.roomCode||"—")}</td>
    <td>${player.latencyMilliseconds!=null?`${player.latencyMilliseconds.toFixed(0)} ms`:"—"}</td>
    <td>${date(player.lastLoginAtUtc)}</td></tr>`).join("")
    :'<tr><td colspan="9" class="empty">没有可查看的玩家</td></tr>';
  byId("playersBody").querySelectorAll("[data-player]").forEach(row=>row.onclick=()=>showPlayer(row.dataset.player));
}
// 渲染房间列表；Allocator 缺失字段保留为破折号而不伪造集群或节点值。
export function renderRooms(rooms){
  byId("roomsBody").innerHTML=rooms.length?rooms.map(room=>`<tr data-room="${esc(room.roomId)}">
    <td><strong>${esc(room.roomCode)}</strong><br><span class="mono">${esc(room.roomId)}</span></td>
    <td>${esc(room.gameMode)}</td><td><span class="pill ${esc(room.lifecycle)}">${esc(room.lifecycle)}</span></td>
    <td>${room.playerCount}/${room.maximumPlayers}</td><td>${room.currentRound}/${room.roundCount}</td>
    <td>${esc(room.regionId||"—")} / ${esc(room.clusterId||"—")} / ${esc(room.lobbyId||"—")} / ${esc(room.nodeId||"—")}</td>
    <td class="mono">${esc(room.serverInstanceId||"—")}</td><td>${date(room.updatedAtUtc)}</td></tr>`).join("")
    :'<tr><td colspan="8" class="empty">没有可查看的房间</td></tr>';
  byId("roomsBody").querySelectorAll("[data-room]").forEach(row=>row.onclick=()=>showRoom(row.dataset.room));
}
// 渲染 Dedicated Server 实例，并仅向具备基础设施角色的操作员提供管理入口。
export function renderInstances(items){
  const canTerminate=state.me?.managementEnabled&&hasRole("infrastructure.operator");
  byId("instancesBody").innerHTML=items.length?items.map(({clusterId,nodeId,instance})=>`<tr>
    <td>${esc(clusterId)}</td><td>${esc(nodeId)}</td><td class="mono">${esc(instance.serverInstanceId)}</td>
    <td>${esc(instance.roomId)}</td><td><span class="pill ${esc(instance.state)}">${esc(instance.state)}</span></td>
    <td>${esc(instance.processId||"—")}</td><td>${esc(instance.advertisedIp)}:${instance.port}</td>
    <td>${date(instance.lastHeartbeatAtUtc)}</td>
    <td>${canTerminate?`<button class="table-action" data-server="${esc(instance.serverInstanceId)}">${esc(actionNames.TerminateAbnormalServer)}</button>`:"—"}</td></tr>`).join("")
    :'<tr><td colspan="9" class="empty">当前没有 Dedicated Server 实例</td></tr>';
  byId("instancesBody").querySelectorAll("[data-server]").forEach(button=>button.onclick=()=>openActionDialog({
    kind:"server",targetId:button.dataset.server,expectedStateSequence:null,
    safeForHighRiskActions:state.safeForHighRiskActions
  }));
}
// 渲染管理申请与异人审批入口，审批按钮仍受后端 RBAC 和状态机二次约束。
function renderActions(actions){
  byId("actionsBody").innerHTML=actions.length?actions.map(action=>`<tr>
    <td class="mono">${esc(action.actionRequestId)}</td><td>${esc(actionNames[action.actionType]||action.actionType)}
      ${actionParameterSummary(action)?`<br><small>${esc(actionParameterSummary(action))}</small>`:""}</td>
    <td class="mono">${esc(action.targetId)}</td><td>${esc(action.requestedBy)}</td>
    <td><span class="pill">${esc(action.status)}</span></td><td>${esc(action.ticketId)}</td>
    <td>${date(action.requestedAtUtc)}</td><td>${((action.targetType==="Player"&&hasRole("player.approver"))
      ||(action.targetType!=="Player"&&hasRole("room.approver")))&&action.status==="PendingApproval"
      ?`<button class="table-action" data-approve="${esc(action.actionRequestId)}" data-target="${esc(action.targetId)}">审批</button>`:"—"}</td></tr>`).join("")
    :'<tr><td colspan="8" class="empty">暂无管理申请</td></tr>';
  byId("actionsBody").querySelectorAll("[data-approve]").forEach(button=>button.onclick=()=>{
    byId("approvalDialog").dataset.actionId=button.dataset.approve;
    const action=state.actions.find(item=>item.actionRequestId===button.dataset.approve);
    byId("approvalTarget").textContent=`目标：${button.dataset.target}${
      actionParameterSummary(action)?` · ${actionParameterSummary(action)}`:""}`;
    byId("approvalComment").value="";
    byId("approvalDialog").showModal();
  });
}
// 只展示经过后端规范化的资产操作摘要，避免把自由文本参数直接拼入页面。
function actionParameterSummary(action){
  const parameters=action?.parameters;
  if(action?.actionType==="GrantPlayerCompensation"&&parameters)
    return `案件 ${parameters.caseId} · ${parameters.assetCode} × ${parameters.amount}`;
  if(action?.actionType==="RevokeErroneousReward"&&parameters)
    return `案件 ${parameters.caseId} · 奖励 ${parameters.rewardGrantId}`;
  return "";
}
// 渲染只读命令 Outbox，帮助审计人员判断重试和执行状态。
function renderOutbox(items){
  byId("outboxBody").innerHTML=items.length?items.map(item=>`<tr>
    <td class="mono">${esc(item.outboxId)}</td><td>${esc(actionNames[item.actionType]||item.actionType)}</td>
    <td><span class="pill">${esc(item.targetType)}</span><br><span class="mono">${esc(item.targetId)}</span></td>
    <td><span class="pill">${esc(item.status)}</span></td><td>${item.attemptCount}</td>
    <td>${date(item.availableAtUtc)}</td><td class="mono">${esc(item.traceId)}</td></tr>`).join("")
    :'<tr><td colspan="7" class="empty">暂无待执行命令</td></tr>';
}
// 渲染调查/客服/补偿案件；证据包与关闭动作始终绑定 CaseId，防止客户端扩大调查范围。
function renderCases(items){
  const resultAction=item=>{
    const roomAction=item.status==="Open"&&item.targetType==="Room"
      ?item.caseType==="RoomLogExport"
      ?`<button type="button" class="secondary" data-case-result="logs" data-case-id="${esc(item.caseId)}" data-target-id="${esc(item.targetId)}">下载日志</button>`
      :item.caseType==="ReplayReview"
        ?`<button type="button" class="secondary" data-case-result="replay" data-case-id="${esc(item.caseId)}" data-target-id="${esc(item.targetId)}">查看回放</button>`
        :""
      :"";
    const packageAction=`<button type="button" class="secondary" data-case-package="${esc(item.caseId)}">证据包</button>`;
    const closeAction=item.status==="Open"
      ?`<button type="button" class="danger" data-case-close="${esc(item.caseId)}">关闭案件</button>`
      :"";
    return `${roomAction}${packageAction}${closeAction}`;
  };
  byId("casesBody").innerHTML=items.length?items.map(item=>`<tr>
    <td class="mono">${esc(item.caseId)}</td><td>${esc(item.caseType)}</td>
    <td><span class="pill">${esc(item.targetType)}</span><br><span class="mono">${esc(item.targetId)}</span></td>
    <td><span class="pill">${esc(item.status)}</span></td><td>${esc(item.ticketId)}</td>
    <td>${esc(item.requestedBy)} → ${esc(item.approvedBy)}</td>
    <td>${date(item.createdAtUtc)}</td><td class="mono">${esc(item.traceId)}</td>
    <td>${resultAction(item)}</td></tr>`).join("")
    :'<tr><td colspan="9" class="empty">暂无管理案件</td></tr>';
  byId("casesBody").querySelectorAll("[data-case-result]").forEach(button=>{
    button.onclick=()=>openCaseResult(
      button.dataset.caseResult,
      button.dataset.targetId,
      button.dataset.caseId);
  });
  byId("casesBody").querySelectorAll("[data-case-package]").forEach(button=>{
    button.onclick=()=>downloadEvidencePackage(button.dataset.casePackage);
  });
  byId("casesBody").querySelectorAll("[data-case-close]").forEach(button=>{
    button.onclick=()=>closeInvestigationCase(button.dataset.caseClose);
  });
}


