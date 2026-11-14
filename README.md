# PrivacyScreen

**面向AI agent持续工作、远程桌面使用场景的 Windows 一键隐私屏保**：本地屏幕显示持续运行的屏保，同时允许兼容的远程控制软件查看并操作真实桌面。适用于Todesk，向日葵，uu远程，相对于自带的隐私屏功能可以在退出远程期间持续保护屏幕内容以及自动关闭扬声器，保护电脑不被（同事、同学、舍友、熊孩子！）干扰，远程控制过程中远程工具可以穿透屏保，不被影响，且在远程界面上角有持续隐私屏是否开启提示，使用更安心

**English Summary:** PrivacyScreen is a lightweight, open-source Windows privacy overlay for remote-desktop workflows. It shows a configurable screensaver on local monitors, excludes that overlay from compatible screen capture, differentiates low-level injected versus physical input, and keeps an optional remote-visible dog-head indicator. Version 1.3 adds crash-recoverable muting of probable built-in speakers. This is a privacy convenience tool, **not** a Windows security lock.

> **重要**：本工具不是系统级锁屏。`Ctrl+Alt+Del` 及安全桌面无法被普通用户进程拦截；同等权限的其他进程可以终止本程序。远程软件可能使用其他捕获或输入路径，网易 UU 远程等软件的兼容性需要按设备实测。

## 功能

- **隐私屏保**：托盘及全局快捷键开启，多显示器覆盖；有图按导入顺序轮播，无图显示渐变及自定义文字；不显示时钟日期。
- **本地/远程输入区分**：锁定后通过 `WH_KEYBOARD_LL` / `WH_MOUSE_LL` 拦截被标为非 injected 的输入，放行被标为 injected 的输入，不移动焦点、不阻挡远程鼠标。
- **密码解锁**：本地 `Ctrl+Alt+U` 呼出绘制式密码卡片，物理键盘输入，通过 PBKDF2-SHA256 哈希验证；密码不经普通 `TextBox`，不在远程捕获画面中呈现。
- **独立图片库**：多选导入 PNG/JPEG/BMP，到 `%LOCALAPPDATA%\PrivacyScreen\images\` 按内容 SHA-256 去重，支持删除及清空。
- **远程可见的狗头指示**：锁定后才显示，主屏左上或右上（默认右上），从屏幕边缘露出约 72%；支持替换自定义图片。
- **内置扬声器静音**：v1.3 新增，成功锁定后尝试静音识别出的内置 Speakers 端点；解锁恢复原 Mute，强杀后下次启动自动按日志恢复；可关闭。
- **可选开机自启**：仅当前 Windows 用户，托盘常驻。

## 工作原理

1. 屏保窗口顶层显示、点击穿透且不抢焦点；调用并检查 `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`，在兼容捕获链路中屏蔽本地屏保层。
2. 全局低层钩子读取 `LLKHF_INJECTED` / `LLMHF_INJECTED`。物理输入被屏蔽（除本地解锁组合及密码输入流程），被标记为 injected 的输入可通过屏保层操作下方桌面。**Injected 不等于 UU 身份认证**；驱动级模拟 HID 的事件可能没有该标志。
3. 指示图使用独立 `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 窗口，`UpdateLayeredWindow` 像素级 Alpha，**特意不调用捕获排除（WDA_NONE）**，以供远程端显示。
4. 音频使用 Core Audio 的 `IMMDeviceEnumerator` / `IMMDevice` / `IAudioEndpointVolume.GetMute/SetMute`，只改变 Mute，不调整音量，不禁用 PnP 设备。仅在锁定成功后运行；是可失败降级的附属功能。

