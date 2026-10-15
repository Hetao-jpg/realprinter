第一部分 · 玩家手册
它能做什么
先使用 / 装备机器 → 左键点商品 → 窗口里选档位 → 收 20 信用点，真值真的变了。

红机只能"假装"改（只动假值，真值不动）；这台机器真值假值一起改，
所以游戏里算价、验真、鉴定助手读到的都是同一个值。

怎么拿到机器
正式途径：供货商上门时买。 供货商每次来店里摆库存，货架上会多出一台
未拥有状态的真实打印机，你付款买下（默认 100 信用点）。他每次来都摆一台，可以一直买。

备用途径：F6 直领（默认关闭）。 在 ModManager → 真实打印机 面板勾上
EnableF6Give，打开后按 F6 领取一台价值 0 的机器（防刷钱）。
关闭时按 F6 无任何反应、不弹提示。正式获取途径仍是等供货商。

使用方法
步骤	说明
①	在仓库里找到「真实打印机」
②	右键 → 使用，或装备到手上
③	左键点击目标商品（香烟 / 化学品 / 金属锭 / 注射器）
④	在弹出的窗口里选档位，扣费写入
★★ 直接拖动到商品上不会触发（与原版红机不同）。
原因：拖动路径被主动短路 —— 拖动结束时游戏给 mod 的 target 是半死对象，
进去读商品数据会 AccessViolation 猝死（连 try/catch 都拦不住）。
静默跳过是唯一稳定解。
→ tooltip 里已明写「先使用 / 装备，再左键点目标商品（拖动无效）」。

只支持这四类（核桃 2026-10-03 拍板）
你要的	游戏内类别
香烟	CATEGORY_GENUINE_CIGARETTE
化学品（含毒药）	CATEGORY_CHEMICAL_PURITY
金属锭	CATEGORY_INGOT_PURITY
免疫宁注射器	CATEGORY_GENUINE_INJECTOR（真假 + 可否使用两条真值都列出来）
毒药按化学品算 —— 游戏真值体系里没有「毒药」这一类，
所以判定依据是"这件物品身上有没有化学品纯度真值"。

其余类别（酒、水、装备成色、印花票…）点了会给一句人话提示，不开窗。
判定只认 category，不写死物品名单 → 游戏加新物品自动支持。

外观
机器沿用原版红色标签打印机的外观（借它的 sprite，不染色）。
形状压到 1×2 格（原版是 2×3），图按 ppu×1.5 等比缩放贴合格子
—— 都是为了让供货商货架上放得下（柜台只有 18×4=72 格）。

配置项（ModManager → 真实打印机）
V1.4.13 起 4 项（能真正"改一次就用一次"的才露出来，其余走代码硬编码默认值）。

项	默认	说明
FeatureCost	20	改一次真值收多少信用点，设 0 = 免费
VerboseLog	关	详细日志。默认只打启动横幅、自检结论与报错；勾上才看流水
SupplierStock	开	供货商每次摆库存追加一台真实打印机
EnableF6Give	关	F6 直领开关。打开后按 F6 领取价值 0 的机器；关闭时不响应
★ cfg 改了立刻生效，不用重启游戏。

已不再暴露到 cfg（值走硬编码默认）：

项	硬编码值	说明
DealerPrice	100	供货商货架上那台的售价
BaseMachineValue	50	机器自身基础价值（原版红机口径，目前无调用方）
常见问题
症状	怎么办
mod 完全没反应	UserData/MelonPreferences.cfg 里没有 [RealPrinter] 段 → mod 没被加载，检查 MelonGame 常量，或确认 DLL 放对了
左键点物品没反应	先确认机器是"使用 / 装备"状态，不是"拖动"。若仍无反应，看启动日志有没有「Harmony 补丁已挂 ✓」
拖动到商品上没反应	正常行为 —— 拖动被主动短路。用法：先使用/装备，再左键点商品
F6 按了没反应	正常 —— EnableF6Give 默认关闭。想用去 ModManager 勾上
供货商来了但货里没打印机	柜台满了。日志搜 [供货商]；若打「回读前柜台没找到」= 被游戏静默销毁，等下次补货
机器图比格子大	缩图失败，3 秒后自动重试；摆货与交互完全不受影响，只是显示溢出。看启动横幅「机器外观：」那行
改了没生效	面板勾上 VerboseLog，看「已执行档位动作」+「商品侧回读 最终 GetNegociatedValue=」
提示里"价值 X → X ★没变"	说明真没改。勾 VerboseLog 看回读断言
读档后失效	勾 VerboseLog 看「读档检查完成」的指针白名单条数（应 ≥1）
拖动机器会闪退	确认部署的是 v1.1.9+（含拖动短路）
面板里出现两行 RealPrinter	Mods/ 目录里有两个版本的 DLL，删掉旧的




