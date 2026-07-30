/**
 * SSE 增量同步层：按 Last-Event-ID 断点续传，超窗时请求受控全量刷新。
 */
import {
  appendPager,byId,requestRefresh,resetPage,setConnection,state
} from "./admin-console-core";
import {renderInstances,renderPlayers,renderRooms} from "./admin-console-dashboard";

// 将 SSE upsert/remove 幂等应用到当前可见页；游标页之外的数据等待翻页或受控重同步加载。
function applyRealtimeEvent(eventType:string,envelope:any){
  if(eventType==="resync"){
    resetPage("players");resetPage("rooms");resetPage("instances");void requestRefresh();return;
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
export async function connectRealtime(){
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


