/** 判断 Cookie 认证请求是否必须携带 CSRF；所有可能产生副作用的方法默认受保护。 */
export function requiresCsrf(method:string):boolean {
  return !["GET","HEAD","OPTIONS"].includes(method.toUpperCase());
}

/** 将认证失败映射为受控提示，禁止把后端异常堆栈或响应正文直接输出到页面。 */
export function authenticationFailureMessage(status:number):string {
  return status===401||status===403
    ?"管理员身份、MFA 或授权范围验证失败"
    :`管理会话建立失败（${status}）`;
}