第二部分 · 开发文档
版本历史
V1.4.14（2026-10-15）· 修复右键放大镜/水桶误触发真实打印机
根因：`_machinePtrs` 指针白名单只增不清 —— 读档后旧指针全部作废却永驻表内；游戏中途销毁机器（卖掉/丢垃圾桶/客户买走）也无人注销。IL2CPP 分配器复用地址后，放大镜/水桶拿到旧打印机地址 → 热路径 `IsOurMachine` 查表误判为真 → `TryClaim` 通过 → May/Can 强制 true 短路原版（放大镜 INSPECTABLE 闸门被绕过）→ Target 开编辑窗。
改动	说明
MachineRegistry 新增 RebuildAndPruneRegistry	全量扫存档重建白名单的同时做差集清理：先扫出"当前还在场上"的指针集合，再把登记表里不在集合内的条目移除。比"先清后扫"更安全 —— 扫描失败只保留旧条目，不会凭空丢机器
BuildMachineRegistryNow 改调 RebuildAndPruneRegistry	自检/手动重建路径同步获得差集清理
RepairMachinesAfterLoad 改调 RebuildAndPruneRegistry	读档后重建白名单时清掉作废指针（原先只 Register 不 Remove，死指针永驻）
Patch_Target 加点击级冷复核	Target 只在玩家真的点击时触发（非热路径），可安全进 IL2CPP。若 self 指针在白名单但 tag/identifier 对不上（疑似地址复用），注销死指针 + 放行原版，避免放大镜/水桶误开编辑窗。May/Can 热路径保持纯指针查表（零 IL2CPP），不加任何深校验
版本号 1.4.13 → 1.4.14	csproj Version + SuiteVersion 同步
V1.4.13（2026-10-05）· cfg 热更新 + F6 直领 + 物品池注册
改动	说明
cfg 热更新	InitPreferences() 给 4 个 entry 挂 OnEntryValueChanged.Subscribe，面板改值立刻同步到 Config.xxx 静态字段
F6 直领	cfg 新增 EnableF6Give（默认关）。OnUpdate 首行 if (!Config.EnableF6Give) return; —— 关着时每帧只读一个静态 bool
物品池注册	新增 RealPrinterMod.Registration.cs：钩 ModItemDirectory.InitDirectory Postfix，把 real_printer Add 进 Directory<GameItem>。工厂返回标准版 100（不是 F6 的 0）
CreateRealPrinter 签名语义	valueOverride 默认从 0 改为 -1。< 0 用 Config.BaseMachineValue；>= 0 显式使用（0 = F6 防刷钱版 / 100 = 供货商售价版）
cfg 项数 3 → 4	EnableF6Give 加回面板
V1.4.12（2026-10-05）· 缩图方案重做
改动	说明
缩图改用 ppu 缩放（方案 D-lite）	旧方案"读源像素 → 双线性/最近邻重采样 → 建新纹理"32→16/48→32 不是整数倍，无论怎么采样都损质量（实机"有点糊"）。新方案复用原纹理，只改 pixelsPerUnit —— 零像素操作、零额外显存
PpuScale 取纵向匹配（= SrcCellH/TargetCellH = 1.5）	物理限制：2×3 → 1×2 是各向异性缩放，ppu 是各向同性的，只能选一方向。横向匹配（×2）实机结果 = 16×24 px = 1×1.5 格，整体偏小；纵向匹配 = 21.3×32 px ≈ 1.33×2 格，纵向填满、横向溢 1/3。核桃实机确认可接受
旧方案整文件重写	删掉 Rescale / ReadPixelsSafe / _retryAfter / RetryDelay / Blit 绕道 / 最近邻/双线性分支 —— 约 200 行 → 约 130 行
ShapeShrink 保持不变	形状仍压到 1×2 —— 这是 D-lite 的前提
V1.4.11（2026-10-04）· 性能优化 + 诊断清理
核桃要求：「把多加的诊断代码和日志输出优化掉，能删就删，能简化就简化，尽可能减少性能消耗。」

