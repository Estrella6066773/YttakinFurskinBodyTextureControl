using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace YttakinFurskinBodyTextureControl
{
    /// <summary>
    /// 尾巴的显示位置微调（按体型分别配置）。
    ///
    /// 联动关系：
    /// 1. 尾巴画在哪，不取决于贴图本身，而取决于基因上的 WithBodyTypeOffset 扩展。
    ///    那套扩展由 Better Gene Graphics Framework（下称 BGF）定义，按体型与朝向分别给出 (前后, 高度) 坐标；
    ///    真正读取它的是 BGF 的 PawnRenderNodeWorker_GeneBodyAttachment.OffsetFor，
    ///    该方法在 PawnRenderTree.ParallelPreDraw 里每帧都会跑一次，所以改完当帧就能看到变化，不必强制重画。
    /// 2. 本类在基因数据装载完成后把「尾巴基因 → 各朝向、各体型的偏移数据」采集下来（见 <see cref="Collect"/>），
    ///    并把作者写下的原始坐标一并留存。之后每次玩家改动滑条，就用「原始坐标 + 玩家增量」重新写回。
    ///    因为 BGF 的算法一行未动，所以左右翻转、按体型缩放这些原有行为全部保持。
    /// 3. 顺带提供纯查询接口 <see cref="EffectiveOffsetCells"/>，供设置界面画预览图使用；
    ///    它只读原始坐标、不加任何已写入的增量，因此预览不会因为反复应用而越算越偏。
    /// 4. 坐标分两层，本模组严格不改这两层的关系：
    ///    下层是「基础位置」，由作者按朝向分别给出，正面、背面、侧面三份数值本来就不相同；
    ///    那是作者针对各自贴图与身体轮廓做的补偿，不是错误，本模组原样保留、不做抹平。
    ///    上层是「玩家指定的那一套」，只有一份，会同时叠加到三个朝向上。
    ///    两层相加才是最终坐标。因为上层只有一份、下层是作者为每个视角配好的基准，
    ///    所以同一个设置在任何视角下都按同一幅度移动，不会出现「换个朝向就各自漂移」的情况，
    ///    玩家也不需要为每个视角分别记一套数值。
    ///    实测本机两个模组共 14 条尾巴，三份朝向容器与八个体型字段全部齐全，
    ///    因此这一套指定值一定落在每一个视角上，不存在某个朝向被漏掉的可能。
    /// 5. 至于每个朝向究竟会画出哪一张贴图，本类也给出判定规则，供设置界面的预览使用
    ///    （见 <see cref="ArtworkPath"/> 与 <see cref="ArtworkMirrored"/>），但只做判断，不改坐标。
    ///    规则同样照抄游戏：实测这批尾巴贴图只有 north、south、east 三种，一张 west 都没有，
    ///    朝西是拿 east 那张水平镜像着画的；另有 8 条尾巴（兔尾、鹿尾、猫尾、恶魔尾、猪尾、鼠尾、狮尾、可爱兔尾）
    ///    的 visibleFacing 里本就不含正面，正面根本不画，所以它们连 south 图都没有。
    ///    这两点若不复刻，预览里朝西那一格会整层空掉，正面那一格也会让人误以为出了偏差。
    ///    也正因为正面不画，那 8 条尾巴给正面写的全零坐标是「配给一张不存在的贴图」的，
    ///    读不到、也用不上，本类不去补它、不去改它。
    ///
    /// 采集对象如何确定（这一条决定了「哪些部件会被改动」）：
    /// 1. 只认 GeneDef：本类遍历 DefDatabase&lt;GeneDef&gt;，别的 Def 类型一概不看。
    ///    这一条就滤掉了「TailoringBench」「Tailor」这类名字里带 Tail 的家具与工作台。
    /// 2. defName 必须含 Tail，且不含 Wing。
    ///    翅膀在本模组环境里以 ERN_AzaphraeWingsFemale、ERN_RhyaethWings 这样的名字出现，不含 Tail；
    ///    这条规则是双保险：万一某个模组把翅膀命名成带 Tail 的样子，也不会被当成尾巴。
    /// 3. 基因上必须真的挂着 BGF 的 WithBodyTypeOffset 扩展。
    ///    这是最关键的一条：没有坐标数据的部件本来就是不可调的，本模组不会替它凭空造一份出来。
    ///    实测（本机 109 个模组）携带该扩展的只有 AFU 的 14 个尾巴基因，翅膀、耳朵、角都没有携带。
    /// 设置界面会把命中的部件逐个列出来，玩家可以直接对照，不必相信本类的判断。
    ///
    /// 需要注意：
    /// 1. 本类刻意不引用 BetterGeneGraphicsFramework.dll，只按类型全名用反射识别它的扩展类。
    ///    这样即使玩家没装 BGF，本模组也能正常启停，不会因为缺少引用而整个加载失败。
    /// 2. 反射只发生在启动与玩家改动设置这两个时刻，渲染热路径上没有任何新增开销。
    /// 3. 自定义只在「停用内置贴图」那一档生效，由调用方通过 Apply 的 active 参数决定；
    ///    另一档下每次调用都会把坐标还原成作者原始值，让尾巴保持作者设定的位置。
    /// 4. 「前后」写的是 x 轴，只写进侧面那一份数据（BGF 的 East）。BGF 画朝西的小人时会把 East 的 x 取反，
    ///    所以同一次改动在朝东与朝西身上方向相反，相对小人的身体却是一致的；
    ///    正北与正南那两份数据的 x 原样不动，符合「前后只影响侧面」的要求。
    /// 5. 原始坐标要正确，前提是采集发生在任何改动之前；因此采集被设计成「第一次应用时」执行一次，
    ///    玩家反复拖动滑条只会在这份原始坐标上叠加，不会越拖越偏。
    /// 6. 采集必须发生在基因数据装载之后。本类的调用方是 [StaticConstructorOnStartup] 的入口类，
    ///    那个时点基因库已经就绪；若在更早的时点调用，本类会识别出基因库尚空并推迟到下次调用，
    ///    不会把「没有尾巴基因」误记成结论。
    /// </summary>
    internal static class TailOffsets
    {
        /// <summary>BGF 里承载坐标的扩展类，按全名匹配，避免引入程序集引用。</summary>
        private const string BgfOffsetExtensionTypeName = "BetterGeneGraphicsFramework.WithBodyTypeOffset";

        /// <summary>判定「这是尾巴基因」的关键词。</summary>
        private const string TailNameKeyword = "Tail";

        /// <summary>
        /// 明确排除的关键词。翅膀在命名上与尾巴不是一回事，这里显式挡掉，
        /// 免得日后某个模组用「TailWing」之类的名字把翅膀混进尾巴。
        /// </summary>
        private static readonly string[] ExcludedNameKeywords = { "Wing" };

        /// <summary>侧面那一份数据的字段名。只有它的 x 会被「前后」改动。</summary>
        private const string SidewaysContainerFieldName = "bodyTypeOffsetEast";

        /// <summary>
        /// 偏移数值表的键分隔符。键写成「尾巴 defName + 分隔符 + 体型 defName」，例如 AFU_BA_FoxTail|Male。
        /// 取竖线是因为 GeneDef 与 BodyTypeDef 的 defName 只允许字母、数字与下划线，不会与它撞车。
        /// </summary>
        private const char KeySeparator = '|';

        /// <summary>BGF 三个朝向的数据容器字段名，与 WithBodyTypeOffset 的字段同名。</summary>
        private static readonly string[] ContainerFieldNames =
        {
            "bodyTypeOffsetNorth", "bodyTypeOffsetSouth", "bodyTypeOffsetEast"
        };

        /// <summary>与 <see cref="ContainerFieldNames"/> 一一对应的朝向。侧面那一份同时服务于朝东与朝西。</summary>
        private static readonly Rot4[] ContainerRots = { Rot4.North, Rot4.South, Rot4.East };

        /// <summary>BGF 按体型分别存放坐标，字段名与 BodyTypeDefOf 及扩展体型一一对应。</summary>
        private static readonly string[] BodyTypeFieldNames =
        {
            "male", "female", "fat", "hulk", "thin", "baby", "child", "femaleBB"
        };

        /// <summary>一份可改写的坐标数据：某个基因、某个朝向、某一批体型字段，以及它们被改动前的原值。</summary>
        private sealed class Slot
        {
            /// <summary>BGF 的 BodyTypeOffset 实例，反射改写的就是它。</summary>
            public object Container;

            /// <summary>所属朝向。侧面那一份同时给朝东与朝西用。</summary>
            public Rot4 Rot;

            /// <summary>是否为侧面那一份。只有侧面才叠加「前后」增量。</summary>
            public bool Sideways;

            public readonly List<FieldInfo> Fields = new List<FieldInfo>();

            /// <summary>与 Fields 一一对应的体型 defName，例如 "Male"、"FemaleBB"。也是设置里的存储键。</summary>
            public readonly List<string> BodyTypeNames = new List<string>();

            /// <summary>与 Fields 一一对应的作者原始坐标；null 表示作者没给这个体型写坐标。</summary>
            public readonly List<Vector3?> Originals = new List<Vector3?>();
        }

        /// <summary>一个尾巴基因采集到的全部内容。</summary>
        private sealed class GeneEntry
        {
            public GeneDef Gene;
            public readonly List<Slot> Slots = new List<Slot>();

            /// <summary>以 Rot4.AsInt 为下标索引槽位；朝西与朝东共用同一份，便于预览查表。</summary>
            public readonly Slot[] ByRot = new Slot[4];
        }

        private static readonly List<GeneEntry> entries = new List<GeneEntry>();

        /// <summary>与 <see cref="entries"/> 顺序一致的基因列表，供设置界面直接用，避免每帧重新拼装。</summary>
        private static readonly List<GeneDef> matchedGenes = new List<GeneDef>();

        /// <summary>可调体型，按 BGF 的字段顺序排列，只保留本局实际存在的体型。</summary>
        private static readonly List<BodyTypeDef> tunableBodyTypes = new List<BodyTypeDef>();

        private static bool collected;

        /// <summary>采集到的尾巴基因。设置界面用它列出部件名并画预览图。</summary>
        internal static IList<GeneDef> MatchedGenes
        {
            get
            {
                Collect();
                return matchedGenes;
            }
        }

        /// <summary>
        /// 可以分别配置的体型。
        /// 取的是「BGF 有对应字段」且「本局存在这个 BodyTypeDef」的交集：
        /// 某些模组会加自己的体型，BGF 里没有它们的字段，改不动，所以不列出来。
        /// </summary>
        internal static IList<BodyTypeDef> TunableBodyTypes
        {
            get
            {
                Collect();
                return tunableBodyTypes;
            }
        }

        /// <summary>
        /// 组装偏移数值表的键：哪一条尾巴、哪一种体型，两两组合成一个键。
        ///
        /// 这是「每条尾巴、每种体型各存一套数值」的落脚点：
        /// 键由两侧共同决定，所以换尾巴或换体型都会读到另一份数据，互不影响。
        /// 设置界面与 <see cref="Apply"/> 都从这里取键，格式只有这一处定义。
        /// </summary>
        internal static string Key(string tailDefName, string bodyTypeDefName)
        {
            return tailDefName + KeySeparator + bodyTypeDefName;
        }

        /// <summary>
        /// 按玩家当前选择重写尾巴坐标。
        /// </summary>
        /// <param name="active">
        /// 自定义是否生效。为 false 时按零增量应用，也就是把坐标还原成作者原始值。
        /// 玩家的两档身体贴图选择会传进来：自定义只在「停用内置贴图」那一档生效。
        /// </param>
        /// <param name="heightOffsets">键为 <see cref="Key"/> 的高度增量。查不到的按 0 处理。</param>
        /// <param name="depthOffsets">键为 <see cref="Key"/> 的前后增量。查不到的按 0 处理。</param>
        internal static void Apply(
            bool active,
            Dictionary<string, float> heightOffsets,
            Dictionary<string, float> depthOffsets)
        {
            Collect();
            for (int i = 0; i < entries.Count; i++)
            {
                GeneEntry entry = entries[i];
                for (int s = 0; s < entry.Slots.Count; s++)
                {
                    Slot slot = entry.Slots[s];
                    for (int j = 0; j < slot.Fields.Count; j++)
                    {
                        string bodyTypeName = slot.BodyTypeNames[j];
                        string key = Key(entry.Gene.defName, bodyTypeName);
                        float height = active ? Lookup(heightOffsets, key) : 0f;
                        float depth = active ? Lookup(depthOffsets, key) : 0f;

                        Vector3? original = slot.Originals[j];
                        Vector3 value = original ?? Vector3.zero;
                        value.z += height;
                        if (slot.Sideways)
                        {
                            value.x += depth;
                        }
                        // 这个字段本来就没值、玩家也没加减时，把字段还原成 null，
                        // 免得凭空多出一份 (0,0,0) 让别人判断「有没有配过」的结果发生变化。
                        bool untouched = original == null && height == 0f && (!slot.Sideways || depth == 0f);
                        slot.Fields[j].SetValue(slot.Container, untouched ? (object)null : (Vector3?)value);
                    }
                }
            }
        }

        /// <summary>
        /// 查某一个基因在某个体型、某个朝向上的坐标。
        ///
        /// 这是给设置界面画预览图用的纯查询：只读实际基准坐标，再加上传进来的增量，
        /// 不去读已经写回基因的数据，因此预览与「应用」互不干扰，反复调用也不会累加。
        /// 朝西与朝东共用侧面那一份数据，这里已按 BGF 的规则把 x 取反。
        ///
        /// 预览只在「停用内置贴图」那一档出现，用的是作者原始坐标，与 <see cref="Apply"/> 的输入一致。
        /// </summary>
        internal static Vector3 EffectiveOffsetCells(
            GeneDef gene,
            BodyTypeDef bodyType,
            Rot4 rot,
            float height,
            float depth)
        {
            Collect();
            GeneEntry entry = FindEntry(gene);
            if (entry == null || bodyType == null)
            {
                return Vector3.zero;
            }
            Slot slot = entry.ByRot[rot.AsInt];
            if (slot == null)
            {
                // 作者没给这个朝向配坐标，BGF 那边也会返回零向量。
                return Vector3.zero;
            }
            int index = slot.BodyTypeNames.IndexOf(bodyType.defName);
            if (index < 0)
            {
                // 这个体型在作者的数据里没有对应字段，BGF 也会退回零向量。
                return Vector3.zero;
            }
            Vector3 value = slot.Originals[index] ?? Vector3.zero;
            value.z += height;
            if (slot.Sideways)
            {
                value.x += depth;
                if (rot == Rot4.West)
                {
                    // BGF 的规则：朝西沿用侧面那一份数据，但把 x 取反。
                    value.x = -value.x;
                }
            }
            return value;
        }

        private static GeneEntry FindEntry(GeneDef gene)
        {
            if (gene == null)
            {
                return null;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Gene == gene)
                {
                    return entries[i];
                }
            }
            return null;
        }

        private static float Lookup(Dictionary<string, float> table, string bodyTypeName)
        {
            if (table == null)
            {
                return 0f;
            }
            float value;
            return table.TryGetValue(bodyTypeName, out value) ? value : 0f;
        }

        /// <summary>某个贴图路径实际存在哪些朝向的图。Directional 是四张方向图的合集。</summary>
        [Flags]
        private enum FacingMask
        {
            None = 0,
            North = 1,
            South = 2,
            East = 4,
            West = 8,

            /// <summary>不带朝向后缀的那张单图。四张方向图都没有时，四个朝向共用它。</summary>
            Single = 16,

            Directional = North | South | East | West
        }

        /// <summary>贴图路径 → 实际存在哪些朝向的图。查一次就缓存，避免每帧重复探测。</summary>
        private static readonly Dictionary<string, FacingMask> textureFacings = new Dictionary<string, FacingMask>();

        /// <summary>
        /// 某个朝向最终画出来的那张贴图属于哪个朝向。
        ///
        /// 规则照抄游戏的 Graphic_Multi.Init：
        /// 缺 north 时按 south、east、west 的次序取一张补位；
        /// 缺 south 时用 north；缺 east 时先看 west 再看 north；缺 west 时用 east。
        /// 四张方向图一张都没有时，四个朝向共用那张单图，朝向不变。
        /// </summary>
        internal static Rot4 ArtworkFacing(string texPath, Rot4 rot)
        {
            FacingMask mask = FacingsOf(texPath);
            if ((mask & FacingMask.Directional) == 0)
            {
                // 没有方向图（含什么都没有的情形），朝向不变。
                return rot;
            }
            if ((mask & FacingMask.North) == 0)
            {
                if ((mask & FacingMask.South) != 0)
                {
                    return Rot4.South;
                }
                return (mask & FacingMask.East) != 0 ? Rot4.East : Rot4.West;
            }
            switch (rot.AsInt)
            {
                case 0:
                    return Rot4.North;
                case 2:
                    return (mask & FacingMask.South) != 0 ? Rot4.South : Rot4.North;
                case 1:
                    return (mask & FacingMask.East) != 0
                        ? Rot4.East
                        : ((mask & FacingMask.West) != 0 ? Rot4.West : Rot4.North);
                default:
                    return (mask & FacingMask.West) != 0
                        ? Rot4.West
                        : ((mask & FacingMask.East) != 0 ? Rot4.East : Rot4.North);
            }
        }

        /// <summary>
        /// 某个朝向画出来的贴图要不要水平翻转。
        /// 只有两种情形：朝西而没有 west 图（用 east 顶替并镜像），
        /// 以及朝东而没有 east 图却拿 west 顶替。其余朝向不翻。
        /// </summary>
        internal static bool ArtworkMirrored(string texPath, Rot4 rot)
        {
            FacingMask mask = FacingsOf(texPath);
            if (rot == Rot4.West)
            {
                // 朝西一律是「借一张侧图再镜像」：借 east，或只有单图时镜像那张单图。
                return (mask & FacingMask.West) == 0
                    && (mask & (FacingMask.East | FacingMask.Single)) != 0;
            }
            if (rot == Rot4.East)
            {
                return (mask & FacingMask.East) == 0 && (mask & FacingMask.West) != 0;
            }
            return false;
        }

        /// <summary>
        /// 某个朝向该去加载哪个文件：实际画的图所属朝向的后缀拼上去。
        /// 返回 null 表示这个路径下一张图都没有，调用方应当跳过这一层。
        ///
        /// 注意：缺 north 时游戏还会把借来的图旋转一个角度（180 或 ±90），
        /// 本方法只负责挑文件，不负责旋转；预览也不做旋转，因为没有实测到这样的尾巴。
        /// </summary>
        internal static string ArtworkPath(string texPath, Rot4 rot)
        {
            FacingMask mask = FacingsOf(texPath);
            if (mask == FacingMask.None)
            {
                return null;
            }
            if ((mask & FacingMask.Directional) == 0)
            {
                return texPath;
            }
            switch (ArtworkFacing(texPath, rot).AsInt)
            {
                case 0:
                    return texPath + "_north";
                case 2:
                    return texPath + "_south";
                case 1:
                    return texPath + "_east";
                default:
                    return texPath + "_west";
            }
        }

        private static FacingMask FacingsOf(string texPath)
        {
            if (texPath.NullOrEmpty())
            {
                return FacingMask.None;
            }
            FacingMask mask;
            if (textureFacings.TryGetValue(texPath, out mask))
            {
                return mask;
            }
            mask = FacingMask.None;
            if (TextureExists(texPath + "_north"))
            {
                mask |= FacingMask.North;
            }
            if (TextureExists(texPath + "_south"))
            {
                mask |= FacingMask.South;
            }
            if (TextureExists(texPath + "_east"))
            {
                mask |= FacingMask.East;
            }
            if (TextureExists(texPath + "_west"))
            {
                mask |= FacingMask.West;
            }
            if (TextureExists(texPath))
            {
                mask |= FacingMask.Single;
            }
            textureFacings[texPath] = mask;
            return mask;
        }

        private static bool TextureExists(string path)
        {
            return ContentFinder<Texture2D>.Get(path, false) != null;
        }

        /// <summary>


        /// <summary>
        /// 扫描全部基因，把尾巴基因上的 BGF 坐标数据登记下来，并记下原始值。
        /// 只做一次；第一次调用时若基因库还没装载完，会留到下次再试。
        /// </summary>
        private static void Collect()
        {
            if (collected)
            {
                return;
            }
            List<GeneDef> defs = DefDatabase<GeneDef>.AllDefsListForReading;
            if (defs == null || defs.Count == 0)
            {
                // 基因库尚未装载完成（例如设置文件在更早的阶段被读取），这次先不动，等下次调用。
                return;
            }
            collected = true;

            BuildTunableBodyTypes();

            for (int i = 0; i < defs.Count; i++)
            {
                GeneDef def = defs[i];
                if (!LooksLikeTail(def))
                {
                    continue;
                }
                GeneEntry entry = null;
                for (int j = 0; j < def.modExtensions.Count; j++)
                {
                    DefModExtension extension = def.modExtensions[j];
                    if (extension == null || extension.GetType().FullName != BgfOffsetExtensionTypeName)
                    {
                        continue;
                    }
                    if (entry == null)
                    {
                        entry = new GeneEntry { Gene = def };
                    }
                    CollectSlots(entry, extension);
                }
                if (entry != null && entry.Slots.Count > 0)
                {
                    entries.Add(entry);
                    matchedGenes.Add(def);
                }
            }
        }

        /// <summary>
        /// 判断一个基因是不是尾巴。
        /// 先排除明确的非尾巴关键词（翅膀），再要求 defName 命中尾巴关键词。
        /// 调用方还会再要求「挂着 BGF 的坐标扩展」，两道筛子一起把范围收窄到真正可调的尾巴。
        /// </summary>
        private static bool LooksLikeTail(GeneDef def)
        {
            if (def == null || def.defName.NullOrEmpty() || def.modExtensions == null)
            {
                return false;
            }
            for (int i = 0; i < ExcludedNameKeywords.Length; i++)
            {
                if (def.defName.IndexOf(ExcludedNameKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return def.defName.IndexOf(TailNameKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>组装可调体型列表：BGF 有字段、且本局存在同名 BodyTypeDef 的体型。</summary>
        private static void BuildTunableBodyTypes()
        {
            for (int i = 0; i < BodyTypeFieldNames.Length; i++)
            {
                // BGF 的字段名就是把体型 defName 的首字母小写，例如 FemaleBB → femaleBB，
                // 所以这里把首字母还原成大写去查 BodyTypeDef。
                string defName = char.ToUpperInvariant(BodyTypeFieldNames[i][0]) + BodyTypeFieldNames[i].Substring(1);
                BodyTypeDef bodyType = DefDatabase<BodyTypeDef>.GetNamedSilentFail(defName);
                if (bodyType != null)
                {
                    tunableBodyTypes.Add(bodyType);
                }
            }
        }

        /// <summary>登记一个 WithBodyTypeOffset 扩展里的三份朝向数据。</summary>
        private static void CollectSlots(GeneEntry entry, DefModExtension extension)
        {
            Type extensionType = extension.GetType();
            for (int i = 0; i < ContainerFieldNames.Length; i++)
            {
                string containerName = ContainerFieldNames[i];
                FieldInfo containerField = extensionType.GetField(containerName, BindingFlags.Public | BindingFlags.Instance);
                if (containerField == null)
                {
                    continue;
                }
                object container = containerField.GetValue(extension);
                if (container == null)
                {
                    // 作者没给这个朝向配坐标，跳过。凭空造一份容器出来等于替作者改了朝向，风险大于收益。
                    continue;
                }
                Slot slot = new Slot
                {
                    Container = container,
                    Rot = ContainerRots[i],
                    Sideways = containerName == SidewaysContainerFieldName
                };
                Type containerType = container.GetType();
                for (int j = 0; j < BodyTypeFieldNames.Length; j++)
                {
                    FieldInfo field = containerType.GetField(BodyTypeFieldNames[j], BindingFlags.Public | BindingFlags.Instance);
                    if (field == null || field.FieldType != typeof(Vector3?))
                    {
                        continue;
                    }
                    slot.Fields.Add(field);
                    slot.BodyTypeNames.Add(char.ToUpperInvariant(BodyTypeFieldNames[j][0]) + BodyTypeFieldNames[j].Substring(1));
                    slot.Originals.Add((Vector3?)field.GetValue(container));
                }
                if (slot.Fields.Count == 0)
                {
                    continue;
                }
                entry.Slots.Add(slot);
                entry.ByRot[slot.Rot.AsInt] = slot;
                if (slot.Sideways)
                {
                    // 朝西沿用侧面那一份数据，查表时指向同一个槽位。
                    entry.ByRot[Rot4.West.AsInt] = slot;
                }
            }
        }
    }
}
