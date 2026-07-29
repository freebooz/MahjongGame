// 管理台仍需操作大量历史对话框节点；统一入口在迁移期提供受控 DOM 访问，后续组件化时逐步收窄类型。
const byId=(id:string):any=>document.getElementById(id);
const state:any={
  // 管理令牌只保存在当前页面内存，刷新或关闭页面即清除，避免 XSS 后跨会话持久窃取。
  token:"",
  timer:null,me:null,pendingAction:null,currentTarget:null,actions:[],cases:[],
  safeForHighRiskActions:false,
  // 三类列表分别维护键集游标历史；切换过滤条件时必须重置，避免跨查询复用游标。
  pages:{
    players:{cursor:null,next:null,history:[]},
    rooms:{cursor:null,next:null,history:[]},
    instances:{cursor:null,next:null,history:[]}
  },
  visible:{players:[],rooms:[],instances:[]},
  eventId:"",eventAbort:null,eventReconnectMilliseconds:1000
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

// 所有 Admin API 请求统一在此附加短期凭据、TraceId 与幂等键，并转换为安全中文错误。
async function request(path:string,options:any={}){
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
// 更新页头连接状态；降级刷新使用离线颜色提醒，但不会清空仍可用的其他面板。
function setConnection(ok,message){
  const node=byId("connection");node.className=`status ${ok?"online":"offline"}`;
  node.textContent=message;
}
// 无房间查看权限或总览失败时提供安全空投影，避免模板读取未定义字段。
function emptyOverview(){
  return {totalRooms:0,activeRooms:0,abnormalRooms:0,totalConnectedPlayers:0,dedicatedServerInstances:0};
}
// 把单个面板请求转换为可降级结果，避免任一依赖失败造成整页白屏。
async function settle(enabled,operation,fallback){
  if(!enabled)return {value:fallback,error:null};
  try{return {value:await operation(),error:null};}
  catch(error){return {value:fallback,error:error.message};}
}
// 生成服务端游标分页查询；页大小仅是请求值，最终上限始终由 Admin API 强制执行。
function withPage(query:URLSearchParams,pageKey:string){
  const page=state.pages[pageKey];
  query.set("pageSize",String(state.me?.realtime?.defaultPageSize||100));
  if(page.cursor)query.set("cursor",page.cursor);
  return query;
}
// 在表格末尾渲染上一页/下一页；历史只保存不透明游标，不在浏览器推导偏移量。
function appendPager(bodyId:string,columns:number,pageKey:string,page:any){
  const body=byId(bodyId),pager=document.createElement("tr");
  pager.innerHTML=`<td colspan="${columns}" class="pagination">
    <button data-page-direction="previous" ${state.pages[pageKey].history.length?"":"disabled"}>上一页</button>
    <span>本页 ${page.items.length} 条</span>
    <button data-page-direction="next" ${page.hasMore?"":"disabled"}>下一页</button>
  </td>`;
  body.appendChild(pager);
  (pager.querySelector("[data-page-direction=previous]") as HTMLButtonElement).onclick=()=>{
    state.pages[pageKey].cursor=state.pages[pageKey].history.pop()||null;void refresh();
  };
  (pager.querySelector("[data-page-direction=next]") as HTMLButtonElement).onclick=()=>{
    state.pages[pageKey].history.push(state.pages[pageKey].cursor);
    state.pages[pageKey].cursor=page.nextCursor;void refresh();
  };
}
function resetPage(pageKey:string){
  state.pages[pageKey]={cursor:null,next:null,history:[]};
}
// 使用受控中文状态与数据年龄渲染来源卡片，绝不展示内部地址或原始异常正文。
function renderSourceHealth(metadata){
  const sources=metadata?.sources||[];
  state.safeForHighRiskActions=metadata?.safeForHighRiskActions===true;
  const decision=byId("freshnessDecision");
  decision.className=`freshness-decision ${state.safeForHighRiskActions?"safe":"unsafe"}`;
  decision.textContent=state.safeForHighRiskActions
    ?"数据为实时状态，可发起高危操作"
    :"存在降级或陈旧数据；高危操作将重新校验并可能被拒绝";
  const statusName={Healthy:"健康",Degraded:"降级",Unavailable:"不可用",Stale:"陈旧"};
  byId("sourceHealthCards").innerHTML=sources.length?sources.map(source=>`
    <article class="source-health ${esc(source.status)}">
      <strong>${esc(source.source)} · ${esc(statusName[source.status]||source.status)}</strong>
      <span>${esc(source.message)}</span>
      <small>最后成功：${date(source.lastSuccessAtUtc)}</small>
      <small>数据年龄：${source.dataAgeSeconds!=null?`${source.dataAgeSeconds.toFixed(1)} 秒`:"—"} · 陈旧阈值 ${source.staleAfterSeconds} 秒</small>
      <small>熔断：${esc(source.circuitState)} · 快照 v${source.snapshotVersion}</small>
    </article>`).join("")
    :'<article class="source-health Degraded"><strong>来源状态不可用</strong><span>健康接口尚未返回结果</span></article>';
}
// 生成详情页的新鲜度摘要，让操作员在管理按钮附近直接看到风险。
function reliabilityHtml(metadata){
  if(!metadata)return '<div class="source-health-inline unsafe">未提供来源新鲜度信息，请勿据此执行高危操作。</div>';
  const summary=(metadata.sources||[]).map(
    source=>`${esc(source.source)}=${esc(source.status)}${source.dataAgeSeconds!=null?`(${source.dataAgeSeconds.toFixed(1)}s)`:""}`).join(" · ");
  return `<div class="source-health-inline ${metadata.safeForHighRiskActions?"":"unsafe"}">
    <strong>${metadata.safeForHighRiskActions?"实时状态可用":"数据已降级；高危操作将由服务端实时复核"}</strong><br>
    <span>${summary||"无来源状态"}</span>
  </div>`;
}
// 并行刷新权限允许的各面板；每个请求独立收敛错误，单点故障不会中断整页更新。
async function refresh(){
  if(!state.token){setConnection(false,"需要凭证");return;}
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
    const sectionErrors=[
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
// 渲染房间列表；Allocator 缺失字段保留为破折号而不伪造集群或节点值。
function renderRooms(rooms){
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
// 通过临时对象 URL 下载已授权结果，完成后立即释放浏览器内存。
function saveBlob(blob,fileName){
  const url=URL.createObjectURL(blob),link=document.createElement("a");
  link.href=url;link.download=fileName;document.body.appendChild(link);link.click();
  link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
// 使用已审批案件 ID 请求日志或回放；后端仍会重新校验案件目标和访问权限。
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
// 证据包由后端按案件固定目标生成；浏览器只保存带清单哈希的结果，不拼接额外玩家范围。
async function downloadEvidencePackage(caseId){
  try{
    const result=await request(
      `/admin/v1/cases/${encodeURIComponent(caseId)}/evidence-package`,
      {trace:true});
    saveBlob(
      new Blob([JSON.stringify(result,null,2)],{type:"application/json"}),
      `investigation-${caseId}-${result.canonicalPayloadHash}.json`);
    return result;
  }catch(error){setConnection(false,error.message);return null;}
}
// 关闭案件是不可逆管理动作：先明确填写结论，再进行第二次确认，并绑定刚生成的证据摘要。
async function closeInvestigationCase(caseId){
  const resolution=window.prompt("请输入案件结论（10～2000 字）。关闭后不可重新打开：")?.trim();
  if(!resolution)return;
  if(!window.confirm(`确认关闭案件 ${caseId}？系统将先生成最终证据包并绑定其 SHA-256。`))return;
  const evidencePackage=await downloadEvidencePackage(caseId);
  if(!evidencePackage)return;
  if(!window.confirm(`证据摘要 ${evidencePackage.canonicalPayloadHash}\n请再次确认提交案件结论。`))return;
  try{
    await request(`/admin/v1/cases/${encodeURIComponent(caseId)}/close`,{
      method:"POST",
      trace:true,
      body:JSON.stringify({
        resolution,
        evidencePackageHash:evidencePackage.canonicalPayloadHash
      })
    });
    await refresh();
  }catch(error){setConnection(false,error.message);}
}
// 渲染 Wallet 意图账本，不提供直接修改余额或对局结果的前端能力。
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
// 加载房间详情和来源新鲜度；降级遥测以未知值展示，不转换为零。
async function showRoom(roomId){
  try{
    const detail=await request(`/admin/v1/rooms/${encodeURIComponent(roomId)}`),r=detail.summary;
    const runtime=detail.runtime||{},players=runtime.players||[],events=detail.timeline||[];
    // 速率只在 Lobby 判断相邻样本同源且计数器单调时存在；null 必须显示为未知，不能伪装成零流量。
    const ingressRate=runtime.networkIngressBytesPerSecond;
    const egressRate=runtime.networkEgressBytesPerSecond;
    const rpcMethods=runtime.rpcMethods||[],settlement=runtime.settlement;
    const canManage=state.me?.managementEnabled&&hasRole("room.operator");
    byId("roomDetail").innerHTML=`<p class="eyebrow">ROOM DETAIL</p><h2>房间 ${esc(r.roomCode)}</h2>
      ${reliabilityHtml(detail.reliability)}
      <div class="detail-grid">
      <div><span>状态</span>${esc(r.lifecycle)}</div><div><span>玩法</span>${esc(r.gameMode)}</div>
      <div><span>MatchId</span><b class="mono">${esc(r.matchId)}</b></div><div><span>状态序号</span>${r.stateSequence}</div>
      <div><span>玩家</span>${detail.playerIds.map(esc).join("<br>")||"—"}</div>
      <div><span>服务实例</span><b class="mono">${esc(r.serverInstanceId||"—")}</b></div>
      <div><span>创建时间</span>${date(r.createdAtUtc)}</div><div><span>最后更新</span>${date(r.updatedAtUtc)}</div>
      <div><span>Tick / FPS</span>${runtime.serverTickMilliseconds?.toFixed(2)??"—"} ms / ${runtime.serverFramesPerSecond?.toFixed(1)??"—"}</div>
      <div><span>进程 CPU</span>${runtime.processCpuPercent?.toFixed(2)??"—"}% / ${runtime.processCpuSampleWindowMilliseconds?.toFixed(0)??"—"} ms 窗口</div>
      <div><span>进程 RSS/工作集</span>${runtime.processMemoryBytes?`${(runtime.processMemoryBytes/1048576).toFixed(1)} MB`:"—"}</div>
      <div><span>应用网络累计</span>入 ${runtime.networkIngressBytes??"—"} / 出 ${runtime.networkEgressBytes??"—"} B</div>
      <div><span>应用网络速率</span>入 ${ingressRate?.toFixed(1)??"—"} / 出 ${egressRate?.toFixed(1)??"—"} B/s</div>
      <div><span>累计 RPC</span>${runtime.rpcReceivedCount??"—"}</div><div><span>开局时间</span>${date(runtime.gameStartedAtUtc)}</div>
      <div><span>结算状态</span>${settlement?`${esc(settlement.status)} · #${settlement.resultSequence??"—"}`:"—"}</div>
      <div><span>结算确认</span>${settlement?date(settlement.confirmedAtUtc):"—"}</div>
      </div>
      ${canManage?'<div class="manage-actions"><button id="createRoomAction">创建管理申请</button></div>':""}
      <h2>玩家连接</h2><p class="mono">${players.length?players.map(p=>`${esc(p.playerId)} · ${esc(p.connectionState)} · ${p.latencyMilliseconds?.toFixed(0)??"—"} ms · 托管 ${p.trustee===null||p.trustee===undefined?"—":p.trustee?"是":"否"} · ${esc(p.disconnectReason||"—")}`).join("<br>"):"等待遥测"}</p>
      <h2>RPC 分类</h2><p class="mono">${rpcMethods.length?rpcMethods.map(m=>`${esc(m.methodName)} · 收到 ${m.receivedCount} · 拒绝 ${m.rejectedCount} · 失败 ${m.failedCount} · 超时 ${m.timeoutCount} · P95/P99 ${m.p95DurationMilliseconds?.toFixed(2)??"—"}/${m.p99DurationMilliseconds?.toFixed(2)??"—"} ms`).join("<br>"):"等待遥测"}</p>
      <h2>事件时间线</h2><p class="mono">${events.length?events.map(e=>`${date(e.occurredAtUtc)} · ${esc(e.eventType)} · ${esc(JSON.stringify(e.data))}`).join("<br>"):"暂无事件"}</p>
      <h2>房间规则</h2><p class="mono">${esc(JSON.stringify(detail.rules))}</p>`;
    if(canManage)byId("createRoomAction").onclick=()=>openActionDialog({
      kind:"room",targetId:r.roomId,expectedStateSequence:r.stateSequence,
      safeForHighRiskActions:detail.reliability?.safeForHighRiskActions===true
    });
    byId("roomDialog").showModal();
  }catch(error){setConnection(false,error.message);}
}
// 根据当前角色生成候选操作列表；这只是界面裁剪，最终授权由服务端执行。
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
// 初始化两步确认对话框，并在详情陈旧时提前提示服务端可能拒绝操作。
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
  byId("actionMessage").textContent=target.safeForHighRiskActions===false
    ?"当前详情包含降级或陈旧数据。提交时服务端会强制读取实时权威状态，若来源未恢复将拒绝创建申请。"
    :"创建后还需要再次手工输入目标，并由另一名审批人批准。";
  updateActionParameterFields();
  byId("actionDialog").showModal();
}
// 按操作类型显示最小必需参数，减少无关敏感信息进入管理申请。
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
// 创建申请或执行第二次目标确认；所有写请求携带 TraceId 与幂等键。
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
// 提交异人审批决定；同人审批和过期状态仍由后端拒绝。
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
// 加载脱敏玩家 360 详情，并按角色与工单要求提供受控证据查询入口。
async function showPlayer(playerId){
  try{
    const detail=await request(`/admin/v1/players/${encodeURIComponent(playerId)}`),p=detail.summary;
    const sessions=detail.sessions||[],logins=detail.loginHistory||[],rooms=detail.roomHistory||[],disconnects=detail.disconnectHistory||[];
    const controls=detail.controlHistory||[],riskLabels=p.riskLabels||[];
    const canManage=state.me?.managementEnabled&&availableActions("player").length>0;
    byId("playerDetail").innerHTML=`<p class="eyebrow">PLAYER 360 · MASKED</p><h2>${esc(p.displayName)}</h2>
      ${reliabilityHtml(detail.reliability)}
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
    {
      evidenceActions.push(["room-history","持久房间历史"]);
      evidenceActions.push(["connection-history","连接 / 掉线 / 恢复历史"]);
      evidenceActions.push(["replays","回放元数据"]);
    }
    if(["chat.compliance","audit.viewer"].some(hasRole)){
      evidenceActions.push(["chat-permission","聊天查询权限"]);
      evidenceActions.push(["chat-records","授权窗口聊天内容"]);
    }
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
      kind:"player",targetId:p.playerId,expectedStateSequence:null,
      safeForHighRiskActions:detail.reliability?.safeForHighRiskActions===true
    });
    byId("playerDialog").showModal();
  }catch(error){setConnection(false,error.message);}
}
// Angular 根组件完成视图挂载后调用本入口，集中注册事件并启动五秒轮询。
// 将 SSE upsert/remove 幂等应用到当前可见页；游标页之外的数据等待翻页或受控重同步加载。
function applyRealtimeEvent(eventType:string,envelope:any){
  if(eventType==="resync"){
    resetPage("players");resetPage("rooms");resetPage("instances");void refresh();return;
  }
  if(eventType==="overview.upsert"){
    const value=envelope.payload||{};
    byId("totalRooms").textContent=value.totalRooms??"—";
    byId("activeRooms").textContent=value.activeRooms??"—";
    byId("abnormalRooms").textContent=value.abnormalRooms??"—";
    byId("connectedPlayers").textContent=value.totalConnectedPlayers??"—";
    byId("serverInstances").textContent=value.dedicatedServerInstances??"—";
    return;
  }
  const entity=eventType.split(".")[0],operation=eventType.split(".")[1];
  const collectionKey=entity==="room"?"rooms":entity==="player"?"players":
    entity==="instance"?"instances":null;
  if(!collectionKey)return;
  const idOf=(item:any)=>collectionKey==="rooms"?item.roomId:
    collectionKey==="players"?item.playerId:item.instance?.serverInstanceId;
  const items=state.visible[collectionKey];
  const index=items.findIndex(item=>idOf(item)===envelope.entityKey);
  if(operation==="remove"&&index>=0)items.splice(index,1);
  if(operation==="upsert"&&index>=0)items[index]=envelope.payload;
  // 仅在第一页且无过滤时接纳新实体，避免在客户端复制服务端过滤规则造成越界显示。
  const unfiltered=collectionKey==="instances"||
    (collectionKey==="rooms"&&!byId("search").value&&!byId("lifecycle").value)||
    (collectionKey==="players"&&!byId("playerSearch").value);
  if(operation==="upsert"&&index<0&&!state.pages[collectionKey].cursor&&unfiltered){
    items.unshift(envelope.payload);
    items.splice(state.me?.realtime?.defaultPageSize||100);
  }
  if(collectionKey==="rooms")renderRooms(items);
  if(collectionKey==="players")renderPlayers(items);
  if(collectionKey==="instances")renderInstances(items);
  appendPager(
    `${collectionKey}Body`,
    collectionKey==="rooms"?8:9,
    collectionKey,
    {items,nextCursor:state.pages[collectionKey].next,hasMore:!!state.pages[collectionKey].next});
}
// 解析标准 SSE 帧，序列只在完整 data JSON 成功处理后推进。
function consumeSseFrame(frame:string){
  if(frame.startsWith(":"))return;
  let id="",eventType="message",data="";
  frame.split("\n").forEach(line=>{
    if(line.startsWith("id:"))id=line.slice(3).trim();
    else if(line.startsWith("event:"))eventType=line.slice(6).trim();
    else if(line.startsWith("data:"))data+=line.slice(5).trim();
  });
  if(!data)return;
  applyRealtimeEvent(eventType,JSON.parse(data));
  if(id)state.eventId=id;
}
// fetch 流允许携带企业 Bearer 和 Last-Event-ID；断线按指数退避续传，超窗由 resync 事件收敛。
async function connectRealtime(){
  if(!state.token||!state.me?.realtime?.sseEnabled)return;
  const controller=new AbortController();state.eventAbort=controller;
  const headers:any={Authorization:`Bearer ${state.token}`,Accept:"text/event-stream"};
  if(state.eventId)headers["Last-Event-ID"]=state.eventId;
  try{
    const response=await fetch("/admin/v1/events",{headers,signal:controller.signal});
    if(!response.ok)throw new Error(`SSE ${response.status}`);
    state.eventReconnectMilliseconds=1000;
    const reader=response.body.getReader(),decoder=new TextDecoder();let buffer="";
    while(true){
      const {done,value}=await reader.read();if(done)break;
      buffer+=decoder.decode(value,{stream:true}).replace(/\r\n/g,"\n");
      let boundary;while((boundary=buffer.indexOf("\n\n"))>=0){
        consumeSseFrame(buffer.slice(0,boundary));buffer=buffer.slice(boundary+2);
      }
    }
  }catch(error){
    if(controller.signal.aborted)return;
    setConnection(false,"实时推送中断，正在断点重连");
  }
  if(!controller.signal.aborted&&state.token){
    const delay=state.eventReconnectMilliseconds;
    state.eventReconnectMilliseconds=Math.min(delay*2,30000);
    setTimeout(connectRealtime,delay);
  }
}
async function bootstrapMonitoring(){
  await refresh();
  if(state.me?.realtime?.sseEnabled){
    if(state.timer)clearInterval(state.timer);state.timer=null;
    state.eventAbort?.abort();void connectRealtime();
  }else if(state.me?.realtime?.legacyPollingEnabled&&!state.timer){
    state.timer=setInterval(refresh,5000);
  }
}
export function initializeAdminConsole(){
  byId("credentialButton").onclick=()=>{byId("credential").value=state.token;byId("credentialDialog").showModal();};
  byId("saveCredential").onclick=()=>{state.token=byId("credential").value.trim();setTimeout(bootstrapMonitoring);};
  byId("refreshButton").onclick=refresh;
  byId("lifecycle").onchange=()=>{resetPage("rooms");void refresh();};
  for(const filterId of ["regionFilter","clusterFilter","lobbyFilter","nodeFilter"]){
    byId(filterId).onchange=()=>{resetPage("rooms");resetPage("instances");void refresh();};
  }
  let searchDelay;byId("search").oninput=()=>{clearTimeout(searchDelay);resetPage("rooms");searchDelay=setTimeout(refresh,350);};
  let playerSearchDelay;byId("playerSearch").oninput=()=>{clearTimeout(playerSearchDelay);resetPage("players");playerSearchDelay=setTimeout(refresh,350);};
  byId("closeRoomDialog").onclick=()=>byId("roomDialog").close();
  byId("closePlayerDialog").onclick=()=>byId("playerDialog").close();
  byId("actionSubmit").onclick=submitAction;
  byId("actionType").onchange=updateActionParameterFields;
  byId("approvalSubmit").onclick=submitApproval;
  void bootstrapMonitoring();
}

// Angular 组件销毁时清理轮询，避免热重载或路由重建产生重复请求。
export function disposeAdminConsole(){
  if(state.timer)clearInterval(state.timer);
  state.timer=null;
  state.eventAbort?.abort();state.eventAbort=null;
}
