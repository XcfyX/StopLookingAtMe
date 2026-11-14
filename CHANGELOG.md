# Changelog

此处汇总 1.0—1.3 版本要点；小版本修复以代码为准。

## [1.3.0] - 2026-11-14
- 新增锁定后基于 Core Audio 的内置 Speakers 端点静音（可关闭），解锁时恢复原 Mute。
- 新增 `audio-restore.json` 预写日志、启动恢复、失败保留、托盘手动恢复及 Audio 日志。
- 新增 `MuteInternalSpeakersOnLock` 配置项（旧配置默认开启）。
- 面向 GitHub 公共仓库更新 README、LICENSE、.gitignore、Windows Actions 和测试清单，设置程序集版本为 1.3.0。

## [1.2.1] - 2026-11-14
- 为 `IndicatorAssets.cs` 补齐 `using System.Reflection;`，修复 `Assembly` 编译错误；其余 v1.2 功能保持不变。

## [1.2.0] - 2026-11-14
- 加入可被远程采集的透明狗头状态指示窗：左上/右上边缘探出、逐像素 Alpha、锁定联动。
- 加入默认狗头 PNG 的内嵌资源、用户更换指示图及相应设置/日志。

## [1.1.0] - 2026-11-13
- 移除时钟和日期，取消无意义的每秒重绘。
- 改用导入式图片库、SHA-256 去重和图片列表管理，兼容旧 `ImageFolder` 配置。
- 改进绘制式密码卡片的视觉设计，保留物理键盘钩子输入。

## [1.0.0] - 2026-11-13
- 首个隐私屏保：多屏覆盖、WDA 捕获排除、点击穿透、低层钩子区分 injected、PBKDF2 密码、托盘及可选自启。