参考：[SetWindowDisplayAffinity](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)、[音频端点 FormFactor](https://learn.microsoft.com/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-endpointformfactor)、[SetMute](https://learn.microsoft.com/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-setmute)、[音频连接类型](https://learn.microsoft.com/windows/win32/api/devicetopology/ne-devicetopology-connectortype)。

## 系统要求

- Windows 10 **2004 或更高版本**、Windows 11，x64。
- 从源码构建：**.NET 8 SDK**。
- 使用 `win-x64` 自包含单文件发布版：**无需另外安装 .NET 运行时**。不需要管理员权限。
- 远程兼容性的前提是远程软件遵循屏幕捕获排除，并把输入作为可识别的注入事件交给 Windows。

## 从源码构建

```powershell
cd PrivacyScreen.WinForms
dotnet restore
dotnet publish .\PrivacyScreen.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

或在 `PrivacyScreen.WinForms` 中运行 `./build-win-x64.ps1`。生成 `PrivacyScreen.WinForms\publish\PrivacyScreen.exe`。

GitHub Actions 的 `windows-latest` 构建也会自动产生 `PrivacyScreen-win-x64` Artifact，下载解压后直接运行 EXE；它是 CI 构建物，不等于已通过机器/网易 UU 联机验证的正式 Release。

## 第一次使用

1. 运行 EXE，首次设置 **8–64 字符 ASCII 密码**（不可留空）。后续设置页面留空即保留现有密码，配置文件存盐和哈希而非明文。
2. 托盘图标或默认 `Ctrl+Alt+S` 开启；`Ctrl+Alt+U` 在本地呼出密码卡片，输入密码回车解锁；Esc 取消密码提示。
3. 从「设置」导入屏保图片、调整轮播、快捷键、自定义文字、开机自启等。
4. 远程使用前，**请先在本机进行一次开启/解锁回归**，随后在网易 UU 远程中确认真实桌面与狗头指示均可见、远程鼠标键盘能操作。
5. 紧急情况可以通过 Windows 安全屏幕 `Ctrl+Alt+Del` 注销/重启；程序异常终止时系统会撤销进程级低层钩子，重启程序可尝试恢复扬声器静音。

## 设置项与兼容

| 配置字段 | 默认值 | 说明 |
| --- | --- | --- |
| `StartHotkey` | `Ctrl+Alt+S` | 开启 |
| `UnlockHotkey` | `Ctrl+Alt+U` | 本地解锁组合 |
| `SlideSeconds` | `30` | 轮播间隔（秒） |
| `Message` | `稍事休息 · 马上回来` | 无图时显示 |
| `ImportedImages` / `ImageLibraryVersion` | `[]` / `0` | 导入式图片库；旧 `ImageFolder` 只做迁移兼容 |
| `AutoStart` | `false` | 当前用户自启 |
| `ShowLockIndicator` | `true` | 锁定时显示狗头 |
| `IndicatorPosition` | `RightTop` | `RightTop` / `LeftTop` |
| `IndicatorImageFile` | `""` | 空表示内嵌默认图 |
| **`MuteInternalSpeakersOnLock`** | **`true`** | v1.3 新增，锁定后尝试静音内置扬声器 |
| `PasswordSalt` / `PasswordHash` / `PasswordIterations` | 首次设置生成 | PBKDF2-SHA256 数据；升级及密码输入留空时不更改 |

配置文件：`%LOCALAPPDATA%\PrivacyScreen\config.json`；日志：同目录 `app.log`。这些是**运行时私有文件**，源码仓库不包含真实配置、个人图片库或日志。老 v1.2.1 配置反序列化时会取新字段默认值，保存设置后写入新字段，不重设密码、不改变已有屏保/狗头设置。升级前建议手动备份用户配置文件。

## v1.3 音频识别与恢复

**识别口径（不是无误判的硬件检测）：** 枚举活动的 `eRender` 端点；只考虑 `PKEY_AudioEndpoint_FormFactor=Speakers`。用 `IDeviceTopology` 查找连接到声卡的适配器连接器，`Physical_Internal` 是内置物理连接证据；通过连接设备 ID（能取得时）与 `PKEY_DeviceInterface_FriendlyName` 排除可识别的 USB、蓝牙、HDMI/DP、虚拟或远程设备。驱动可能隐藏拓扑或设备路径；无法确认时，仅回退到默认**多媒体渲染端点**中的 `Speakers` 候选，并继续排除明显外接设备。**默认 Speakers ≠ 必然笔记本内置喇叭**：某些驱动、插入 3.5mm 耳机的共用端点或改名设备仍可能无法区分。请在七彩虹 X15 AT 上核对 `app.log`。如果不符合预期，关闭选项。

- 仅在**所有屏保窗口成功保护**之后静音；没有匹配端点、属性读取或 Core Audio 调用失败都只记录 `Audio:` 日志，不会让屏保回滚。
- 对每个选中端点读取 `GetMute`。原本已经静音的端点不变，其他端点先把 `Id`、`Name`、`OriginalMute=false` 写入 `%LOCALAPPDATA%\PrivacyScreen\audio-restore.json`，再调用 `SetMute(true)`。**不读写音量数值**。
- 解锁时只对记载的端点恢复为原 Mute；未能恢复的条目保留。进程被强制结束时，**下一次启动会优先尝试恢复**。若音频设备当时未连接、端点 ID 已变化或恢复失败，记录会保留供下次重试。未锁定且有剩余记录时，托盘会出现「恢复扬声器」手动入口。
- 如果设置关闭静音，未来锁定不会主动查询或修改音频；**但此前的崩溃恢复记录仍会在下次启动时处理**，避免丢失声音。。
- 原状态已静音的端点永不被解除静音。异常强杀后如果用户在屏保期间通过其他工具手动改过相同端点的 Mute，恢复记录仍会按锁定前状态处理；这是基于快照的已知取舍。

## 已知局限与安全边界

- **不是系统安全锁**：`Ctrl+Alt+Del` 无法拦截，任务管理器/同权限进程/管理权限进程可关闭本工具。不要用它保护高敏感数据或替代 Windows 锁定。
- `WDA_EXCLUDEFROMCAPTURE` 不是 DRM，也不覆盖所有截屏、GPU、驱动或物理摄像头捕获路径；网易 UU 的捕获和输入机制随版本/配置可能变化。
- 各显示器的屏保窗口能够捕获排除，不保证其他软件会显示指示图；音频 loopback 的行为单独待验证。
- 没有系统级物理与远程输入身份验证；任何能产生 injected 标志的进程输入都可能被放行。驱动级 HID 远程输入也可能被误当实体键盘拦截。
- Windows 的设备拓扑属性依赖驱动，内置扬声器识别只能尽力而为；断开或被重新编号的音频设备可能需要重新连接才能完成恢复。

## 资源许可

代码以 [MIT License](LICENSE) 发布，Copyright (c) XcfyX。

**`PrivacyScreen.WinForms/Assets/indicator-default.png` 来源于用户提供的表情包拼图，经提取和透明去杂边处理，仅作为默认状态指示图。** 仓库使用者可以在设置中更换为自己的图片。**在仓库公开发布前请确认原素材的使用授权；如涉及版权，请替换为自行创作或有明确许可的图像。** MIT 软件许可证不应被理解为对第三方图片授予权利。

## 版本与贡献

版本记录见 [CHANGELOG.md](CHANGELOG.md)，手动与回归清单见 [docs/TESTING.md](docs/TESTING.md)。欢迎通过 Issue 反馈 UU 远程兼容性、Windows 版本、设备 FormFactor/Topology（**请删去私人路径及可识别信息**）。

未来可以考虑：音频端点预览/白名单、端点重连时提醒、更多屏保画面配置。
