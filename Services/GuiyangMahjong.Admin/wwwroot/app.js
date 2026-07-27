const byId=id=>document.getElementById(id);
const state={
  token:sessionStorage.getItem("mahjong-admin-token")||"",
  timer:null,me:null,pendingAction:null,currentTarget:null,actions:[],cases:[]
};
const esc=value=>String(value??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const date=value=>value?new Date(value).toLocaleString("zh-CN",{hour12:false}):"—";
const hasRole=role=>state.me?.roles?.includes(role);
const actionNames={
  ForceDissolveRoom:"强制解散房间",TerminateAbnormalServer:"终止异常服务器",
  MarkRoomAbnormal:"标记房间异常",TriggerCompensation:"触发补偿流程",
  ProhibitNewPlayers:"禁止新玩家加入",EnableMaintenanceMode:"启用维护模式",
  ExportRoomLogs:"导出房间日志",ViewReplay:"查看回放",
  StartDisputeInvestigation:"发起争议调查",
  ForceLogoutPlayer:"强制玩家下线",TemporaryFreezePlayer:"临时冻结",
  PermanentBanPlayer:"永久封禁",LiftPlayerBan:"解除封禁",
  MutePlayer:"禁言",UnmutePlayer:"解除禁言",
  ResetAbnormalPlayerSession:"重置异常会话",MarkRiskAccount:"标记风险账号",
  GrantPlayerCompensation:"发放补偿",RevokeErroneousReward:"撤销错误奖励",
  ViewPlayerReplay:"查看玩家回放",CreatePlayerSupportTicket:"创建客服工单"
};

async function request(path,options={}){
  const headers={Authorization:`Bearer ${state.token}`,...(options.body?{"Content-Type":"application/json"}:{})};
  if(options.trace)headers["X-Trace-Id"]=crypto.randomUUID();
  if(options.idempotent)headers["Idempotency-Key"]=crypto.randomUUID();
  const response=await fetch(path,{...options,headers:{...headers,...options.headers}});
  if(response.status===401)throw new Error("管理凭证无效或已失效");
  if(!response.ok){
    let message=`管理服务返回 ${response.status}`;
    try{message=(await response.json()).message||message;}catch{}
    throw new Error(message);
  }
  return response.status===204?null:response.json();
}
function setConnection(ok,message){
  const node=byId("connection");node.className=`status ${ok?"online":"offline"}`;
  node.textContent=message;
}
function emptyOverview(){
  return {totalRooms:0,activeRooms:0,abnormalRooms:0,totalConnectedPlayers:0,dedicatedServerInstances:0};
}
async function refresh(){
  if(!state.token){setConnection(false,"需要凭证");return;}
  try{
    state.me=await request("/admin/v1/me");
    byId("identity").textContent=state.me.operatorId;
    const roomQuery=new URLSearchParams();
    if(byId("lifecycle").value)roomQuery.set("lifecycle",byId("lifecycle").value);
    if(byId("search").value)roomQuery.set("search",byId("search").value);
    const playerQuery=new URLSearchParams();
    if(byId("playerSearch").value)playerQuery.set("search",byId("playerSearch").value);
    const canRooms=hasRole("room.viewer"),canPlayers=hasRole("player.viewer");
    const canAudit=hasRole("audit.viewer");
    const canCases=["room.operator","room.approver","support.operator","player.approver",
      "risk.analyst","compensation.operator","audit.viewer"].some(hasRole);
    const canAssetOperations=["compensation.operator","player.approver","audit.viewer"].some(hasRole);
    const canActions=["room.operator","room.approver","player.operator","player.approver",
      "sanction.operator","risk.analyst","support.operator","infrastructure.operator",
      "compensation.operator","audit.viewer"].some(hasRole);
    const [overview,players,rooms,instances,actions,outbox,cases,assetOperations]=await Promise.all([
      canRooms?request("/admin/v1/overview"):emptyOverview(),
      canPlayers?request(`/admin/v1/players?${playerQuery}`):[],
      canRooms?request(`/admin/v1/rooms?${roomQuery}`):[],
      canRooms?request("/admin/v1/instances"):[],
      canActions&&state.me.managementEnabled?request("/admin/v1/action-requests"):[],
      canAudit&&state.me.managementEnabled?request("/admin/v1/command-outbox"):[],
      canCases&&state.me.managementEnabled?request("/admin/v1/cases"):[],
      canAssetOperations&&state.me.managementEnabled
        ?request("/admin/v1/player-asset-operations"):[]
    ]);
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
    renderOutbox(outbox);renderCases(cases);renderAssetOperations(assetOperations);
    byId("actionsPanel").hidden=!(canActions&&state.me.managementEnabled);
    byId("outboxPanel").hidden=!(canAudit&&state.me.managementEnabled);
    byId("casesPanel").hidden=!(canCases&&state.me.managementEnabled);
    byId("assetOperationsPanel").hidden=!(canAssetOperations&&state.me.managementEnabled);
    setConnection(true,`已更新 ${new Date().toLocaleTimeString("zh-CN",{hour12:false})}`);
  }catch(error){setConnection(false,error.message);}
}
function renderPlayers(players){
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
function renderRooms(rooms){
  byId("roomsBody").innerHTML=rooms.length?rooms.map(room=>`<tr data-room="${esc(room.roomId)}">
    <td><strong>${esc(room.roomCode)}</strong><br><span class="mono">${esc(room.roomId)}</span></td>
    <td>${esc(room.gameMode)}</td><td><span class="pill ${esc(room.lifecycle)}">${esc(room.lifecycle)}</span></td>
    <td>${room.playerCount}/${room.maximumPlayers}</td><td>${room.currentRound}/${room.roundCount}</td>
    <td>${esc(room.clusterId||"—")} / ${esc(room.nodeId||"—")}</td>
    <td class="mono">${esc(room.serverInstanceId||"—")}</td><td>${date(room.updatedAtUtc)}</td></tr>`).join("")
    :'<tr><td colspan="8" class="empty">没有可查看的房间</td></tr>';
  byId("roomsBody").querySelectorAll("[data-room]").forEach(row=>row.onclick=()=>showRoom(row.dataset.room));
}
function renderInstances(items){
  const canTerminate=state.me?.managementEnabled&&hasRole("infrastructure.operator");
  byId("instancesBody").innerHTML=items.length?items.map(({clusterId,nodeId,instance})=>`<tr>
    <td>${esc(clusterId)}</td><td>${esc(nodeId)}</td><td class="mono">${esc(instance.serverInstanceId)}</td>
    <td>${esc(instance.roomId)}</td><td><span class="pill ${esc(instance.state)}">${esc(instance.state)}</span></td>
    <td>${esc(instance.processId||"—")}</td><td>${esc(instance.advertisedIp)}:${instance.port}</td>
    <td>${date(instance.lastHeartbeatAtUtc)}</td>
    <td>${canTerminate?`<button class="table-action" data-server="${esc(instance.serverInstanceId)}">${esc(actionNames.TerminateAbnormalServer)}</button>`:"—"}</td></tr>`).join("")
    :'<tr><td colspan="9" class="empty">当前没有 Dedicated Server 实例</td></tr>';
  byId("instancesBody").querySelectorAll("[data-server]").forEach(button=>button.onclick=()=>openActionDialog({
    kind:"server",targetId:button.dataset.server,expectedStateSequence:null
  }));
}
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
function actionParameterSummary(action){
  const parameters=action?.parameters;
  if(action?.actionType==="GrantPlayerCompensation"&&parameters)
    return `案件 ${parameters.caseId} · ${parameters.assetCode} × ${parameters.amount}`;
  if(action?.actionType==="RevokeErroneousReward"&&parameters)
    return `案件 ${parameters.caseId} · 奖励 ${parameters.rewardGrantId}`;
  return "";
}
function renderOutbox(items){
  byId("outboxBody").innerHTML=items.length?items.map(item=>`<tr>
    <td class="mono">${esc(item.outboxId)}</td><td>${esc(actionNames[item.actionType]||item.actionType)}</td>
    <td><span class="pill">${esc(item.targetType)}</span><br><span class="mono">${esc(item.targetId)}</span></td>
    <td><span class="pill">${esc(item.status)}</span></td><td>${item.attemptCount}</td>
    <td>${date(item.availableAtUtc)}</td><td class="mono">${esc(item.traceId)}</td></tr>`).join("")
    :'<tr><td colspan="7" class="empty">暂无待执行命令</td></tr>';
}
function renderCases(items){
  const resultAction=item=>item.status!=="Open"||item.targetType!=="Room"?"—"
    :item.caseType==="RoomLogExport"
      ?`<button type="button" class="secondary" data-case-result="logs" data-case-id="${esc(item.caseId)}" data-target-id="${esc(item.targetId)}">下载日志</button>`
      :item.caseType==="ReplayReview"
        ?`<button type="button" class="secondary" data-case-result="replay" data-case-id="${esc(item.caseId)}" data-target-id="${esc(item.targetId)}">查看回放</button>`
        :"—";
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
}
function saveBlob(blob,fileName){
  const url=URL.createObjectURL(blob),link=document.createElement("a");
  link.href=url;link.download=fileName;document.body.appendChild(link);link.click();
  link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
async function openCaseResult(kind,roomId,caseId){
  try{
    if(kind==="logs"){
      const response=await fetch(
        `/admin/v1/rooms/${encodeURIComponent(roomId)}/log-exports/${encodeURIComponent(caseId)}`,
        {headers:{Authorization:`Bearer ${state.token}`,"X-Trace-Id":crypto.randomUUID()}});
      if(!response.ok)throw new Error(`日志导出失败（${response.status}）`);
      saveBlob(await response.blob(),`room-${roomId}-logs-${caseId}.json`);
      return;
    }
    const records=await request(
      `/admin/v1/rooms/${encodeURIComponent(roomId)}/replays?caseId=${encodeURIComponent(caseId)}`,
      {trace:true});
    saveBlob(
      new Blob([JSON.stringify(records,null,2)],{type:"application/json"}),
      `room-${roomId}-replays-${caseId}.json`);
  }catch(error){setConnection(false,error.message);}
}
function renderAssetOperations(items){
  byId("assetOperationsBody").innerHTML=items.length?items.map(item=>`<tr>
    <td class="mono">${esc(item.operationId)}</td><td class="mono">${esc(item.playerId)}</td>
    <td>${esc(item.operationType)}</td>
    <td>${item.operationType==="GrantCompensation"
      ?`${esc(item.assetCode)} × ${item.amount}`:esc(item.rewardGrantId)}</td>
    <td><span class="pill">${esc(item.status)}</span></td>
    <td class="mono">${esc(item.caseId)}</td>
    <td>${esc(item.requestedBy)} → ${esc(item.approvedBy)}</td>
    <td>${date(item.createdAtUtc)}</td><td class="mono">${esc(item.traceId)}</td></tr>`).join("")
    :'<tr><td colspan="9" class="empty">暂无资产操作</td></tr>';
}
async function showRoom(roomId){
  try{
    const detail=await request(`/admin/v1/rooms/${encodeURIComponent(roomId)}`),r=detail.summary;
    const runtime=detail.runtime||{},players=runtime.players||[],events=detail.timeline||[];
    const canManage=state.me?.managementEnabled&&hasRole("room.operator");
    byId("roomDetail").innerHTML=`<p class="eyebrow">ROOM DETAIL</p><h2>房间 ${esc(r.roomCode)}</h2>
      <div class="detail-grid">
      <div><span>状态</span>${esc(r.lifecycle)}</div><div><span>玩法</span>${esc(r.gameMode)}</div>
      <div><span>MatchId</span><b class="mono">${esc(r.matchId)}</b></div><div><span>状态序号</span>${r.stateSequence}</div>
      <div><span>玩家</span>${detail.playerIds.map(esc).join("<br>")||"—"}</div>
      <div><span>服务实例</span><b class="mono">${esc(r.serverInstanceId||"—")}</b></div>
      <div><span>创建时间</span>${date(r.createdAtUtc)}</div><div><span>最后更新</span>${date(r.updatedAtUtc)}</div>
      <div><span>Tick / FPS</span>${runtime.serverTickMilliseconds?.toFixed(2)??"—"} ms / ${runtime.serverFramesPerSecond?.toFixed(1)??"—"}</div>
      <div><span>进程内存</span>${runtime.processMemoryBytes?`${(runtime.processMemoryBytes/1048576).toFixed(1)} MB`:"—"}</div>
      <div><span>累计 RPC</span>${runtime.rpcReceivedCount??"—"}</div><div><span>开局时间</span>${date(runtime.gameStartedAtUtc)}</div>
      </div>
      ${canManage?'<div class="manage-actions"><button id="createRoomAction">创建管理申请</button></div>':""}
      <h2>玩家连接</h2><p class="mono">${players.length?players.map(p=>`${esc(p.playerId)} · ${esc(p.connectionState)} · ${p.latencyMilliseconds?.toFixed(0)??"—"} ms`).join("<br>"):"等待遥测"}</p>
      <h2>事件时间线</h2><p class="mono">${events.length?events.map(e=>`${date(e.occurredAtUtc)} · ${esc(e.eventType)} · ${esc(JSON.stringify(e.data))}`).join("<br>"):"暂无事件"}</p>
      <h2>房间规则</h2><p class="mono">${esc(JSON.stringify(detail.rules))}</p>`;
    if(canManage)byId("createRoomAction").onclick=()=>openActionDialog({
      kind:"room",targetId:r.roomId,expectedStateSequence:r.stateSequence
    });
    byId("roomDialog").showModal();
  }catch(error){setConnection(false,error.message);}
}
function availableActions(kind){
  const values=[];
  const add=(role,types)=>{if(hasRole(role))types.forEach(type=>values.push(type));};
  if(kind==="room"){
    add("room.operator",["MarkRoomAbnormal","ProhibitNewPlayers","EnableMaintenanceMode",
      "ForceDissolveRoom","ExportRoomLogs","ViewReplay","StartDisputeInvestigation"]);
    add("compensation.operator",["TriggerCompensation"]);
  }else if(kind==="player"){
    add("player.operator",["ForceLogoutPlayer","ResetAbnormalPlayerSession"]);
    add("sanction.operator",["TemporaryFreezePlayer","PermanentBanPlayer","LiftPlayerBan","MutePlayer","UnmutePlayer"]);
    add("risk.analyst",["MarkRiskAccount"]);
    add("compensation.operator",["GrantPlayerCompensation","RevokeErroneousReward"]);
    add("support.operator",["ViewPlayerReplay","CreatePlayerSupportTicket"]);
  }else if(kind==="server"){
    add("infrastructure.operator",["TerminateAbnormalServer"]);
  }
  return [...new Set(values)];
}
function openActionDialog(target){
  state.pendingAction=null;state.currentTarget=target;
  const types=availableActions(target.kind);
  byId("actionType").innerHTML=types.map(type=>`<option value="${esc(type)}">${esc(actionNames[type])}</option>`).join("");
  byId("actionDialogTitle").textContent=target.kind==="player"
    ?"创建玩家管理申请":target.kind==="server"?"创建服务器管理申请":"创建房间管理申请";
  byId("actionTargetLabel").textContent=target.kind==="player"
    ?"目标玩家 ID":target.kind==="server"?"Dedicated Server ID":"目标房间 ID";
  byId("actionTarget").value=target.targetId;
  byId("actionTicket").value="";byId("actionReason").value="";byId("actionConfirmation").value="";
  byId("assetCode").value="";byId("assetAmount").value="";byId("rewardGrantId").value="";
  byId("originalSanctionCommandId").value="";
  byId("confirmationStep").hidden=true;byId("actionSubmit").textContent="创建申请";
  byId("actionMessage").textContent="创建后还需要再次手工输入目标，并由另一名审批人批准。";
  updateActionParameterFields();
  byId("actionDialog").showModal();
}
function updateActionParameterFields(){
  const actionType=byId("actionType").value;
  const isGrant=actionType==="GrantPlayerCompensation";
  const isRevoke=actionType==="RevokeErroneousReward";
  const isSanctionReversal=actionType==="LiftPlayerBan"||actionType==="UnmutePlayer";
  byId("assetParameterFields").hidden=!(isGrant||isRevoke);
  byId("sanctionReferenceFields").hidden=!isSanctionReversal;
  byId("grantParameterFields").hidden=!isGrant;
  byId("revokeParameterFields").hidden=!isRevoke;
  if(isGrant||isRevoke){
    const cases=state.cases.filter(item=>
      item.caseType==="CompensationReview"&&item.status==="Open");
    byId("assetCaseId").innerHTML=cases.length
      ?cases.map(item=>`<option value="${esc(item.caseId)}">${esc(item.caseId)} · ${esc(item.ticketId)}</option>`).join("")
      :'<option value="">没有可用的补偿审查案件</option>';
  }
}
async function submitAction(){
  try{
    if(!state.pendingAction){
      const actionType=byId("actionType").value;
      const parameters=actionType==="GrantPlayerCompensation"?{
        caseId:byId("assetCaseId").value,
        assetCode:byId("assetCode").value,
        amount:Number(byId("assetAmount").value)
      }:actionType==="RevokeErroneousReward"?{
        caseId:byId("assetCaseId").value,
        rewardGrantId:byId("rewardGrantId").value
      }:(actionType==="LiftPlayerBan"||actionType==="UnmutePlayer")?{
        originalCommandId:byId("originalSanctionCommandId").value
      }:null;
      state.pendingAction=await request("/admin/v1/action-requests",{
        method:"POST",trace:true,idempotent:true,body:JSON.stringify({
          actionType,targetId:state.currentTarget.targetId,
          reason:byId("actionReason").value,ticketId:byId("actionTicket").value,
          expectedStateSequence:state.currentTarget.expectedStateSequence,parameters
        })
      });
      byId("confirmationStep").hidden=false;
      byId("actionSubmit").textContent="再次确认并提交审批";
      byId("actionMessage").textContent="第一步已完成。请核对并手工输入目标 ID。";
      return;
    }
    await request(`/admin/v1/action-requests/${encodeURIComponent(state.pendingAction.actionRequestId)}/confirm`,{
      method:"POST",body:JSON.stringify({targetConfirmation:byId("actionConfirmation").value})
    });
    byId("actionDialog").close();state.pendingAction=null;await refresh();
  }catch(error){byId("actionMessage").textContent=error.message;}
}
async function submitApproval(){
  try{
    const actionId=byId("approvalDialog").dataset.actionId;
    await request(`/admin/v1/action-requests/${encodeURIComponent(actionId)}/approvals`,{
      method:"POST",body:JSON.stringify({
        decision:byId("approvalDecision").value,comment:byId("approvalComment").value
      })
    });
    byId("approvalDialog").close();await refresh();
  }catch(error){setConnection(false,error.message);}
}
async function showPlayer(playerId){
  try{
    const detail=await request(`/admin/v1/players/${encodeURIComponent(playerId)}`),p=detail.summary;
    const sessions=detail.sessions||[],logins=detail.loginHistory||[],rooms=detail.roomHistory||[],disconnects=detail.disconnectHistory||[];
    const controls=detail.controlHistory||[],riskLabels=p.riskLabels||[];
    const canManage=state.me?.managementEnabled&&availableActions("player").length>0;
    byId("playerDetail").innerHTML=`<p class="eyebrow">PLAYER 360 · MASKED</p><h2>${esc(p.displayName)}</h2>
      <div class="detail-grid">
      <div><span>PlayerId</span><b class="mono">${esc(p.playerId)}</b></div><div><span>账号状态</span>${esc(p.accountStatus)}</div>
      <div><span>在线状态</span>${p.online?"在线":"离线"}</div><div><span>登录渠道</span>${esc(p.provider)}</div>
      <div><span>当前设备</span><b class="mono">${esc(p.currentDeviceId||"—")}</b></div><div><span>IP 网段</span>${esc(p.currentMaskedIp||"—")}</div>
      <div><span>当前大厅</span>${esc(p.lobbyId||"—")}</div><div><span>当前房间 / 实例</span>${esc(p.roomCode||"—")} / <b class="mono">${esc(p.serverInstanceId||"—")}</b></div>
      <div><span>延迟</span>${p.latencyMilliseconds!=null?`${p.latencyMilliseconds.toFixed(0)} ms`:"—"}</div><div><span>活跃会话</span>${p.activeSessionCount}</div>
      <div><span>控制版本</span>${p.controlVersion}</div><div><span>冻结到期</span>${date(p.frozenUntilUtc)}</div>
      <div><span>禁言到期</span>${date(p.mutedUntilUtc)}</div><div><span>风险标签</span>${riskLabels.length?riskLabels.map(esc).join(" / "):"—"}</div>
      </div>
      ${canManage?'<div class="manage-actions"><button id="createPlayerAction">创建玩家管理申请</button></div>':""}
      <h2>登录历史</h2><p class="mono">${logins.length?logins.map(x=>`${date(x.occurredAtUtc)} · ${esc(x.deviceId)} · ${esc(x.maskedIp)} · ${esc(x.clientSummary)}`).join("<br>"):"暂无记录"}</p>
      <h2>会话</h2><p class="mono">${sessions.length?sessions.map(x=>`${esc(x.sessionReference)} · ${x.active?"有效":"失效"} · ${date(x.createdAtUtc)} → ${date(x.expiresAtUtc)}`).join("<br>"):"暂无记录"}</p>
      <h2>房间历史</h2><p class="mono">${rooms.length?rooms.map(x=>`${date(x.createdAtUtc)} · ${esc(x.roomCode)} · ${esc(x.lifecycle)} · ${esc(x.matchId)}`).join("<br>"):"暂无记录"}</p>
      <h2>掉线历史</h2><p class="mono">${disconnects.length?disconnects.map(x=>`${date(x.occurredAtUtc)} · ${esc(JSON.stringify(x.data))}`).join("<br>"):"暂无记录"}</p>`;
    byId("playerDetail").insertAdjacentHTML("beforeend",
      `<h2>制裁与风险操作记录</h2><p class="mono">${controls.length?controls.map(x=>
        `${date(x.effectiveAtUtc)} · ${esc(actionNames[x.actionType]||x.actionType)} · ${esc(x.ticketId)} · ${esc(x.requestedBy)} → ${esc(x.approvedBy)} · Trace ${esc(x.traceId)}`
      ).join("<br>"):"暂无记录"}</p>`);
    const evidenceActions=[];
    if(["player.operator","sanction.operator","risk.analyst","support.operator",
      "player.approver","audit.viewer"].some(hasRole))
      evidenceActions.push(["identity-history","登录 / 设备 / 会话历史"]);
    if(["player.operator","sanction.operator","risk.analyst","support.operator",
      "compensation.operator","player.approver","audit.viewer"].some(hasRole))
      evidenceActions.push(["gm-operations","GM 操作记录"]);
    if(["risk.analyst","sanction.operator","player.approver","audit.viewer"].some(hasRole))
      evidenceActions.push(["reports","举报历史"]);
    if(["compensation.operator","player.approver","audit.viewer"].some(hasRole)){
      evidenceActions.push(["asset-changes","资产变化"]);
      evidenceActions.push(["reward-claims","奖励领取"]);
      evidenceActions.push(["payment-orders","支付订单"]);
    }
    if(["support.operator","risk.analyst","player.approver","audit.viewer"].some(hasRole))
      evidenceActions.push(["replays","回放元数据"]);
    if(["chat.compliance","audit.viewer"].some(hasRole))
      evidenceActions.push(["chat-permission","聊天查询权限"]);
    if(evidenceActions.length){
      byId("playerDetail").insertAdjacentHTML("beforeend",`
        <section class="confirmation">
          <h2>受控证据查询</h2>
          <p>每次查询都必须填写关联工单；回放查询填写已独立审批的回放审查案件 ID。查询行为会写入审计账本。</p>
          <label>工单 / 回放审查案件 ID
            <input id="playerEvidenceTicket" maxlength="128" placeholder="例如 CS-20260727-001">
          </label>
          <div class="manage-actions">${evidenceActions.map(([kind,label])=>
            `<button type="button" class="secondary" data-evidence="${esc(kind)}">${esc(label)}</button>`
          ).join("")}</div>
          <pre id="playerEvidenceResult" class="mono">尚未查询</pre>
        </section>`);
      byId("playerDetail").querySelectorAll("[data-evidence]").forEach(button=>{
        button.onclick=async()=>{
          const reference=byId("playerEvidenceTicket").value.trim();
          if(!reference){byId("playerEvidenceResult").textContent="请先填写关联工单或审查案件 ID。";return;}
          const kind=button.dataset.evidence;
          const parameter=kind==="replays"?"caseId":"ticketId";
          try{
            const path=kind==="identity-history"
              ?`/admin/v1/players/${encodeURIComponent(playerId)}?ticketId=${encodeURIComponent(reference)}`
              :`/admin/v1/players/${encodeURIComponent(playerId)}/${kind}?${parameter}=${encodeURIComponent(reference)}`;
            const result=await request(
              path,
              {trace:true});
            byId("playerEvidenceResult").textContent=JSON.stringify(result,null,2);
          }catch(error){byId("playerEvidenceResult").textContent=error.message;}
        };
      });
    }
    if(canManage)byId("createPlayerAction").onclick=()=>openActionDialog({
      kind:"player",targetId:p.playerId,expectedStateSequence:null
    });
    byId("playerDialog").showModal();
  }catch(error){setConnection(false,error.message);}
}
byId("credentialButton").onclick=()=>{byId("credential").value=state.token;byId("credentialDialog").showModal();};
byId("saveCredential").onclick=()=>{state.token=byId("credential").value.trim();sessionStorage.setItem("mahjong-admin-token",state.token);setTimeout(refresh);};
byId("refreshButton").onclick=refresh;
byId("lifecycle").onchange=refresh;
let searchDelay;byId("search").oninput=()=>{clearTimeout(searchDelay);searchDelay=setTimeout(refresh,350);};
let playerSearchDelay;byId("playerSearch").oninput=()=>{clearTimeout(playerSearchDelay);playerSearchDelay=setTimeout(refresh,350);};
byId("closeRoomDialog").onclick=()=>byId("roomDialog").close();
byId("closePlayerDialog").onclick=()=>byId("playerDialog").close();
byId("actionSubmit").onclick=submitAction;
byId("actionType").onchange=updateActionParameterFields;
byId("approvalSubmit").onclick=submitApproval;
state.timer=setInterval(refresh,5000);refresh();
