import {AfterViewInit, Component, OnDestroy} from '@angular/core';
import {
  disposeAdminConsole,
  initializeAdminConsole
} from './admin-console/admin-console';

/**
 * Admin 管理台根组件。
 *
 * 当前组件托管完整的监控、审批与受控证据查询视图；命令提交仍由后端执行
 * RBAC、二次确认、异人审批和实时状态校验，前端提示不能替代服务端安全边界。
 */
/**
 * 管理控制台的 Angular 根组件。
 *
 * 组件只负责把 Angular 生命周期桥接到现有控制台模块；业务状态、请求与面板降级仍由
 * 可独立测试的 TypeScript 模块管理，避免根组件演化成新的巨型文件。
 */
@Component({
  selector: 'app-root',
  standalone: true,
  templateUrl: './app.component.html'
})
export class AppComponent implements AfterViewInit, OnDestroy {
  /**
   * 等待 Angular 创建全部对话框和表格节点后再注册控制台事件，避免访问尚未挂载的元素。
   */
  ngAfterViewInit(): void {
    initializeAdminConsole();
  }

  /**
   * 销毁组件时停止实时轮询，防止开发热更新或未来路由切换留下后台定时器。
   */
  ngOnDestroy(): void {
    disposeAdminConsole();
  }
}
