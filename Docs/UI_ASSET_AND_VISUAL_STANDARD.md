# UI 资产与视觉核心规范

状态：Current
核对日期：2026-07-30
适用范围：Unreal Engine 5.8 PC、Android 手机和平板横屏 UI。

## 1. 事实来源与生产边界

- 机器清单：`SourceArt/UI/Data/ui_asset_inventory.json`；
- 麻将牌映射：`SourceArt/UI/Data/DT_TileTextureRegistry.csv`；
- 确定性资源生成：`Scripts/GenerateUIAssets.py`；
- Unreal 导入：`Scripts/ImportUIAssets.py`；
- 运行时主题：`/Game/UI/Data/DA_UITheme`；
- 按钮和面板样式：`/Game/UI/Data/DA_ButtonStyles`、
  `/Game/UI/Data/DA_PanelStyles`。

更新模型、材质、纹理或其他美术资源时，必须精确删除本次目标旧资源，再全量生成或
导入新资源；不得依赖覆盖导入或 Reimport 保留旧设置。截图必须按目标窗口边界获取，
多窗口验证分别保存。

## 2. 美术方向

现代国风与贵阳地域文化融合：青绿山水、南明河、甲秀楼和贵州山地云雾用于背景层；
苗族、布依族几何纹样与蜡染曲线只用于边缘装饰；暖金负责强调，深绿色负责长时间
对局的视觉稳定。

约束：

1. 房间号、昵称、分数和按钮标签由 UMG 渲染，不烘焙进图片；
2. 背景中央保持低细节，文化元素不得遮挡手牌和操作区；
3. 面板、按钮和输入框使用透明 PNG 与九宫格，图标使用独立 Image；
4. 深绿桌面保持低噪点，象牙白麻将牌拥有最高识别优先级；
5. AI 或程序生成资源不得复制商业游戏界面，不得带随机文字、Logo 或水印。

## 3. 设计 Token

| Token | Hex | 用途 |
| --- | --- | --- |
| PrimaryGreen | `#176B5B` | 主面板、主按钮 |
| DeepTableGreen | `#073F36` | 牌桌、深色背景 |
| JadeGreen | `#42A58C` | Focus、成功、亮边 |
| WarmGold | `#D9A441` | 主强调、房主、胡牌 |
| DarkGold | `#8C6422` | 深边框、按压态 |
| CreamWhite | `#F4EEDC` | 牌体、浅面板、主文字 |
| InkBlack | `#18201F` | 深文字、中性底 |
| WarningRed | `#B8463A` | 胡、危险、断线 |
| InfoBlue | `#397DA5` | 信息、杠、网络 |
| DisabledGray | `#6A706D` | 禁用、离线 |

圆角使用 8/16/24/32 px；边框使用 2/4/6 px。Android 默认用静态边框替代高频
Glow/Pulse，避免持续材质参数更新。

## 4. 命名和目录

| 前缀 | 含义 |
| --- | --- |
| `T_BG_*` | 全屏背景 |
| `T_Panel_*_9Slice` | 透明九宫格面板 |
| `T_Btn_<Type>_<State>` | Normal/Hovered/Pressed/Disabled 按钮底图 |
| `T_Input_*`、`T_Checkbox_*`、`T_Toggle_*`、`T_Slider_*` | 表单控件 |
| `T_AvatarFrame_*`、`T_Seat_*` | 头像和座位状态 |
| `Icon_*` | 独立图标 |
| `T_Tile_<Wan|Tong|Tiao>_<01..09>` | 规则索引 0～26 牌面 |
| `M_UI_*`、`MI_UI_*` | UI Material 与 Material Instance |
| `DA_*` | 主题、Brush、ButtonStyle、字体和注册表 |

源 PNG 位于 `SourceArt/UI`；导入目标只使用 `/Game/UI/Textures`、
`/Game/UI/Data` 和 `/Game/UI/Materials`。

## 5. 资产基线

