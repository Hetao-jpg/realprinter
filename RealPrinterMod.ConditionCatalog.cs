// ═══════════════════════════════════════════════════════════
// ★ 官方档位目录 —— 「游戏到底有哪些档位、各值多少」的唯一来源
//
// 【本文件管什么】
//   首次用到时（**懒建**，V1.5.2 起不再在启动期建表）把 56 个
//   `ItemConditionList.Create*` 全跑一遍，建成
//   「档位 id → 官方对象信息」与「category → 档位列表」两张索引。
//   候选列表、写入、加价、中文名**全部**来自这两张表。
//
// 【其他部分在哪】（V1.2.0 从 FeatureEngine.cs 拆出，逻辑逐字未改）
//   · FeatureRow / DetectFeatures → RealPrinterMod.FeatureDetector.cs
//   · WriteFeature（真值写入）    → RealPrinterMod.FeatureWriter.cs
//   · 编辑窗                      → RealPrinterMod.Editor.cs
//
// ═══════════════════════════════════════════════════════════
// ★★★★★★ V0.5.19：改用**游戏官方的档位工厂方法**，不再手工 clone ItemCondition
//
// 【核桃两个实机问题的根因】
//   问题1「化学品的品质价格没有同步」：
//     我 V0.5.8 起的 `ModValueFor()` 是**我自己按档位名猜加价**的一张表，
//     而且 `default: return 0` —— 也就是说**所有纯度档位都返回 0**。
//     改 `street_standard` → `perfect_purity` 后，档位名变了，
//     但 modValue/compareValue 全被我硬写成 0 → **售价当然不动**。
//     而游戏自己每个档位都带正确的加价（street_standard 该加多少就是多少）。
//   问题2「真值价值的标签是英语，没有翻译」：
//     `FindConditionTemplate()`我写了个**永远 return null 的空实现**
//     （拿不到父物品就放弃），于是 `CloneCondition()` 里
//     `c.display = newId` —— **直接把英文档位名当显示名塞进去了**。
//     游戏本来有本地化的中文 display（「街头标准」「实验室纯品」…），
//     我亲手把它覆盖成了英文。截图里 tooltip 显示 `perfect_purity (0%)` 就是这个。
//
// 【正解（V0.5.19 取证得出）】
//   //   dump.cs TypeDefIndex 996 `ItemConditionList`：**56 个官方静态工厂方法**，
//   覆盖了全部档位：
//     CreateStreetStandard / CreateLabPure / CreatePharmaGrade / CreateNearPharma /
//     CreateJunkGrade / CreateStarchGrade / CreateCutProduct（化学 7 档全齐）
//     CreateGenuineCigarette / CreateCounterfeitCigarette / CreateGenuineInjector /
//     CreateCounterfeitInjector / CreateExpiredInjector / CreateUnusableInjector（验真/注射器）
//     Create{Perfect,VeryHigh,High,Fair,Low,VeryLow}QualityIngot（金属锭 6 档）
//     Create{Pure,HighQuality,Base,Ghost,Rust}Water / CreateGutterflow（水 6 档）
//     Create{MasterVintage,Reserve,ProductionWine,Homebrew,BucketBrew,Undrinkable}（酒 6 档）
//     CreateEquipementCondition{New,Excellent,Good,Fair,Poor}（装备 5 档）
//     CreateFood{VeryFresh,Fresh,Stale,Spoiled} / Create{Expired,NonExpired}（新鲜度）
//     以及 Dented / Rusted / OpenBox / FactorySealed / Replica / ModuleNormal…
//   → **直接调它**：display 本地化中文、modValue/compareValue 加价、
//     onActivateActionId 全部是游戏自己配好的，一个字都不用我猜。
//
// 【为什么必须用官方对象（不能自己造）】
//   ① display 是**本地化中文**，我造不出来；
//   ② modValue/compareValue 是**每个档位不同的加价**，猜不出来；
//   ③ onActivateActionId 是动作名（`counterfeitCigarette`…），还注册在
//      `ItemConditionActionList.ItemConditionActionDict` 里 —— 自己填 id
//      而没在字典里注册，游戏执行动作时找不到 handler = 静默失效。
//   → **这正是核桃铁律 #1：先借鉴成功案例，别自创。** 我自创了三代。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using MelonLoader;
using Il2Cpp;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// ★★★★★★ V0.5.19：**全库官方档位工厂清单**（dump.cs TypeDefIndex 996 `ItemConditionList`）
    ///
    /// 【为什么列全部 56 个，而不是只列我猜的那几十档】
    ///   我第一版是「档位名 → 工厂」的手写映射（比如把 `perfect_purity` 指向
    ///   `CreateLabPure`）。**这是又一个自创**：工厂返回的对象自带的 identifier
    ///   未必等于我写的键名（比如 CreateLabPure 的 identifier 可能就是 `lab_pure`），
    ///   一旦对不上，写进去的就是一个"键名与对象不匹配"的四不像 = 静默失效。
    ///   → 正解：**只列工厂，不猜键名**。启动时把 56 个工厂全跑一遍，
    ///     读它们**自己说的** identifier / display / category / 加价，
    ///     建成 `官方档位 → ItemCondition` 与 `category → 档位[]` 两张索引。
    ///     于是"候选列表"和"能否写入"都来自同一份官方数据，天然自洽。
    ///
    ///【为什么要跑一遍而不是直接用】
    ///   工厂返回值是**新对象**，每次 new。写档位时必须现调（拿到干净对象），
    ///   不能复用缓存 —— 否则同一个 ItemCondition 实例被挂到多件物品上，
    ///   游戏任何一处改它都会串。
    ///
    /// ★★ 为什么这个数组是**方法**而不是静态字段：
    ///   静态字段初始化器一旦抛异常（TypeInitializationException），
    ///   **整个 RealPrinterMod 类永久不可用** —— 一个可选功能会炸掉全部功能。
    ///   MEMORY 铁律：catch 要剥到最深层，TypeInitializationException 会藏根因。
    ///   → 改成 `AllOfficialFactories()` 方法 + 调用点 try/catch：
    ///     建目录失败只让"真值改写"这一项降级，其余功能照常。
    private static Func<ItemCondition>[] _allFactories;

    private static Func<ItemCondition>[] AllOfficialFactories()
    {
        if (_allFactories != null) return _allFactories;
        // —— 化学纯度 / 化工品质线 ——
        _allFactories = new Func<ItemCondition>[]
        {
            ItemConditionList.CreateLabPure, ItemConditionList.CreatePharmaGrade,
            ItemConditionList.CreateNearPharma, ItemConditionList.CreateStreetStandard,
            ItemConditionList.CreateCutProduct, ItemConditionList.CreateJunkGrade,
            ItemConditionList.CreateStarchGrade,
            // —— 金属锭纯度（6 档）——
            ItemConditionList.CreatePerfectQualityIngot, ItemConditionList.CreateVeryHighQualityIngot,
            ItemConditionList.CreateHighQualityIngot, ItemConditionList.CreateFairQualityIngot,
            ItemConditionList.CreateLowQualityIngot, ItemConditionList.CreateVeryLowQualityIngot,
            // —— 验真：香烟 / 印花票 / 注射器 ——
            ItemConditionList.CreateGenuineCigarette, ItemConditionList.CreateCounterfeitCigarette,
            ItemConditionList.CreateGenuineStamp, ItemConditionList.CreateCounterfeitStamp,
            ItemConditionList.CreateGenuineInjector, ItemConditionList.CreateCounterfeitInjector,
            ItemConditionList.CreateExpiredInjector, ItemConditionList.CreateUnusableInjector,
            // —— 水（6 档）——
            ItemConditionList.CreatePureWater, ItemConditionList.CreateHighQualityWater,
            ItemConditionList.CreateBaseWater, ItemConditionList.CreateGhostWater,
            ItemConditionList.CreateRustWater, ItemConditionList.CreateGutterflow,
            // —— 酒（6 档）——
            ItemConditionList.CreateMasterVintage, ItemConditionList.CreateReserve,
            ItemConditionList.CreateProductionWine, ItemConditionList.CreateHomebrew,
            ItemConditionList.CreateBucketBrew, ItemConditionList.CreateUndrinkable,
            // —— 装备成色（5 档）——
            ItemConditionList.CreateEquipementConditionNew, ItemConditionList.CreateEquipementConditionExcellent,
            ItemConditionList.CreateEquipementConditionGood, ItemConditionList.CreateEquipementConditionFair,
            ItemConditionList.CreateEquipementConditionPoor,
            // —— 新鲜度 / 过期 ——
            ItemConditionList.CreateFoodVeryFresh, ItemConditionList.CreateFoodFresh,
            ItemConditionList.CreateFoodStale, ItemConditionList.CreateFoodSpoiled,
            ItemConditionList.CreateExpired, ItemConditionList.CreateNonExpired,
            // —— 外观 / 状态 ——
            ItemConditionList.CreateDented, ItemConditionList.CreateNonDented,
            ItemConditionList.CreateRusted, ItemConditionList.CreateRustFree,
            ItemConditionList.CreateOpenBox, ItemConditionList.CreateFactorySealed,
            ItemConditionList.CreateReplica, ItemConditionList.CreateNonReplica,
            ItemConditionList.CreateModuleStuck, ItemConditionList.CreateModuleNormal,
            ItemConditionList.CreateFreeNormal,
            // ★ `CreateFreeFree` 是第 56 个（我第一版只列了 55，漏了这个）。
            //   靠 tools/find_factory.py 交叉核对才发现 —— 别再凭记忆数数量。
            ItemConditionList.CreateFreeFree,
        };
        return _allFactories;
    }

    /// <summary>
    /// 官方档位快照：跑工厂时一次性读出来的真实数据（只读，当字典值用）。
    /// ★★ V1.4.11 精简：删掉 `ModValue` / `ModValueDisplay` / `CompareValue` / `ActionId`
    ///   四个字段。
    ///   【为什么能删 —— 它们已经没有任何读取方了】
    ///     · `ActionId`（onActivateActionId）：唯一读它的地方是 FeatureWriter，
    ///       而那里读的是**游戏对象自己的** `condition.onActivateActionId`，
    ///       不走这个快照字段。
    ///     · `ModValueDisplay`：唯一读取方 `OfficialModValueDisplay()` 自己
    ///       也没有调用者（V1.4.6 起 UI 不显示加价，V1.4.6 之后"改后 ±N"也删了）。
    ///     · `ModValue` / `CompareValue`：从来就只有那 56 行明细日志在用，
    ///       明细已删 → 彻底没人读。
    ///   【省下的不只是四个字段】
    ///     建表时每个档位要多读 4 个 IL2CPP 属性（每个都是一次原生调用 + 委托
    ///     包装 + try/catch）→ 56 档 × 4 = **224 次原生调用白跑**，
    ///     换来的是四个没人读的 int。启动一次不多，但这是"零收益的原生调用"，
    ///     与 MEMORY 那条"每帧空转"是同一类浪费，只是量级小。
    ///   ★ 真要用加价，正确做法是**现调工厂**（`info.Make()`）读新对象的字段 ——
    ///     快照本来就只用于"候选列表和写入同源"，不该兼职数值仓库。
    /// </summary>
    private sealed class ConditionInfo
    {
        public string Id;
        public string Display;
        public string Category;
        /// <summary>★ 保留工厂委托：要写的时候**现调**拿干净对象，不复用快照。</summary>
        public Func<ItemCondition> Make;
    }

    private static Dictionary<string, ConditionInfo> _byId;      // identifier → 档位
    private static Dictionary<string, List<ConditionInfo>> _byCat; // category → 档位
    private static bool _catalogTried;
    private static int _catalogFail;

    /// ★建目录失败时**允许有限次重试**（最多 3 次），但成功后绝不再跑。
    ///   【为什么允许重试】首次调用可能发生在游戏静态初始化之前
    ///   （比如 mod 加载早于 Assembly-CSharp 的 .cctor）→ 一次失败就永久
    ///   锁死目录 = 静默失效，正是核桃最恨的那种。
    ///   【为什么限 3 次】无限重试 = 每次开窗刷一遍异常日志，刷屏。
    private static void AllowCatalogRetry()
    {
        _catalogFail++;
        if (_catalogFail>= 3) return;          // 用尽次数，保持失败状态
        _catalogTried = false;                 // 允许下次调用再试
    }

    // ★★ V1.5.2：原 `BuildConditionCatalogOnce()`（显式启动期建表）已删除 ——
    //   它让本 mod 在启动期抢在游戏自己之前触碰 `ItemConditionList`，是
    //   2026-10-16 事故的元凶（详见 RealPrinterMod.cs 的 OnInitializeMelon 顶部）。
    //   现在目录**只走懒建**：许可判定 / 开窗 / 写入时经由 `EnsureCatalog` 现建，
    //   此时游戏早已自己初始化完该类。自检同理，见 `RunPostBuildSelfChecksOnce`。

    /// <summary>官方档位总数（延迟自检用，V1.5.2 起横幅不再读它）。</summary>
    internal static int ConditionCatalogCount()
    {
        EnsureCatalog();
        return _byId == null ? 0 : _byId.Count;
    }

    /// <summary>
    /// ★ V0.5.19：官方档位自检。**编译期查不出来的错，只能靠这个兜**。
    ///
    /// 【查什么】
    ///   ① 目录是否建起来（_byId 非空）—— 全空 = 真值改写整体不可用；
    ///   ② 每个档位必须有**非空中文 display**（空 = 写进去 tooltip 会空白）；
    ///   ③ `CategoryValues` 里所有**手写候选数组**的元素
    ///      必须能在官方目录里找到 —— 这条直接堵死 V0.5.18 那个
    ///      "手写 perfect_purity 但游戏叫 lab_pure"的坑。
    /// </summary>
    internal static List<string> SelfCheckConditionCatalog()
    {
        var bad = new List<string>();
        EnsureCatalog();
        if (_byId == null || _byId.Count == 0)
        {
            bad.Add("官方档位目录为空 —— 56 个 Create* 全失败，真值改写不可用。");
            return bad;
        }

        foreach (var kv in _byId)
        {
            var ci = kv.Value;
            if (string.IsNullOrEmpty(ci.Display))
                bad.Add("档位 " + ci.Id + " 的 display 为空（写进去界面会空白）。");
        }

        // ③ 每个 category 的候选**必须都能解析成官方对象**——
        //   这条直接堵死"选得到但写不进去"（V0.5.18 的静默失效形态）。
        //   ★ 不再检查 CategoryValues 里的手写数组：V0.5.19 起它们已不被引用
        //     （那些名字游戏根本不存在），留着当历史对照，检查它们只会误报。
        foreach (var kv in _byCat)
        {
            foreach (var ci in kv.Value)
            {
                var probe = ResolveOfficialCondition(ci.Id);
                if (probe == null)
                    bad.Add("★ category " + kv.Key + " 的候选「" + ci.Id
                            + "」解析不出官方对象 → 玩家能选但写不进去 = 静默失效。");
                else if (!string.Equals(SafeStr(() => probe.identifier), ci.Id, StringComparison.OrdinalIgnoreCase))
                    bad.Add("★ 档位 " + ci.Id + " 工厂返回的 identifier 是「"
                            + SafeStr(() => probe.identifier) + "」—— 键名与对象不一致。");
            }
        }
        return bad;
    }

    private static void EnsureCatalog()
    {
        if (_catalogTried) return;
        _catalogTried = true;   // ★ 先置位：即使中途抛异常也不反复重试（防每次开窗都刷异常）
        try
        {
            _byId = new Dictionary<string, ConditionInfo>(StringComparer.OrdinalIgnoreCase);
            _byCat = new Dictionary<string, List<ConditionInfo>>(StringComparer.OrdinalIgnoreCase);

            // ★ 工厂数组构造本身也可能抛（方法签名变动/ 找不到 token）→ 包在 try 里
            Func<ItemCondition>[] factories;
            try { factories = AllOfficialFactories(); }
            catch (Exception fe)
            {
                RLog.Error("★ 官方工厂清单构建失败（真值改写降级，其余功能正常）: " + fe);
                _byId = null; _byCat = null;
                AllowCatalogRetry();
                return;
            }
            if (factories == null) { _byId = null; _byCat = null; AllowCatalogRetry(); return; }

            foreach (var f in factories)
            {
                if (f == null) continue;
                ItemCondition c = null;
                try { c = f(); } catch { continue; }
                if (c == null) continue;

                string id = SafeStr(() => c.identifier);
                string disp = SafeStr(() => c.display);
                string cat = SafeStr(() => c.category);
                if (string.IsNullOrEmpty(id)) continue;

                var info = new ConditionInfo
                {
                    Id = id,
                    Display = disp,
                    Category = cat,
                    Make = f,
                };
                // ★ V1.4.11：原本这里还读 modValue / modValueDisplay / compareValue /
                //   onActivateActionId 四个属性（各一次 IL2CPP 调用 + 委托 + try/catch），
                //   填进快照后**一个读取方都没有** → 连字段带读取一起删了。
                //   详见 ConditionInfo 的类注释。

                // 目录建表一次，重复 id 覆盖即可（原逻辑"先到先留"语义未变，
                // 因为 56 个工厂的 identifier 不会重复）。
                _byId[id] = info;
                if (!string.IsNullOrEmpty(cat))
                {
                    List<ConditionInfo> lst;
                    if (!_byCat.TryGetValue(cat, out lst)) { lst = new List<ConditionInfo>(); _byCat[cat] = lst; }
                    lst.Add(info);
                }

                // ★★ V1.4.11：**逐档明细整段删除**（原来 56 行 `官方档位: id=… display=…`）。
                //   【为什么删 —— 它是一次性取证产物，不是诊断手段】
                //     写它的时候（V0.5.19）我需要核对这个游戏到底有哪些官方档位、
                //     各值多少 —— 那是一次性的调研需求，答案已经写进代码里的
                //     56 个 `Create*()` 工厂引用了，**再打一遍没有任何信息量**。
                //   【为什么它当初是 Detail 也仍然刷屏】
                //     它在 `EnsureCatalog` 的**建表循环里**，而建表当时是启动必跑的一次
                //     （V1.5.2 起改为懒建，但"每次用到前只建一次"的性质不变）。
                //     56 行 × 每次启动 = 日志里长期占一半篇幅，把真正的报错挤下去。
                //   【自检已经覆盖了它该发现的问题】
                //     `SelfCheckConditionCatalog` 逐档验「display 非空」+
                //     「category 的每个候选都能解析成官方对象」+「identifier 与键名一致」，
                //     出问题会**指名道姓**报出来（"档位 X 的 display 为空"）——
                //     比 56 行全量清单更容易定位。
                //   ★ 要重新做这份调研时，正确做法是临时加一行 Detail，
                //     **别把常驻日志当成调研窗口**。
            }

            _catalogFail = 0;   // ★ 成功即锁定，绝不再重建（避免重复刷日志）
            // ★ V1.4.11：类别 → 档位名清单的 15 行明细也删了（同一理由：那是一次性核对用的）。
            //   ★ 保留**计数**（几档 / 几类）—— 那是"目录建起来了没有"的最短证明，
            //     一行就能让"改了没反应"的排障从 10 秒变成 10 秒。
            RLog.Msg("官方档位目录就绪：" + _byId.Count + " 档 / " + _byCat.Count + " 类");

            // ★★ V1.5.2：延迟自检 —— 目录懒建成功后只跑一次（见方法注释）。
            RunPostBuildSelfChecksOnce();
        }
        catch (Exception e)
        {
            RLog.Error("★ 官方档位目录建立失败: " + e.Message);
            _byId = null; _byCat = null;
            AllowCatalogRetry();
        }
    }

    /// ★★ V1.5.2：延迟自检 —— 目录**懒建成功后**只跑一次。
    ///
    /// 【为什么必须搬到这里（2026-10-16 事故实锤）】
    ///   这两段自检原来在 `OnInitializeMelon` 里跑（启动期），会成为全场第一个
    ///   触碰游戏 `ItemConditionList` 的代码 —— 该类 .cctor 内部要取本地化文本
    ///   （LocHelper.Get → Addressables），启动早期本地化未就绪 → cctor 抛
    ///   `invalid operation handle` 且**构造失败被永久缓存**（同 .NET 语义，
    ///   TypeInitializationException）→ 本地化 / 物品系统连锁瘫痪：
    ///   主菜单变英文、读档失败。搬到懒建路径后，首跑时机 = 玩家进游戏后第一次
    ///   触发许可/开窗，那时游戏早已自己初始化完该类 —— 本 mod 不可能再当"第一个"。
    ///
    /// 【功能不减】两条自检的日志内容/分级一字未改（①官方档位自检
    ///   ②许可类别自检 functional→Error / cosmetic→Warning），只是时间点
    ///   从"启动日志末尾"挪到"目录首次就绪之后"。
    ///
    /// 【为什么挂在 EnsureCatalog 而不是各调用点】全部懒建都汇聚到 EnsureCatalog
    ///   的成功路径 —— 挂这里一次覆盖所有入口（AllowedSet / 开窗 / 写入），
    ///   不依赖调用方自觉。
    private static bool _postBuildChecked;

    private static void RunPostBuildSelfChecksOnce()
    {
        if (_postBuildChecked) return;
        _postBuildChecked = true;   // ★ 先置位：自检自身异常也不重复跑（防刷屏）

        // ① 官方档位目录自检（原 OnInitializeMelon 第一块，逐字沿用）
        try
        {
            var bad = SelfCheckConditionCatalog();
            if (bad.Count > 0)
                RLog.Error("★★ 官方档位自检发现 " + bad.Count + " 处问题（改真值可能不可用）：\n"
                          + string.Join("\n", bad.ConvertAll(x => "    · " + x)));
            else
                RLog.Detail("官方档位自检通过（" + ConditionCatalogCount() + " 档全部可解析）。");
        }
        catch (Exception e) { RLog.Error("★★ 官方档位自检异常: " + e.Message); }

        // ② 许可类别自检（原 OnInitializeMelon 第二块，逐字沿用；
        //    分级判据的完整来龙去脉见 License.SelfCheckLicense 的文档注释）
        try
        {
            License.SelfCheckLicense(out var funcBad, out var cosBad);
            if (funcBad.Count > 0)
                RLog.Error("★★ 许可类别自检发现 " + funcBad.Count + " 处功能问题"
                           + "（这些类别开不了窗）：\n"
                           + string.Join("\n", funcBad.ConvertAll(x => "    · " + x)));
            if (cosBad.Count > 0)
                RLog.Warning("⚠ 许可显示降级（不影响功能）：\n"
                           + string.Join("\n", cosBad.ConvertAll(x => "    · " + x)));
            if (funcBad.Count == 0 && cosBad.Count == 0)
                RLog.Detail("许可类别自检通过（" + License.AllAllowed().Length + " 类均有官方档位）。");
        }
        catch (Exception e) { RLog.Error("★★ 许可类别自检异常: " + e.Message); }
    }

    /// <summary>
    /// ★ V0.5.19：按档位名拿**官方** ItemCondition 对象（现调工厂 → 全新实例）。
    /// 拿不到返回 null（调用方必须明确失败，**禁止降级**成手工半成品）。
    /// </summary>
    private static ItemCondition ResolveOfficialCondition(string newId)
    {
        if (string.IsNullOrEmpty(newId)) return null;
        EnsureCatalog();
        if (_byId == null) return null;

        ConditionInfo info;
        if (!_byId.TryGetValue(newId, out info)) return null;

        try
        {
            // ★ 现调工厂拿**干净的新对象**（不复用缓存实例：挂到多件物品上会串味）
            var c = info.Make();
            if (c == null) return null;

            string disp = SafeStr(() => c.display);
            if (string.IsNullOrEmpty(disp))
            {
                RLog.Warning("★ 官方档位 " + newId + " 的 display 为空 → 弃用");
                return null;
            }
            return c;
        }
        catch (Exception e)
        {
            RLog.Error("★ 官方档位 " + newId + " 构造失败: " + e.Message);
            return null;
        }
    }

    /// ★ V0.5.19：按 category 取该类别的**官方**档位 id 列表（候选按钮的数据源）。
    ///   取不到就返回 null → UI 走"自由输入"兜底，不静默给错候选。
    internal static string[] OfficialIdsForCategory(string cat)
    {
        if (string.IsNullOrEmpty(cat)) return null;
        EnsureCatalog();
        if (_byCat == null) return null;
        List<ConditionInfo> lst;
        if (!_byCat.TryGetValue(cat, out lst) || lst.Count == 0) return null;

        // ★ V1.4.12：改 ConcurrentDictionary —— 无锁读，与 _machinePtrs 风格统一。
        //   字典只增不改（目录建好后内容冻结），GetOrAdd 天然幂等。
        string[] cached;
        if (_idArrayCache.TryGetValue(cat, out cached)) return cached;

        var ids = new string[lst.Count];
        for (int i = 0; i < lst.Count; i++) ids[i] = lst[i].Id;

        // 并发时可能多算一个数组，但 GetOrAdd 保证最终只存一份 —— 无害。
        return _idArrayCache.GetOrAdd(cat, ids);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> _idArrayCache
        = new System.Collections.Concurrent.ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★★ V0.5.19：取某档位的**官方中文显示名**（本地化 display）。
    ///
    /// 【为什么必须用官方的】游戏自己的 tooltip / 商店界面显示的都是这份本地化文本
    ///   （「街头标准」「实验室纯品」…），我自己翻译就是**自创**，会出现两套叫法。
    ///   → 中文名只有一个来源：<c>ItemConditionList.Create*()</c> 的返回值。
    ///
    /// 【为什么要缓存】UI 每次重建窗口要渲染十几个候选 → 十几个工厂调用 = 多余开销。
    ///   只读 display，不需要每次现调工厂。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _displayCache
    = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string OfficialDisplay(string tierId)
    {
        if (string.IsNullOrEmpty(tierId)) return null;
        string hit;
        if (_displayCache.TryGetValue(tierId, out hit)) return hit;

        string disp = null;
        try
        {
            EnsureCatalog();
            if (_byId != null)
            {
                ConditionInfo info;
                if (_byId.TryGetValue(tierId, out info)) disp = info.Display;
            }
        }
        catch (Exception ex) { RLog.Swallowed("OfficialDisplay", ex); }

        // ★ ConcurrentDictionary 不接受 null value —— 查不到就**不缓存**，
        //   每次重查。这是异常路径（id 不在目录里），不缓存也无性能损失。
        if (disp != null) _displayCache.TryAdd(tierId, disp);
        return disp;
    }

    /// <summary>
    /// ★ V1.2.0：取某档位的**官方加价**（`modValueDisplay`）。
    /// ★★ V1.4.11：**已删除**（原来无调用点；它依赖的 `ConditionInfo.ModValueDisplay`
    ///   字段也一并删了 —— 一个没人调的函数 + 一个没人读的字段 = 两层死代码）。
    ///
    /// 【知识留档 —— 将来真要复现"改前预览"时照这段做】
    ///   ① **口径必须与"当前加价"一致**：编辑窗悬停里的「当前加价」读的是
    ///      `ItemCondition.modValueDisplay`（见 Editor.cs）。候选档位的加价
    ///      **必须读同一个字段**，否则界面上会出现两套互相矛盾的数字
    ///      （核桃铁律：不要自创口径）。
    ///   ② **数据必须现调工厂拿**（`ConditionInfo.Make()`），不要在快照里存数值 ——
    ///      快照只用于"候选列表与写入同源"，不兼职数值仓库。
    ///   ③ **取不到返回 `null`，UI 侧不显示这一项**。绝不显示 0 假装"没加价" ——
    ///      0 和"取不到"是两回事，显示 0 会让玩家以为这个档位不加价。
    ///   ④ ★ `modValueDisplay` 在不同 category 上填充规则不一致（这正是
    ///      V1.4.6 删掉"改后 ±N"的原因）。要做预览得先按 category 验证一遍，
    ///      别默认它到处可用。
    /// </summary>
}
