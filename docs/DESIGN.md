# OpenPet 设计说明

## 目标

在利民 2.8" 320×240 USB 小屏上，独立地养一只宠物：屏幕只负责显示，桌宠的养成、
动画、台词、存档都在本机完成，不与 DSH 通信。

## 分层

```
┌─────────────────────────────────────────────┐
│ OpenPet.App (WPF)                           │
│  MainWindow / tray / hotkeys / preview      │
│  PetRuntime  ← 组装、配置、存档、生命周期     │
│  PetEngine   ← 养成数值 + 动画状态机 + 物理   │
│  AnimationLibrary / MemeLibrary / FrameCache │
│  PetSceneRenderer (Skia)                    │
│  LibraryWindow ← 106 动作 / 27 表情素材库     │
│  DisplayService (20 Hz 推流，后台线程)        │
│  Headless (--snapshot / --screen-test)      │
└───────────────┬─────────────────────────────┘
                │ 320×240 BGRA
┌───────────────▼─────────────────────────────┐
│ OpenPet.Core                                │
│  BgraImage / ImageTransforms (rot90)        │
│  Rgb565Encoder / FrameAssembler / FramePump │
└───────────────┬─────────────────────────────┘
                │ 240×320×2 RGB565 + 20B header
┌───────────────▼─────────────────────────────┐
│ OpenPet.Devices                             │
│  DeviceEnumerator / DeviceCatalog           │
│  HidTransport (513B reports, overlapped)    │
│  ThermalrightType2 (handshake + frame)      │
└─────────────────────────────────────────────┘
```

## 帧循环

`DisplayService` 在专用后台线程运行：

1. 每 50 ms 调 `PetEngine.Tick(DateTime.Now)`；引擎内部有锁，UI 的喂食/点击可以并发进入。
2. `CreateSnapshot()` 复制一份只读快照（数值、当前动画帧、气泡、粒子、坐标、朝向）。
3. `PetSceneRenderer.Render(snapshot, 320, 240)` 画出 BGRA。
4. 预览每 ~90 ms 通过 `PreviewReady` 事件交给 UI（`WriteableBitmap`）。
5. 如果设备已连接：
   - `ImageTransforms.RotateAndFlip` 做 rot90（PM=53 的行序是 240×320）；
   - `Rgb565Encoder.Encode` 做 RGB565 + 4×4 Bayer 抖动；
   - `FrameAssembler` 加 20 字节头并按 512 字节对齐；
   - `FramePump` 内容未变时最多 1.2 s 补发一次保活；
   - `ThermalrightType2.SendFrame` 分块写 HID。
6. 连接失败/被占用时只跑预览，每 5 s 在后台重连，不阻塞渲染。

## 养成模型

数值都在 `PetEngine` 内，0–100：

- **饱食**：默认 4/h 衰减，睡觉时 35% 速率。
- **心情**：2.2/h；饥饿时额外 +2/h 惩罚；玩耍/抚摸/喂食提升。
- **精力**：醒着 3.5/h 衰减；睡觉 22/h 回复。
- **经验/等级**：每次互动给经验；升级阈值 `baseExp × level^growth`；升级触发 `events.levelup` + 星星粒子 + 气泡。
- **作息**：`sleepStart`–`sleepEnd`，夜里精力低于 95 会自己睡；精力见底（默认 12）也会睡；睡到 `wakeEnergy`（85）且不在作息窗口才醒。
- **离线结算**：启动时用 `lastTickUtc` 到现在的时长补算衰减/回复；超过 1 小时会说 `lines.offline`。

状态每 60 s 以及每次互动后落盘到 `%LocalAppData%\OpenPet\pet-state.json`（临时文件 + 原子替换）。

## 动画状态机

`ResolvedAnimations` 把配置里的动画名与实际素材求交集；缺失的只警告一次。

- 当前动画是 `idle` 循环，`AutoPick` 每隔 8–14 s 按 `animationWeights` 掷骰：
  - `idle`：继续待机；
  - `turn`：播 `animations.turn`，播完翻转 `facing`；
  - `move`：从 `moves.actions` 抽一个，按 `leadSec/tailSec` 平移 `offsetX`；
  - 其余概率：按分类权重抽 `categories[].actions`，朝右时跳过 `noMirror`。
- 互动/事件动画（`events.feed` 等）播一次，播完回 idle 并暂停 2–3 s。
- 点击回应走 `clicks` 池；拖拽循环 `drag`；睡觉循环 `events.sleep`；醒来播 `events.wake`。
- 缺素材时按 `events` 的 fallback 回落到 `clicks` 或 `idle`。

## 拖拽物理

- 预览坐标映射到 320×240；命中框是精灵中心 60% 宽、整个高度。
- 拖动时 `offsetX/Y` 跟手；松手把鼠标速度乘 `throwPower` 作为初速度。
- 每 tick：`vy += gravity*dt`，地面 `offsetY=0` 反弹 `restitution`，地面摩擦 `groundFriction`，
  天花板可选反弹；速度足够小就落地回到 idle。
- `offsetX` 被限制在 `display.motionRangeX` 内，避免角色完全走出屏幕。

## 渲染

- 逻辑画布固定 320×240；其它分辨率等比居中。
- 背景按本地时间使用四套渐变（清晨/白天/黄昏/夜晚），夜晚画星星、白天画尘埃，外加径向柔光和暗角。
- HUD：顶部等级/名字/陪伴天数/时钟 + 经验细线；底部三条状态（饱食/心情/精力）。
- 气泡：字体由 `font.family` 决定（默认微软雅黑 13.5 px，`candy` 可切到上首软糖体），
  自动按字符换行（最多 3 行），带小尾巴指向头顶。
- 睡觉：半透明夜幕 + 飘动的 Zzz；踩脚位置按素材帧底部 8.3% 的透明留白校正到 `floorY`。

## 配置与素材

- 配置为 JSONC，`JsonSerializer` 开启注释跳过和尾随逗号；`PetConfigLoader` 做范围/颜色/时间校验。
- 素材：`tools/animations.json` 是全部 106 个动作的清单；`prepare_animations.py` 用 ffmpeg
  `-c:v libvpx-vp9` 解码（保 alpha），输出 320×180 / 12 fps 的 WebP 帧 + `meta.json`。
- 素材库：`AnimationLibrary` 提供全部动作名；`MemeLibrary` 扫描 `assets/memes/*.png`。
  `LibraryWindow` 点播动作时调用 `PetEngine.PlayAction`，表情包调用 `PetEngine.ShowMeme`。
- 表情包：碎碎念有 `display.memeChance` 概率随机附一张；`PetSceneRenderer.DrawMeme` 在右侧画
  104×104 的白色贴纸卡，约 `memeDurationSec` 秒后淡出。
- 运行期 `FrameCache` 以 LRU 缓存解码后的 `SKBitmap`（默认 180 帧，约 41 MB）。

## 关键不变量

- 只有显示线程碰 `PetSceneRenderer`、`FrameCache`、`HID`；UI 只读快照。
- `PetEngine` 所有公开入口都在同一把锁里；`PetRuntime` 的 Reload 先停显示线程再换引擎/渲染器。
- 屏幕不可用永远不阻塞 UI：连接在 `Task` 里做，失败退化为预览。
- 配置里 `idle` 必须至少有一个可用素材，否则启动时明确报错。
