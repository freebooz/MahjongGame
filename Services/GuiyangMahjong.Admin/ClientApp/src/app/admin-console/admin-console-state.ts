/**
 * 管理控制台共享状态、HTTP 边界和安全渲染工具。
 * 令牌仅驻留页面内存；模块不持久化凭据，也不推导服务端游标或授权结果。
 */
import {authenticationFailureMessage,requiresCsrf} from "./admin-security";
import {settlePanel} from "./panel-degradation";
// 管理台仍需操作大量历史对话框节点；统一入口在迁移期提供受控 DOM 访问，后续组件化时逐步收窄类型。
export const byId=(id:string):any=>document.getElementById(id);
export const state:any={
  // 企业令牌仅在会话交换调用栈中短暂存在；成功后只保留内存 CSRF 和 HttpOnly Cookie。
  legacyBearer:"",csrfToken:"",authenticated:false,deviceId:crypto.randomUUID(),
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
// 分页和管理动作通过注入的刷新器回到仪表盘层，避免共享状态模块反向依赖渲染模块。
let refreshHandler:()=>Promise<void>=async()=>{};
export function configureRefreshHandler(handler:()=>Promise<void>){refreshHandler=handler;}
export function requestRefresh(){return refreshHandler();}

export const esc=value=>String(value??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
export const date=value=>value?new Date(value).toLocaleString("zh-CN",{hour12:false}):"—";
export const hasRole=role=>state.me?.roles?.includes(role);
export const actionNames={
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
export function adminHeaders(options:any={}){
  const headers:any={"X-Admin-Device-Id":state.deviceId,...(options.body?{"Content-Type":"application/json"}:{})};
  if(state.legacyBearer)headers.Authorization=`Bearer ${state.legacyBearer}`;
  if(state.csrfToken&&requiresCsrf(options.method||"GET"))
    headers["X-Admin-CSRF"]=state.csrfToken;
  if(options.trace)headers["X-Trace-Id"]=crypto.randomUUID();
  if(options.idempotent)headers["Idempotency-Key"]=crypto.randomUUID();
  return headers;
}
// 使用企业短期 Bearer 建立 BFF 会话；令牌不会写入 state、Web Storage、URL 或日志。
export async function establishBrowserSession(token:string){
  const response=await fetch("/admin/bff/v1/session",{
    method:"POST",credentials:"same-origin",
    headers:{Authorization:`Bearer ${token}`,"X-Admin-Device-Id":state.deviceId}
  });
  if(response.status===404){
    // 服务端关闭 BFF 时进入显式兼容模式；令牌仍仅驻留当前页面内存。
    state.legacyBearer=token;state.authenticated=true;return;
  }
  if(!response.ok)throw new Error(authenticationFailureMessage(response.status));
  const session=await response.json();
  state.csrfToken=session.csrfToken;state.legacyBearer="";state.authenticated=true;
}
export async function request(path:string,options:any={}){
  const headers=adminHeaders(options);
  const response=await fetch(path,{...options,credentials:"same-origin",headers:{...headers,...options.headers}});
  if(response.status===401){state.authenticated=false;throw new Error("管理会话无效、已撤销或设备/网络发生异常");}
  if(!response.ok){
    let message=`管理服务返回 ${response.status}`;
    try{message=(await response.json()).message||message;}catch{}
    throw new Error(message);
  }
  return response.status===204?null:response.json();
}
// 更新页头连接状态；降级刷新使用离线颜色提醒，但不会清空仍可用的其他面板。
export function setConnection(ok,message){
  const node=byId("connection");node.className=`status ${ok?"online":"offline"}`;
  node.textContent=message;
}
// 无房间查看权限或总览失败时提供安全空投影，避免模板读取未定义字段。
export function emptyOverview(){
  return {totalRooms:0,activeRooms:0,abnormalRooms:0,totalConnectedPlayers:0,dedicatedServerInstances:0};
}
// 把单个面板请求转换为可降级结果，避免任一依赖失败造成整页白屏。
export async function settle(enabled,operation,fallback){
  return settlePanel(enabled,operation,fallback);
}
// 生成服务端游标分页查询；页大小仅是请求值，最终上限始终由 Admin API 强制执行。
export function withPage(query:URLSearchParams,pageKey:string){
  const page=state.pages[pageKey];
  query.set("pageSize",String(state.me?.realtime?.defaultPageSize||100));
  if(page.cursor)query.set("cursor",page.cursor);
  return query;
}
// 在表格末尾渲染上一页/下一页；历史只保存不透明游标，不在浏览器推导偏移量。
export function appendPager(bodyId:string,columns:number,pageKey:string,page:any){
  const body=byId(bodyId),pager=document.createElement("tr");
  pager.innerHTML=`<td colspan="${columns}" class="pagination">
    <button data-page-direction="previous" ${state.pages[pageKey].history.length?"":"disabled"}>上一页</button>
    <span>本页 ${page.items.length} 条</span>
    <button data-page-direction="next" ${page.hasMore?"":"disabled"}>下一页</button>
  </td>`;
  body.appendChild(pager);
  (pager.querySelector("[data-page-direction=previous]") as HTMLButtonElement).onclick=()=>{
    state.pages[pageKey].cursor=state.pages[pageKey].history.pop()||null;void requestRefresh();
  };
  (pager.querySelector("[data-page-direction=next]") as HTMLButtonElement).onclick=()=>{
    state.pages[pageKey].history.push(state.pages[pageKey].cursor);
    state.pages[pageKey].cursor=page.nextCursor;void requestRefresh();
  };
}
// 筛选条件变化后丢弃旧游标链，防止把上一查询条件的不透明游标发送给服务端。
export function resetPage(pageKey:string){
  state.pages[pageKey]={cursor:null,next:null,history:[]};
}
// 使用受控中文状态与数据年龄渲染来源卡片，绝不展示内部地址或原始异常正文。
export function renderSourceHealth(metadata){
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
export function reliabilityHtml(metadata){
  if(!metadata)return '<div class="source-health-inline unsafe">未提供来源新鲜度信息，请勿据此执行高危操作。</div>';
  const summary=(metadata.sources||[]).map(
    source=>`${esc(source.source)}=${esc(source.status)}${source.dataAgeSeconds!=null?`(${source.dataAgeSeconds.toFixed(1)}s)`:""}`).join(" · ");
  return `<div class="source-health-inline ${metadata.safeForHighRiskActions?"":"unsafe"}">
    <strong>${metadata.safeForHighRiskActions?"实时状态可用":"数据已降级；高危操作将由服务端实时复核"}</strong><br>
    <span>${summary||"无来源状态"}</span>
  </div>`;
}


