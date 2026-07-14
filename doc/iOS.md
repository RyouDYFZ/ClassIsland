# iOS / iPadOS 开发与构建

ClassIsland 的 iPhone 与 iPad 主界面由 Avalonia 统一实现。Swift 代码只存在于 ActivityKit bridge 和 Widget Extension 中；业务代码通过 `ClassIsland.Platforms.Abstraction` 提供的纯 C# API 调用实时活动与灵动岛。

## 使用 GitHub Actions 构建 unsigned IPA

构建已整合到统一的 `.github/workflows/build_release.yml`（Actions 中显示为 `Build`），unsigned IPA 不需要 Apple 证书、provisioning profile 或 GitHub Environment Secrets。

- Pull Request、`master`、`develop/v2/ios` 与 `develop/v2/misha-alpha` 的相关提交会通过仓库统一的 NUKE `PublishApp` 目标构建 Release `ios-arm64` 真机版本；这些集成构建启用 Developer Preview，并使用 `0.0.<run number>` 作为合法的临时显示版本。
- `ios-v<major>.<minor>.<patch>` 标签会使用标签中的实际版本并关闭 Developer Preview；正式发布的 `workflow_dispatch` 则使用所选 release tag 和 `primary_version`。
- 工作流运行平台抽象测试，并构建 Avalonia 主程序、Swift bridge 和 Live Activity Extension。
- 主程序使用正式 Bundle ID `cn.classisland.ios`，Extension 使用 `cn.classisland.ios.LiveActivityExtension`。
- 构建结果封装为标准 `Payload/ClassIsland.iOS.app` IPA，并生成 SHA-256 文件。
- 上传前会重新解包，检查 arm64、minimum OS、Swift back-deployment runtime、ActivityKit weak link、Privacy Manifest、Extension 和 bridge，并递归确认所有 bundle 与 Mach-O 都没有残留签名或 provisioning profile。
- 普通 CI Artifact 保留 14 天，名称为 `out_app_ios_arm64_selfContained_ipa`；正式发布会重新校验 checksum，并把 IPA 与 basename-only `.sha256` 一同加入 draft Release。

当前只支持 iOS/iPadOS 真机的 `ios-arm64` RID。SoundFlow 1.2.1 没有提供 Simulator 原生 framework，因此 `iossimulator-*` 构建会在项目校验阶段给出明确错误。主程序和 Swift bridge 最低支持 iOS 15.0；Live Activity Extension 最低支持 iOS 16.1。

推送代码后，进入 `Actions > Build` 打开对应运行，从 Artifacts 下载 unsigned IPA。`Run workflow` 是完整发布入口，需要提供 release tag 和主版本，并且 iOS 构建失败会阻止发布任务。

unsigned IPA 不能直接安装到普通 iPhone 或 iPad；安装前需由使用者通过自己的证书或侧载工具重新签名。仓库与 Action 不处理签名。

Windows 可以编译和测试 C# 层，但无法执行 Xcode、构建 Widget Extension 或封装 iOS 真机应用。

## 课程本地通知

iOS 最多保留 64 条 pending local notifications。ClassIsland 为其它系统通知预留 4 条，每次按时间顺序提交最近 60 条课程提醒，并向后扫描最多 60 天以填满这个窗口。应用进入前台、课表或提醒设置改变、NTP 同步结果改变时会立即重排；应用保持活跃时还会每 6 小时补齐一次。

`DispatcherTimer` 在应用被 iOS 挂起后不会继续执行，因此滚动窗口不会在无限期后台状态中自行补充。用户重新打开或切回 ClassIsland 后会自动补齐，无需手动操作。需要长期完全不启动应用仍持续更新计划时，必须增加服务端 push 或合适的 iOS BackgroundTasks 方案，但系统仍不保证后台任务准点执行。

## 通过 Files App 查看应用文件

iOS 与 iPadOS 版本已启用文件共享和原位打开，应用数据保存在可见的 `Documents/ClassIsland/Data` 目录。安装并至少启动一次 ClassIsland 后，可在 Files App 的“在我的 iPhone/iPad 上 > ClassIsland”中查看配置、课表、日志等文件。

从 Files App 选择并需要长期保存的 security-scoped 文件会复制到 `Documents/ClassIsland/Data/ImportedFiles`，并在配置中保存不含容器 UUID 的可迁移引用，避免选择器关闭、设备迁移或容器路径变化后失效。该目录会随备份和 `.cidata` 导出；可在“设置 > 存储 > iOS 导入文件”中查看或逐项清理未被配置引用的副本。一次性导入内容位于应用临时目录并会自动清理过期项目。

## 从 C# 调用实时活动和灵动岛

业务代码不需要引用 Swift 类型：

```csharp
using ClassIsland.Platforms.Abstraction;
using ClassIsland.Platforms.Abstraction.Models.LiveActivities;

var service = PlatformServices.LiveActivityService;
if (service.Availability == LiveActivityAvailability.Available)
{
    var result = await service.PublishAsync(new LessonLiveActivityContent(
        IntervalId: "lesson-2026-07-12-3",
        Phase: LessonLiveActivityPhase.OnClass,
        Title: "数学",
        Subtitle: "第 3 节",
        Detail: "高一（1）班 · 302 教室",
        CompactText: "数学",
        StartTime: DateTimeOffset.Now,
        EndTime: DateTimeOffset.Now.AddMinutes(40)));

    if (!result.IsSuccess)
    {
        // 根据 result.Code 和 result.ErrorMessage 记录或降级处理。
    }
}

await service.EndAsync(LiveActivityDismissalPolicy.Immediate);
```

`PublishAsync` 会复用当前由 ClassIsland 创建的 Activity，并平滑更新其 `ContentState`；`IntervalId` 仅用于业务日志和内容去重，不会强制删除并新建 Activity。非 iOS 平台、低于 iOS 16.1 的系统或用户关闭实时活动时，API 会安全返回 `Unsupported` 或 `Disabled`。

ActivityKit 单次内容数据不能超过 4 KB。应用可执行时，协调器只在课程状态/关键字段变化、准备上课与上下课边界以及每分钟一次的低频自愈时刷新；进入后台前会再同步一次。倒计时和进度由 ActivityKit 根据绝对时间自行渲染，iOS 16.2 及以上还会把课程边界写入 `staleDate`；如果进程已被挂起而无法更新/结束，系统会将旧内容标记为 stale，界面会明确提示打开 ClassIsland 刷新，而不会继续声称旧课程状态仍为最新。

“下一节课”的课前准备阶段使用与“准备上课”本地通知相同的课程 attached settings、室内/室外提前量和 channel 开关；普通课间不受该开关抑制。若应用在准备时刻已被系统挂起且此前没有活动，iOS 不允许本地代码在后台准点新建 Live Activity。`pushType: .token` 本身也不会提供本地定时执行；要保证后台首次创建与边界改写，必须完整实现 token 上报及服务端 APNs Activity push-to-start/update 链路，当前纯本地版本不作准点后台创建的保证。iPadOS 会显示系统支持的实时活动表面，但没有 iPhone 的 Dynamic Island 硬件区域。