改动	省下的东西
删 RealPrinterMod.Diagnostic.cs（682 行）	两个常驻 Harmony 探针永不卸载；探针为拿柜台状态要调 MayHaveValidInventorySlot / TryFindOneValidInventorySlot / CheckShapePlacement（带逐件忽略重试，最多 N×M 次）。诊断是一次性的，留着就是永久税
ShrinkShape 抢救到新文件	唯一需要的正式功能，从 682 行剥出来成为 ShapeShrink.cs（78 行）
删 OnUpdate + PollHotkeys + WasPressedNow	★ 本轮最实的一处：F6 停用后 PollHotkeys 什么都不做，却让 mod 每帧多跑一次 PlayerStore.Instance 的 IL2CPP 属性读
删 Patch_EndDrag 空补丁	三重负收益：① 每次拖动都被调一次；② 它是 private 无参方法，定位器口径不同 → 自检长期误报"未挂上"（误报比不报更坏）；③ 停用状态该由注释证明，不该由常驻补丁证明
交互自检改为只报失败项	原来无论成败都 new StringBuilder + 每目标一行，启动固定刷 4 行
删 56 行档位明细 + 15 行类别明细	一次性调研产物，每次启动占日志一半篇幅，把真报错挤下去
删 4 个死字段 + 1 个死函数	ConditionInfo.ModValue/ModValueDisplay/CompareValue/ActionId 全无读取方 → 56×4 = 224 次原生调用换四个没人读的 int
★ 修"日志开关形同虚设"	RLog.Detail("…" + SafeId(x)) 里 SafeId(x) 是实参，开关在函数里面，求值在函数外面。修了 5 处
HotLog.Log() 首行加关门判断	挂在 MayTarget/CanTarget 接管分支上，每秒被调 900+ 次。99% 的调用白拿一次锁只为发现"哦，不该打"
删 KeyName() / 重复 SetName() / 重复 Detail	死代码或冗余调用
修供货商补丁自检的错误引导文案	原来提示"改用 F6 直领"——但 F6 从 V1.4.6 起就关着且 cfg 无开关，指了一条死路。改为"回退游戏版本或改存档"
删 OpenEditor 的 RejectSilent 日志	拖动时被高频触发
保留没动的（都是防线，不是浪费）：

PointerSafety 的三道裸指针预检 + LooksLikeHeapPtr —— AV 不可捕获，只能事前拦

IsOurMachine 的纯 ConcurrentDictionary 查表 —— 热路径零 IL2CPP，已是最优

RLog.Swallowed 的"第一次 + 每 100 次"限流 —— 让静默失效变可见，且不刷屏

TryGiveToWarehouse 的回读断言 + 失败即注销 —— 游戏静默销毁物品是本项目最大的坑

