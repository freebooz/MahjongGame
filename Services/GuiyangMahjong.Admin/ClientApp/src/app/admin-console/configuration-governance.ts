import {byId,date,esc,hasRole,request} from './admin-console-state';

/** 配置治理面板的最小草稿读模型；正本只在用户明确展开时显示，不写入 Web Storage。 */
interface ConfigurationDraft {
  draftId:string; configKey:string; status:string; createdBy:string; createdAtUtc:string;
  ticketId:string; payloadHash:string;
}

let refreshTimer:number|null=null;
let lastSuccessAt:Date|null=null;

/**
 * 独立刷新配置面板。任何上游错误只降级本面板，并保留最后成功时间和数据年龄，
 * 不会清空房间、玩家或审计面板，也不会把内部异常堆栈显示给管理员。
 */
export async function refreshConfigurationGovernance():Promise<void>{
  const panel=byId('configurationPanel');
  if(!panel)return;
  const allowed=hasRole('governance.publisher')||hasRole('governance.approver');
  panel.hidden=!allowed;
  if(!allowed)return;
  try{
    const drafts=await request('/admin/operations/v1/configurations/drafts') as ConfigurationDraft[];
    lastSuccessAt=new Date();
    byId('configurationSource').textContent=`来源：Configuration Service · 最后成功：${lastSuccessAt.toLocaleString('zh-CN')} · 数据年龄：0 秒 · 陈旧阈值：60 秒`;
    byId('configurationDraftsBody').innerHTML=drafts.length?drafts.map(draft=>`<tr>
      <td>${esc(draft.draftId)}</td><td>${esc(draft.status)}</td><td>${esc(draft.createdBy)}</td>
      <td>${esc(draft.ticketId)}</td><td>${date(draft.createdAtUtc)}</td>
      <td>${draft.status==='Draft'&&hasRole('governance.publisher')?`<button data-config-validate="${esc(draft.draftId)}">验证</button>`:''}
      ${draft.status==='Validated'&&hasRole('governance.approver')?`<button data-config-publish="${esc(draft.draftId)}" data-ticket="${esc(draft.ticketId)}">审批并发布</button>`:''}</td>
    </tr>`).join(''):'<tr><td colspan="6" class="empty">暂无配置草稿</td></tr>';
    bindDraftActions();
  }catch{
    const age=lastSuccessAt?Math.floor((Date.now()-lastSuccessAt.getTime())/1000):null;
    byId('configurationSource').textContent=`来源：Configuration Service · 当前降级 · 最后成功：${lastSuccessAt?.toLocaleString('zh-CN')??'无'} · 数据年龄：${age??'未知'} 秒 · 陈旧阈值：60 秒`;
  }
}

/** 创建强类型草稿；仅创建待审对象，不会直接切换运行配置。 */
async function createDraft():Promise<void>{
  let payload:unknown;
  try{payload=JSON.parse(byId('configurationPayload').value);}catch{byId('configurationMessage').textContent='配置 JSON 格式无效。';return;}
  const ticketId=byId('configurationTicket').value.trim();
  const reasonCode=byId('configurationReason').value.trim();
  if(!ticketId||!reasonCode){byId('configurationMessage').textContent='工单和原因不能为空。';return;}
  await request('/admin/operations/v1/configurations/drafts',{
    method:'POST',body:JSON.stringify({configKey:'platform.runtime',schemaVersion:1,payload,reasonCode,ticketId}),trace:true,idempotent:true
  });
  byId('configurationMessage').textContent='草稿已创建，必须先验证并由另一位管理员审批。';
  await refreshConfigurationGovernance();
}

/** 绑定动态表格按钮；发布前再次输入原因并执行二次确认，服务端仍会独立执行 RBAC 与异人审批。 */
function bindDraftActions():void{
  document.querySelectorAll<HTMLButtonElement>('[data-config-validate]').forEach(button=>button.onclick=async()=>{
    await request(`/admin/operations/v1/configurations/drafts/${encodeURIComponent(button.dataset['configValidate']!)}/validate`,{method:'POST',trace:true,idempotent:true});
    await refreshConfigurationGovernance();
  });
  document.querySelectorAll<HTMLButtonElement>('[data-config-publish]').forEach(button=>button.onclick=async()=>{
    const draftId=button.dataset['configPublish']!;
    const reasonCode=window.prompt('请输入发布原因代码（将写入审计）','approved-release')?.trim();
    if(!reasonCode||!window.confirm(`确认发布配置草稿 ${draftId}？发布将原子切换新版本。`))return;
    await request(`/admin/operations/v1/configurations/drafts/${encodeURIComponent(draftId)}/publish`,{
      method:'POST',body:JSON.stringify({approvalId:crypto.randomUUID(),reasonCode,ticketId:button.dataset['ticket']}),trace:true,idempotent:true
    });
    await refreshConfigurationGovernance();
  });
}

/** 初始化面板事件和低频数据年龄刷新；凭据继续由 BFF HttpOnly 会话持有。 */
export function initializeConfigurationGovernance():void{
  const button=byId('configurationCreate');
  if(!button)return;
  button.onclick=()=>void createDraft().catch(()=>{byId('configurationMessage').textContent='创建失败，请检查权限、审批字段或上游状态。';});
  void refreshConfigurationGovernance();
  refreshTimer=window.setInterval(()=>void refreshConfigurationGovernance(),30_000);
}

/** Angular 销毁视图时清理定时器，防止热更新产生重复轮询。 */
export function disposeConfigurationGovernance():void{
  if(refreshTimer!==null)window.clearInterval(refreshTimer);
  refreshTimer=null;
}
