/**
 * 调查证据与高风险管理交互层。
 * 所有写操作仍通过后端二次确认、审批、幂等键和审计约束，本模块不提供修改对局结果的入口。
 */
import {
  actionNames,adminHeaders,byId,date,esc,hasRole,reliabilityHtml,request,requestRefresh,setConnection,state
} from "./admin-console-state";

// 通过临时对象 URL 下载已授权结果，完成后立即释放浏览器内存。
function saveBlob(blob,fileName){
  const url=URL.createObjectURL(blob),link=document.createElement("a");
  link.href=url;link.download=fileName;document.body.appendChild(link);link.click();
  link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
// 使用已审批案件 ID 请求日志或回放；后端仍会重新校验案件目标和访问权限。
export async function openCaseResult(kind,roomId,caseId){
  try{
    if(kind==="logs"){
      const response=await fetch(
        `/admin/v1/rooms/${encodeURIComponent(roomId)}/log-exports/${encodeURIComponent(caseId)}`,
        {credentials:"same-origin",headers:adminHeaders({trace:true})});
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
export async function downloadEvidencePackage(caseId){
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
export async function closeInvestigationCase(caseId){
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
    await requestRefresh();
  }catch(error){setConnection(false,error.message);}
}
// 渲染 Wallet 意图账本，不提供直接修改余额或对局结果的前端能力。
export function renderAssetOperations(items){
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
export async function showRoom(roomId){
  try{
    const trustView=await request(`/admin/operations/v1/trust-safety/rooms/${encodeURIComponent(roomId)}`);
    const detail=trustView.detail,r=detail.summary;
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
      <div><span>Room Epoch</span>${trustView.roomEpoch}</div><div><span>动作 / 状态版本</span>${runtime.actionSequence??"—"} / ${trustView.stateVersion}</div>
      <div><span>Fleet / Provider</span>${esc(trustView.fleet||"—")} / ${esc(trustView.provider)}</div><div><span>Build / RuleSet</span>${esc(trustView.buildVersion)} / ${esc(trustView.ruleSetVersion)}</div>
      <div><span>快照版本 / 年龄</span>${runtime.snapshotVersion??"—"} / ${trustView.snapshotAgeSeconds!=null?`${trustView.snapshotAgeSeconds.toFixed(1)} 秒`:"—"}</div>
      <div><span>恢复状态 / Trace</span>${esc(runtime.recoveryState||"—")} / <b class="mono">${esc(runtime.lastTraceId||"—")}</b></div>
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
      <h2>玩家连接</h2><p class="mono">${players.length?players.map(p=>`${esc(p.playerId)} · 座位 ${p.seatIndex} · ${esc(p.connectionState)} · ${p.latencyMilliseconds?.toFixed(0)??"—"} ms · 丢包 ${p.packetLossPercent?.toFixed(2)??"—"}% · 重连 ${p.reconnectCount??"—"} · 非法动作 ${p.illegalActionCount??"—"} · 托管 ${p.trustee===null||p.trustee===undefined?"—":p.trustee?"是":"否"} · ${esc(p.disconnectReason||"—")}`).join("<br>"):"等待遥测"}</p>
      <h2>风险事件</h2><p class="mono">${trustView.riskSignals.length?trustView.riskSignals.map(x=>`${date(x.observedAtUtc)} · ${esc(x.severity)} · ${esc(x.code)} · Trace ${esc(x.traceId)}`).join("<br>"):"暂无风险事件"}</p>
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
export function openActionDialog(target){
  state.pendingAction=null;state.currentTarget=target;
  const types=availableActions(target.kind);
  byId("actionType").innerHTML=types.map(type=>`<option value="${esc(type)}">${esc(actionNames[type])}</option>`).join("");
  byId("actionDialogTitle").textContent=target.kind==="player"
    ?"创建玩家管理申请":target.kind==="server"?"创建服务器管理申请":"创建房间管理申请";
  byId("actionTargetLabel").textContent=target.kind==="player"
    ?"目标玩家 ID":target.kind==="server"?"Dedicated Server ID":"目标房间 ID";
  byId("actionTarget").value=target.targetId;
  byId("actionTicket").value="";byId("actionReason").value="";byId("actionConfirmation").value="";
  byId("actionReasonCode").value="OPERATOR_REQUEST";byId("actionDescription").value="";
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
export function updateActionParameterFields(){
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
export async function submitAction(){
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
          reason:byId("actionReason").value,reasonCode:byId("actionReasonCode").value,
          operationDescription:byId("actionDescription").value,
          ticketId:byId("actionTicket").value,
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
    byId("actionDialog").close();state.pendingAction=null;await requestRefresh();
  }catch(error){byId("actionMessage").textContent=error.message;}
}
// 提交异人审批决定；同人审批和过期状态仍由后端拒绝。
export async function submitApproval(){
  try{
    const actionId=byId("approvalDialog").dataset.actionId;
    await request(`/admin/v1/action-requests/${encodeURIComponent(actionId)}/approvals`,{
      method:"POST",body:JSON.stringify({
        decision:byId("approvalDecision").value,comment:byId("approvalComment").value
      })
    });
    byId("approvalDialog").close();await requestRefresh();
  }catch(error){setConnection(false,error.message);}
}
// 加载脱敏玩家 360 详情，并按角色与工单要求提供受控证据查询入口。
export async function showPlayer(playerId){
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
      <div><span>丢包 / 重连</span>${p.packetLossPercent?.toFixed(2)??"—"}% / ${p.reconnectCount??"—"}</div><div><span>连接 / 托管</span>${esc(p.connectionState||"—")} / ${p.trustee===null||p.trustee===undefined?"—":p.trustee?"是":"否"}</div>
      <div><span>掉线开始</span>${date(p.disconnectedAtUtc)}</div><div><span>非法动作</span>${p.illegalActionCount??"—"}</div>
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