V1.4.7 ~ V1.4.10（2026-10-04）· 柜台摆货问题
改动	说明
V1.4.7 形状压缩 2×3 → 1×2	★ 根因实机查明：柜台 18×4=72 格，失败那次货占 69 格只剩 3 格，而机器原形状 2×3=6 格 → TryAcceptAllOrDestroy 静默销毁（不抛异常）。压到 1×2=2 格后实机确认"回来了，稳定"
V1.4.7 网格预览修复	GridShapeBuilder.ctor(w,h) 只分配不填数据 → 必须 SetDataFill((byte)1)，否则 UI 不画占位网格
V1.4.9 精灵缩放 2×3 → 1×2	形状压了但图还是原大小。新增 SpriteRescale.cs，钩 GameItemElement.ResolveSpriteByName 的 Postfix
V1.4.9 修 V1.4.8 的永久锁	我自己写的 _tried 永久锁 + 启动预热：预热那一刻图集还没加载完 → 拿不到原图 → _tried=true 永久生效 → 图永远不缩。改为源图直接从 __result 拿 + 失败后冷却 3 秒可重试
V1.4.10 修缩图模糊	尺寸对了但画面糊。两层插值叠加：新纹理写死 Bilinear + 双线性重采样（这是照片的做法）。像素游戏必须 Point 最近邻
V1.4.6（2026-10-04）
改动	说明
cfg 精简 6 → 3 项	只留 FeatureCost / VerboseLog / SupplierStock
修 _cfg 未实例化崩溃	原来 _cfg = null 且 InitPreferences 里没 new，导致所有 CreateEntry 全没跑 → cfg 段为空
F6 直领完全停用	PollHotkeys 不再响应 F6，连提示也不弹
提示价格改读 GetNegociatedValue()	原来是 unitValue（基础值，不随档位变）→ 化学品/香烟/金属锭改完显示"36 → 36 ★没变"是假消息
删掉候选档位「改后 +N」	modValueDisplay 在不同 category 上填充规则不一致 → 显示"改后 +0"在撒谎
编辑窗字号 11 → 11.5	整体放大半个字号
信用点前加"目前余额"	原来是"340 点"玩家不知道是余额还是费用
建机 tooltip 明写「拖动无效」	短描述 + 长描述都加
MelonInfo 版本号同步	从硬编码 "1.4.3" 改为 SuiteVersion 常量
删 _attachCheckedAt / _nextHotkeyTime	F6 停用后的遗留死字段
V1.4.5 及更早
版本	关键改动
V1.4.5	整体删除外观染色功能 —— 删除 RealPrinterMod.Appearance.cs（580 行）、F3/F4 热键、外观配置项、Patch_ResolveSprite。实机证实旧存档加载后立刻按 F4 会崩在 MonoMod JIT 钩子。机器沿用原版红机外观。面板条目 9 → 6
V1.4.4	删 F4 手动刷新外观热键（RefreshSkinHotkey）
V1.4.3	删三个无实际用途的测试项：MaxUpgradeLevel / AllowRiskyTiers / CleanupStaleHotkey。BaseMachineValue 固定 50
V1.4.1	注册完立刻写盘（MelonPreferences.Save()）+ cfg 完整性自检
V1.4.0	V1.3.0 的「同行带货」已停用 —— 实机证实往共享客户柜台塞物品会污染剧情 NPC 的商品池并导致崩溃。改为供货商上货：挂在 StoreClientList.PlaceSupplierInventory() 的 Postfix 上
V1.3.1	摆货失败时注销白名单 —— 游戏静默销毁物品后悬空指针会引发 AV
V1.2.0	大拆分：Machine.cs（870 行）→ PointerSafety / MachineRegistry / MachineBuilder；FeatureEngine.cs（773 行）→ ConditionCatalog / FeatureDetector / FeatureWriter。纯搬运、零逻辑改动
V1.0.6	拖动期崩溃根治：DisplayNameOf 只允许读裸字段（不调 IL2CPP）
V1.0.5	横幅精简；日志默认安静
V1.0.4	★★★ 关键修正：改完档位必须调 ExecuteOnActivateAction(item) —— 只贴标签不执行动作 = 游戏里的实际状态根本没变
V1.0.2	玩家提示从"弹窗"改为轻提示 StoreUIManager.Notify
V1.0.0	首次发布（曾被 MelonGame 公司名写错导致整 mod 静默不加载）
代码结构（V1.4.13：约 4900 行 / 16 个文件）
文件	职责
RealPrinterMod.cs	Mod 入口、生命周期、启动横幅与自检、OnUpdate（F6 轮询入口）
RealPrinterMod.Core.cs	配置（含 cfg 热更新）、日志、扣费、许可判定、玩家提示
RealPrinterMod.Runtime.cs	读档重挂、购买回执、F6 直领（GiveOneManually / PollHotkeys）、TryGiveToWarehouse / TryPlace
RealPrinterMod.Supplier.cs	供货商上货（PlaceSupplierInventory Postfix）
RealPrinterMod.DealerSupply.cs	购买回执补丁 + 柜台工具函数
RealPrinterMod.Editor.cs	编辑窗（只有真值区块）+ 开窗准入判定
RealPrinterMod.Interact.cs	Harmony 交互补丁（MayTarget / CanTarget / Target）
RealPrinterMod.PointerSafety.cs	裸指针闸门、偏移自检、SafeId
RealPrinterMod.MachineRegistry.cs	指针白名单、存档扫描
RealPrinterMod.MachineBuilder.cs	建机、交互判据、OnTargetItemFunc
RealPrinterMod.ConditionCatalog.cs	官方档位目录（56 工厂）+ 中文名
RealPrinterMod.SpriteRescale.cs	精灵缩放（复用纹理 + 改 ppu）
RealPrinterMod.ShapeShrink.cs	形状压缩（2×3 → 1×2 格）
RealPrinterMod.FeatureDetector.cs	FeatureRow / DetectFeatures / 候选档位
RealPrinterMod.FeatureWriter.cs	真值写入引擎（三层断言）
RealPrinterMod.Registration.cs	★ V1.4.13：物品池注册（ModItemDirectory.InitDirectory Postfix）
V1.2.0 拆分（起因：代码评审 P0 —— 单文件 660+ 行）：

Machine.cs（870 行）→ PointerSafety / MachineRegistry / MachineBuilder

