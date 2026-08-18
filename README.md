# PhysBone to VRM 1 SpringBone

将 VRC PhysBone 高保真转换为 VRM 1.0 SpringBone，并将转换后的头像 Prefab 导出为 VRM 1.0 `.vrm`。

## 功能

- 使用 VRC 实际初始化后的 PhysBone 拓扑，而不是只猜测 Transform 层级。
- 按关节采样 Pull、Spring、Stiffness、Gravity、Gravity Falloff、Immobile、Radius 与曲线。
- 保留原骨骼层级；分支按 VRM 线性 Spring 规则拆分，必要时只生成尾端 Transform。
- 转换 Sphere、Capsule、Inside Sphere/Capsule 与 Plane Collider。
- 只绑定源 PhysBone 显式引用的 ColliderGroup，保留 Collider–Spring 碰撞关系。
- 将 Angle、Hinge、Polar 限制映射到 UniVRM 的 `VRMC_springBone_limit` 扩展。
- 分析面板显示 PhysBone、Spring、动态骨骼、模拟段、Collider、显式引用、警告与错误总数。
- 默认保留并禁用源组件，可重复重建，也可安全移除本工具生成的数据。
- 只转换当前启用且位于 active 层级中的 PhysBone/Collider，与 UniVRM 实际导出的头像状态一致；无法导出的显式 Collider 引用会作为阻断错误报告。
- 保存独立、非 Variant 的转换 Prefab 及其专用 `VRM10Object`，并直接打开 UniVRM 的 VRM 1.0 导出面板生成 `.vrm`。

## 环境要求

- Unity 2022.3 LTS
- VRChat SDK - Avatars 3.10.3 或兼容的新版本
- UniVRM 0.131.2（`com.vrmc.gltf` 与 `com.vrmc.vrm` 必须来自同一版本）

本包不包含 VRChat SDK 或 UniVRM。请先通过 VRChat Creator Companion 安装 Avatar SDK，再按 UniVRM 官方发布说明安装 0.131.2。

## 安装

### Git URL / UPM

1. 确认上述依赖已经存在于项目中。
2. 在 Unity Package Manager 选择 **Add package from git URL...**。
3. 输入 `https://github.com/ccd775/PhysBone2SpringBone.git#v2.2.0`；移除标签可跟随 `main`。

也可以下载仓库后，将仓库根目录作为本地 package 加入 Package Manager。

## 使用

1. 打开 **sayunana > PhysBone2SpringBone**。
2. 把场景中的头像根 `Animator` 拖到 **Avatar Animator**。
3. 点击 **分析转换质量与兼容性**，检查阻断错误与格式差异警告。
4. 点击 **转换为 VRM 1.0 SpringBone**。
5. 在 **Prefab / VRM 1.0 导出** 区域保存转换后的 Prefab。
6. 以该 Prefab 为导出目标，打开 UniVRM VRM 1.0 导出面板，补齐并确认 Meta、Mesh、Export Settings 后导出 `.vrm`。

默认生成的 `VRM10Object` 与 Prefab 位于 `Assets/PhysBone2SpringBoneGenerated`，不会写入安装包目录。

## Collider 与扩展

- Sphere 与 Capsule 使用标准 VRM 1.0 SpringBone Collider 数据。
- Inside Sphere/Capsule 与 Plane 使用 `VRMC_springBone_extended_collider`；Plane 同时带标准 Sphere fallback。不支持该扩展的查看器不会获得 Inside 语义或真正的无限平面碰撞。
- Angle/Hinge/Polar 使用 `VRMC_springBone_limit`。不支持该扩展的运行时会忽略这些角度限制。
- ColliderGroup 在 `.vrm` 格式中没有 Transform 归属；Collider 本身仍绑定原骨骼节点，Spring 到 Group 的引用会保留。

## 无法等价转换的功能

VRM SpringBone 与 VRC PhysBone 是不同求解器，以下能力没有格式级一一对应：

- 玩家手部、世界或全局碰撞权限；只能保留显式 Collider 引用。
- Grab、Pose、Snap To Hand 与权限过滤。
- Stretch/Squish 和 PhysBone 动画参数。
- 每帧由动画改变的 rest pose。
- 部分或逐关节 Immobile。工具仅对整条链完全 Immobile 的情况设置 VRM Center；其余情况优先保留世界空间惯性。

转换器会在分析报告中明确列出这些差异，不会静默伪造支持。

## 安全与回滚

- 建议始终保留默认选项：源 PhysBone/Collider 会被禁用而不是删除。
- “删除源组件”会删除头像层级下全部 VRC PhysBone/Collider，且此后不能安全重建或移除转换结果；只应对已经备份的副本使用。
- 场景组件的转换和移除支持 Unity Undo；新建的 `VRM10Object` 资产、Prefab 与 `.vrm` 文件属于持久资产，不会随场景 Undo 自动删除。

## License and credits

MIT License，见 [LICENSE.md](LICENSE.md)。

本项目是社区 `sayunana` PhysBone2SpringBone 工具的彻底重写版；兼容类与命名空间被保留。第三方依赖及商标说明见 [NOTICE.md](NOTICE.md)。
