/** 单个管理面板请求的受控结果；失败只携带安全错误文案和调用方提供的降级值。 */
export interface PanelResult<T> {
  /** 当前面板可继续渲染的实时值或安全降级值。 */
  value:T;
  /** 当前面板的受控错误；其他面板不得因该值非空而中断。 */
  error:string|null;
}

/**
 * 独立收敛一个面板请求。禁用时不发起调用；失败时只替换当前面板，
 * 使房间、玩家、实例、审计等数据源可以分别降级而不会造成整页白屏。
 */
export async function settlePanel<T>(
  enabled:boolean,
  operation:()=>Promise<T>,
  fallback:T
):Promise<PanelResult<T>> {
  if(!enabled)return {value:fallback,error:null};
  try{return {value:await operation(),error:null};}
  catch(error){
    return {
      value:fallback,
      error:error instanceof Error?error.message:"面板数据源暂时不可用"
    };
  }
}