FeatureEngine.cs（773 行）→ ConditionCatalog / FeatureDetector / FeatureWriter

纯搬运、零逻辑改动，拆完做了成员名集合比对：0 丢失。
（回滚点：../RealPrinter_V1.1.11_前.bak/。）

移植自 BlackPrinter 时不动的部分：
PointerSafety / MachineRegistry / MachineBuilder / Interact / ConditionCatalog /
FeatureWriter 里的指针白名单、读档重挂、写入回读断言 —— 这些是踩过崩溃坑换来的，
任何"顺手优化"都可能踩回去。

关键实现要点（改代码前必读）
1. 档位数据必须来自游戏官方工厂
全部来自 ItemConditionList.Create*（共 56 个）。启动时全跑一遍建索引，
候选列表 = 官方 identifier，绝不手写档位名。

（BlackPrinter 曾按枚举名猜出 perfect_purity 等一批游戏不存在的值，
编译期不报，实机只表现为"价格不动 + 显示英文"。）

2. ★★★ 改完档位必须调 ExecuteOnActivateAction(item)
V1.0.4 实机最贵的一条。ItemCondition 三个方法分工明确：

SetConditions(fake, real) —— 只是贴标签

HasOnActivateAction() —— 这个档位带不带后续动作

ExecuteOnActivateAction(item) —— 真正改商品的那一步

赝品/正品的 onActivateActionId 分别是 counterfeitCigarette / genuineCigarette，
注册在 ItemConditionActionList.ItemConditionActionDict。
只贴标签不执行动作 = 游戏里的实际状态根本没变 → 售价纹丝不动。

3. 「标签写对了」≠「游戏状态变了」—— 断言要分两层
标签层：identifier / display / modValue / compareValue 与官方对象一致

商品层：动作执行后回读 GameItem.GetNegociatedValue()（玩家看到的最终价，
不是 unitValue —— 后者是基础值，通常不随档位变）

★ V1.4.6 修正：之前提示读 unitValue，导致化学品/香烟/金属锭改完显示
"36 → 36 ★没变"（实际游戏里已变），是一句假消息。

4. 热路径只比指针
IsOurMachine 只查 ConcurrentDictionary<long, byte>，一个字节内存都不读
—— V0.4.7 实机证明热路径碰 IL2CPP 字符串会猝死。

5. 读档必须重挂指针白名单
读档后指针全变，不重建就"点了没反应"（静默失效）。

6. 三个关闭途径都要清会话
标题栏 / ESC / 自己的按钮，靠 win.onClose 兜住，
否则状态永久残留、必须重启才恢复。

7. 许可闸门在 OpenEditor 最前面
不在四类内根本不开窗，只给提示。

8. ★ 拖动是主动短路的，不是 bug
OnTargetItemFunc 第一行 if (IsDraggingNow()) return; 是 V1.1.9
为防 AccessViolation 而加的，任何"顺手删掉这行"的改动都会让拖动崩溃回归
（V1.1.9 实机堆栈已留档）。

9. 日志开关的实参求值陷阱
RLog.Detail("…" + SafeId(x)) 里 SafeId(x) 是实参，
C# 会在进入函数之前就把它求值完。开关在函数里面，求值在函数外面。

→ 正确写法：判据搬到调用点

csharp
if (Config.VerboseLog) RLog.Detail("…" + SafeId(x));
新加日志一律把判据写在调用点。

10. F6 直领的每帧开销
OnUpdate 首行 if (!Config.EnableF6Give) return; —— 关着时每帧只读一个静态 bool。
任何在 EnableF6Give 判据之前加代码的改动都会让"关着时零开销"失效。

排障（开发视角）
看日志开头 [真印]。启动横幅会打：许可类别、费用、官方档位自检结果、
Harmony 是否挂上、供货商上货补丁是否挂上、物品池注册补丁是否挂上、F6 开关状态。