| 类别 | 数量 | 源尺寸 | Unreal 目录 |
| --- | ---: | --- | --- |
| 背景 | 8 | 2560×1440 | `/Game/UI/Textures/Backgrounds` |
| 面板 | 11 | 256×256 | `/Game/UI/Textures/Panels` |
| 按钮状态图 | 52 | 320×112 / 192×192 | `/Game/UI/Textures/Buttons` |
| 输入/复选/滑块 | 11 | 32～256 px | `/Game/UI/Textures/Controls` |
| 头像框/座位 | 9 | 192×192 | `/Game/UI/Textures/Avatars` |
| 图标 | 24 | 128×128 | `/Game/UI/Textures/Icons` |
| 麻将牌 | 31 | 256×352 | `/Game/UI/Textures/Tiles` |
| UI Material | 9 + 5 MI | 参数化 | `/Game/UI/Materials` |
| Style/DataAsset | 6 | 数据资产 | `/Game/UI/Data` |

清单必须记录 SHA-256、尺寸、颜色模式、Alpha 和磁盘大小。

## 6. 纹理与九宫格导入

- 小图标、按钮、面板、控件、头像和麻将牌：
  `UserInterface2D`、`UI` LOD Group、sRGB、NoMipmaps、NeverStream；
- 背景：`UserInterface2D`、`UI` LOD Group、sRGB、保留 Mip、允许流送；
- Android Cook 中背景最大边建议 2048；
- 导入入口必须从项目根和 `UE_ROOT` 解析路径，不写死开发者盘符。

| 面板 | Margin(px) | Normalized | 推荐尺寸范围 |
| --- | ---: | ---: | --- |
| Main GreenGold | 40 | 0.1563 | 420×240～1600×900 |
| Dialog CreamGold / DarkGreen | 44 | 0.1719 | 520×360～1400×980 |
| PlayerInfo | 36 | 0.1406 | 280×120～720×280 |
| RoomRule | 34 | 0.1328 | 360×180～960×720 |
| Toast / Notice | 30 | 0.1172 | 320×80～1400×320 |
| ScoreRow / InputBox / Tab | 28 | 0.1094 | 按控件最小尺寸约束 |
| NetworkStatus | 26 | 0.1016 | 180×52～520×96 |

九宫格资源使用 Box，透明四角不得进入拉伸区。

## 7. 按钮与 UI Material

按钮类型包括 PrimaryGold、PrimaryGreen、SecondaryBlue、DangerRed、NeutralDark、
TransparentIcon、RoundIcon、MahjongAction、Peng、Gang、Hu、Pass、PlayTile。
每种必须具备四态；文字由 UMG TextBlock 叠加。Pressed 状态下移 5 px，Hovered
高光仅用于支持悬停的设备。

UI Material 必须使用 `User Interface` Domain、Translucent Blend 和低成本参数，
禁止读取 SceneTexture。允许的核心用途为渐变面板、柔光、Focus 描边、去饱和、
禁用、进度、网络状态、手牌选中和背景遮罩。Android 默认关闭时间动画或降低刷新频率。

## 8. Android、SafeZone 与显存

- 设计基准：1920×1080；
- 宽屏目标：2400×1080、2340×1080；
- 根节点必须使用 SafeZone；
- 背景允许流送，小图默认 NeverStream；
- UI 常驻显存目标：高档约 48 MiB、中档约 28 MiB、低档约 18 MiB；
- 最终预算必须以 Cook 后 ASTC 数据和真机采样复核；
- 图标可后续合并为 1024×1024 图集，27 张牌面可合并为 2048×2048 图集，
  但不得改变注册表键或规则索引。

## 9. 人工与自动审查

核心页面为 Login、Lobby、CreateRoomDialog、JoinRoomDialog、Room、RuleConfig、
GameHUD、Settlement、ErrorToast、ReconnectOverlay。

每张截图检查背景清晰度、文字对比、九宫格、按钮四态、图标、SafeZone、宽屏留白、
手牌识别和装饰遮挡。当前 1920×1080 与 1280×720 的 10 页面矩阵已通过；真 2340/2400
宽屏、Shipping 中文字体以及 Android 手机/平板仍需最终验收。自动化结果不能替代
目标设备上的窗口级人工审查。
