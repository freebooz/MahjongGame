/**
 * Angular 根组件调用的管理控制台生命周期适配器。
 * 这里只注册 DOM 事件和启动策略，业务请求、渲染和实时同步分别位于独立 TypeScript 模块。
 */
import {
  byId,configureRefreshHandler,resetPage,setConnection,state
} from "./admin-console-core";
import {refresh} from "./admin-console-dashboard";
import {
  submitAction,submitApproval,updateActionParameterFields
} from "./admin-console-management";
import {connectRealtime} from "./admin-console-realtime";

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
  configureRefreshHandler(refresh);
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