症状	先看
mod 完全没反应	cfg 里没有 [RealPrinter] 段 → mod 没被加载，去查 MelonGame
左键点物品没反应	横幅里「Harmony 补丁已挂」；报错行
拖动到商品上没反应	正常行为（V1.1.9 起主动短路）；改用法：先使用/装备，再左键点商品
供货商来了但货里没打印机	日志搜 [供货商]。横幅里应有「供货商上货补丁已挂 ✓」—— 没有这行就是补丁没挂上（看报错）。若打「回读前柜台没找到」= 柜台满被游戏销毁，等下次补货
机器摆上了但点不动「买」	柜台 18×4=72 格。买/卖共用 backInvinvElement 判定，纯收购型客户在场时空柜台会挡住购买。先确认有同类客户在场，再勾 VerboseLog 看详情
机器图比格子大	横幅「机器外观：」那行。显示"未生成"= 缩图失败，3 秒后自动重试；摆货与交互完全不受影响
F6 按了没反应	① cfg 里 EnableF6Give 是关的（正常）；② 若已开，看横幅「F6 直领：开」+ 日志有无领取记录
窗口空白	勾 VerboseLog → 看「编辑窗：… 原始 feature N 条」，N=0 说明这物品没真值特性
改了没生效	勾 VerboseLog → 看「已执行档位动作」+「商品侧回读 最终 GetNegociatedValue=」
提示里"价值 X → X ★没变"	说明真没改（V1.4.6 起提示读的是最终价，不再有假消息）；勾 VerboseLog 看回读断言
读档后失效	勾 VerboseLog → 看「读档检查完成」的指针白名单条数（应 ≥1）
拖动机器会闪退	确认部署的是 v1.1.9+（含 IsDraggingNow 拖动短路）
启动报「cfg 挂载异常」	_cfg 未初始化（V1.4.6 已修，见 Core.cs）
启动报「cfg 条目不完整」	旧 cfg 残留或被面板重写过。删掉 UserData/MelonPreferences.cfg 的 [RealPrinter] 段后重启
⚠ 判断"某段代码跑没跑"之前，先确认它是不是挂在 VerboseLog 后面 ——
默认静默的日志很容易被误读成"没执行"（开发这个 mod 时因此误判过两次）。

★ 反向也成立：RLog.Detail("…" + 某个 IL2CPP 调用) 里的实参
在 VerboseLog 关着时照样求值 —— 开关在函数里面，实参求值在函数外面。
所以"日志没打"不代表"那段代码没跑"。

部署前必跑自检
bash
python tools/check_mod_identity.py .   # MelonGame 是否与游戏一致
为什么必须跑：MelonGame("公司名", "游戏名") 里公司名写错的后果
不是报错，而是整个 mod 被 MelonLoader 静默拒绝加载 —— 没有日志、
没有异常、功能一点都没有、cfg 段也不出现。

V1.0.0 就栽在这（公司名串成了别的游戏的名），核桃按 F6 完全没反应。

权威值直接从游戏自己的 Probably Stolen_Data/app.info 读，不靠记忆：
Questing Goose Studio / Probably Stolen。

排障第一招：UserData/MelonPreferences.cfg 里没有本 mod 的段
→ 说明 mod 根本没被加载，去查 MelonGame，别去查功能代码。

与 BlackPrinter 的关系
两个 mod 可以同时装：

identifier：real_printer vs black_printer

专属 tag：REAL_PRINTER_TAG vs BLACK_PRINTER_TAG

★ V1.4.5 起两者外观一致（都是原版红色标签打印机）——
同装两个 mod 时靠 tooltip 名字区分

两边错开，互不把对方的机器认成自己的。

RealPrinter 砍掉的东西（都在 BlackPrinter 里还留着）：
92 条 tag 白名单、343 条黑名单、1169 行汉化词典、恢复链、裸指针开关、
派生值监视、筛选档位、F1~F12 共 10 个排障热键。

待验 / 已知风险
供货商来访频率未知 —— 若隔太多天来一次，获取会太慢，到时再加辅助途径。

PlaceSupplierInventory 是否只在供货商补货时调用（而不是新开商店也调）未确认
—— 若新开商店也调，机器会出现得比预期频繁（cfg 关 SupplierStock 可退）。

老存档里已存在的机器 tooltip 文案不会更新（CreateRealPrinter 只在建机时执行一次），
新领/新买的机器才会带新文案。

老存档里已存在的机器形状仍是 2×3（ShrinkShape 只在建机时跑一次），
图也仍是原大图。要让老存档里的机器也变小，需要重新买一台。
（缩图补丁本身对老存档有效 —— 它钩的是元素解析，判定靠 identifier + spritePath，
不依赖建机时刻；但形状压不了，格子与图会继续对不上。）

Sprite.Create 出来的 Sprite 未设 HideFlags —— 若图集被卸载可能短暂丢图；
目前实机未见。

横向溢出 1/3 格 —— 图比格子宽一点。若将来游戏 UI 层级变动导致可改 localScale，
可考虑非等比缩放彻底解决。