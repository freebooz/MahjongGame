import {bootstrapApplication} from '@angular/platform-browser';
import {AppComponent} from './app/app.component';

/**
 * Angular 22 standalone 应用入口；启动失败只记录安全摘要，不输出访问凭据或响应正文。
 */
bootstrapApplication(AppComponent).catch(() => {
  console.error('Admin 管理台启动失败，请检查前端构建和浏览器兼容性。');
});
