# Unreal Engine 5.8工具链恢复与验证报告

## 结论

当前机器没有可用的Unreal Engine 5.8完整工具链，无法真实执行`GuiyangMahjongServer`构建、快照恢复自动化测试或确定性重放测试。本次没有使用旧二进制伪报通过，也没有改用UE 5.4/5.5/5.6冒充5.8。

## 实际检查

- 注册表`HKLM\SOFTWARE\EpicGames\Unreal Engine\5.8`指向`D:\Program Files\Epic Games\UE_5.8`；
- 该目录缺少`Engine\Build\BatchFiles\Build.bat`、`RunUAT.bat`、UnrealBuildTool、`UnrealEditor.exe`和`UnrealEditor-Cmd.exe`；
- Epic Launcher全局Manifest仅存在`QuixelBridge_5.8`和`FabPlugin_5.8`，不存在UE 5.8引擎主体Manifest；
- `H:\UE58_Source`只有`.git`目录，不是可构建的源码引擎；先前文档提到的`D:\UnrealEngine-5.8.0-release`当前不存在；
- D盘约有795 GiB可用空间，容量不是当前阻断点；
- 项目已有`GuiyangMahjongServer.Target.cs`、Server Build.cs和自动化测试源码；
- 当前`Binaries\Win64\GuiyangMahjongServer.exe`时间为2026-07-22 12:39，服务器源码最近更新时间为2026-07-31 19:16，因此二进制已过期；
- `Scripts\Test-PackageIsolation.ps1`通过；`Test-TargetModuleGraph.ps1`因没有有效UE根目录而停止。

## 必须由环境所有者完成的恢复

1. 使用Epic Games Launcher安装或校验Unreal Engine 5.8引擎主体，安装位置可继续使用`D:\Program Files\Epic Games\UE_5.8`；
2. 安装时至少包含Windows C++工具链支持；若需Linux Server打包，还需UE 5.8匹配的Linux交叉编译工具链；
3. 确认下列文件真实存在且可执行：
   - `Engine\Build\BatchFiles\Build.bat`；
   - `Engine\Build\BatchFiles\RunUAT.bat`；
   - `Engine\Binaries\Win64\UnrealEditor-Cmd.exe`；
4. 设置当前终端`UE_ROOT=D:\Program Files\Epic Games\UE_5.8`，不要修改项目源码来绕过工具链检查。

## 工具链恢复后的验收命令

```powershell
& "$env:UE_ROOT\Engine\Build\BatchFiles\Build.bat" `
  GuiyangMahjongServer Win64 Development `
  -Project="H:\MahjongGame\GuiyangMahjong.uproject" -WaitMutex -NoHotReload

& "$env:UE_ROOT\Engine\Binaries\Win64\UnrealEditor-Cmd.exe" `
  "H:\MahjongGame\GuiyangMahjong.uproject" `
  -Unattended -NullRHI -NoSound -NoSplash `
  "-ExecCmds=Automation RunTests GuiyangMahjong.GameServer.Snapshot.;Quit" `
  -TestExit="Automation Test Queue Empty" -Log

& "$env:UE_ROOT\Engine\Binaries\Win64\UnrealEditor-Cmd.exe" `
  "H:\MahjongGame\GuiyangMahjong.uproject" `
  -Unattended -NullRHI -NoSound -NoSplash `
  "-ExecCmds=Automation RunTests GuiyangMahjong.Rules.SnapshotDeterminism;Quit" `
  -TestExit="Automation Test Queue Empty" -Log
```

完成后还必须运行`Scripts\Test-TargetModuleGraph.ps1 -EngineRoot $env:UE_ROOT`和`Scripts\Test-PackageIsolation.ps1`，确认Client不含ServerOnly/Agones，Server不含ClientOnly/EditorTools，且测试日志中不存在失败项。

## 未完成项

- UE 5.8引擎主体安装/校验：未执行，属于外部大体积依赖安装；
- 当前源码Server构建：被工具链阻断；
- 快照恢复和确定性重放自动化测试：被`UnrealEditor-Cmd.exe`缺失阻断；
- Linux Server构建：除引擎主体外还需核对匹配的交叉编译工具链。

在上述环境前置条件满足前，不应进入真实Agones镜像发布或把旧Server二进制用于故障演练。
