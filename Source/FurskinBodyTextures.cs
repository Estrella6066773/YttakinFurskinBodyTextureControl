using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace YttakinFurskinBodyTextureControl
{
    /// <summary>
    /// 毛绒种（Furskin 基因）成年身体贴图的取值来源。
    ///
    /// 联动关系：
    /// 1. 本类被 <see cref="FurskinBodyPatch"/> 调用，为 <c>FurDef.GetFurBodyGraphicPath</c> 提供返回值；
    ///    取哪一档由 <see cref="YttakinFurskinBodyTextureControlMod.Settings"/> 的开关决定。
    /// 2. 开关改变后由 <see cref="RefreshFurskinPawnGraphics"/> 通知渲染树重画贴图。
    ///
    /// 需要注意：
    /// 1. 本类只负责说明「用哪张图」，不负责「怎么画」，因此原模组日后调整画法（颜色、偏移等）不受影响。
    /// 2. 两档路径都要在选用前探测贴图是否真的存在。原版的身体贴图不在 Core 的文件目录里
    ///    （打包在 Unity 资源与资料片的资源包里），所以探测原版路径必须走 <see cref="ContentFinder{T}"/>，
    ///    它会依次查各模组的散装文件、Unity 资源、资料片资源包；而原模组的重绘贴图是散装文件，
    ///    探测时只认它自己的内容，避免把别的模组顶替进来说成「原模组重绘」。
    /// 3. 探测失败时一律返回 null，由补丁退回原版方法的取值，不让一个空路径或错路径交给渲染层导致身体整块消失。
    /// </summary>
    internal static class FurskinBodyTextures
    {
        internal const string FurskinDefName = "Furskin";

        /// <summary>原模组「Yttakin furskin retexture and gene tweak」的 packageId。</summary>
        internal const string OriginalModPackageId = "ChenDuXiu.Gene.MoreCuteYttakin";

        private const string VanillaBodies = "Things/Pawn/Humanlike/Bodies/";
        private const string CuteBodies = "Things/Pawn/Humanlike/Bodies/NL/";
        private const string FurChild = VanillaBodies + "Naked_FurChild";

        /// <summary>探测贴图存在与否时用的方向后缀。身体是多方向贴图，抽查朝南一张足够判断整套是否就位。</summary>
        private const string ProbeSuffix = "_south";

        /// <summary>Furskin 的 bodyTypeGraphicPaths 里成年体型对应的项名，也就是本模组真正会改写的范围。</summary>
        private static readonly string[] OverriddenBodyTypes = { "Male", "Female", "Hulk", "Fat", "Thin" };

        private static readonly Dictionary<string, string> cutePaths = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> vanillaPaths = new Dictionary<string, string>();

        private static bool tablesBuilt;
        private static bool cuteChecked;
        private static bool cuteAvailable;
        private static bool cuteMissingWarned;
        private static bool vanillaChecked;
        private static bool vanillaAvailable;
        private static bool vanillaMissingWarned;

        /// <summary>原模组的重绘贴图是否齐全可用。探测只做一次。</summary>
        internal static bool CuteTexturesAvailable
        {
            get
            {
                if (!cuteChecked)
                {
                    cuteChecked = true;
                    BuildTables();
                    cuteAvailable = CheckCuteTextures();
                    if (!cuteAvailable && !cuteMissingWarned)
                    {
                        cuteMissingWarned = true;
                        Log.Warning(
                            "[毛绒种身体贴图控制] 没能在「Yttakin furskin retexture and gene tweak」里找到重绘的身体贴图，"
                            + "「强制使用该模组贴图」这一档已退回原模组自己的取值。原模组日后改动贴图存放路径时会出现这条提示。");
                    }
                }
                return cuteAvailable;
            }
        }

        /// <summary>原版那套毛绒身体贴图是否还在。原版改名换路径时这一档才需要退回。</summary>
        internal static bool VanillaTexturesAvailable
        {
            get
            {
                if (!vanillaChecked)
                {
                    vanillaChecked = true;
                    BuildTables();
                    vanillaAvailable = CheckVanillaTextures();
                    if (!vanillaAvailable && !vanillaMissingWarned)
                    {
                        vanillaMissingWarned = true;
                        Log.Warning(
                            "[毛绒种身体贴图控制] 找不到原版的毛绒身体贴图，"
                            + "「停用该模组贴图」这一档已退回原模组自己的取值。原版日后改动贴图存放路径时会出现这条提示。");
                    }
                }
                return vanillaAvailable;
            }
        }

        internal static bool OriginalModLoaded
        {
            get { return FindOriginalMod() != null; }
        }

        /// <summary>
        /// 返回 bodyTypeGraphicPaths 里该体型应当取的贴图路径。
        /// 返回 null 表示这一项不该由本模组决定，调用方应保留原版方法的取值。
        /// </summary>
        internal static string Resolve(string bodyTypeDefName, bool useOriginalModTextures)
        {
            if (bodyTypeDefName.NullOrEmpty())
            {
                return null;
            }
            BuildTables();
            string path;
            if (useOriginalModTextures)
            {
                if (!cutePaths.TryGetValue(bodyTypeDefName, out path))
                {
                    return null;
                }
                return CuteTexturesAvailable ? path : null;
            }
            // 停用档只把原模组重绘的那几项改回原版路径。幼年、婴儿以及别的模组身体型不在其中：
            // 它们本来就没有被原模组改过，硬写一份路径反而会盖掉别的模组对它们的处理。
            if (!vanillaPaths.TryGetValue(bodyTypeDefName, out path))
            {
                return null;
            }
            return VanillaTexturesAvailable ? path : null;
        }

        /// <summary>
        /// 预览用的毛皮贴图路径：返回游戏此刻真正会用的那一张。
        ///
        /// 与 <see cref="Resolve"/> 的区别在于对「本模组不管的体型」的处理：
        /// Resolve 返回 null 是让补丁保留原版取值，而补丁保留的那个值本身可能是张真贴图
        /// （例如幼年与婴儿，作者给的是原版 Naked_FurChild）。预览要画的是画面上真正出现的东西，
        /// 所以这里在 Resolve 给不出路径时，回头去读 FurDef 上现存的 bodyTypeGraphicPaths，
        /// 那正是补丁返回 null 时游戏会采用的那份值。
        /// </summary>
        internal static string PreviewFurPath(string bodyTypeDefName, bool useOriginalModTextures)
        {
            string overridden = Resolve(bodyTypeDefName, useOriginalModTextures);
            if (!overridden.NullOrEmpty())
            {
                return overridden;
            }
            FurDef furDef = DefDatabase<FurDef>.GetNamedSilentFail(FurskinDefName);
            if (furDef == null || furDef.bodyTypeGraphicPaths == null)
            {
                return null;
            }
            for (int i = 0; i < furDef.bodyTypeGraphicPaths.Count; i++)
            {
                BodyTypeGraphicData data = furDef.bodyTypeGraphicPaths[i];
                if (data != null && data.bodyType != null && data.bodyType.defName == bodyTypeDefName)
                {
                    return data.texturePath;
                }
            }
            return null;
        }

        /// <summary>
        /// 让场上所有毛绒种单位重画贴图。
        /// 渲染树的图形带缓存，换了路径不通知就看不到变化；同时也不该全图重画，别的单位并没有变化。
        ///
        /// 注意：这里只覆盖活着的单位（含商队与途中运输）。已经变成尸体的毛绒种不在其中，
        /// 它们会保留切换前的贴图，直到尸体被重新载入或离图；这点差别只在贴图之间，不影响其它行为。
        /// </summary>
        internal static void RefreshFurskinPawnGraphics()
        {
            if (Current.Game == null)
            {
                return;
            }
            List<Pawn> pawns = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive;
            if (pawns == null)
            {
                return;
            }
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.story == null || pawn.story.furDef == null)
                {
                    continue;
                }
                if (pawn.story.furDef.defName != FurskinDefName)
                {
                    continue;
                }
                PawnRenderer renderer = pawn.Drawer?.renderer;
                if (renderer != null)
                {
                    renderer.SetAllGraphicsDirty();
                }
            }
        }

        private static void BuildTables()
        {
            if (tablesBuilt)
            {
                return;
            }
            tablesBuilt = true;
            for (int i = 0; i < OverriddenBodyTypes.Length; i++)
            {
                string bodyType = OverriddenBodyTypes[i];
                cutePaths[bodyType] = CuteBodies + "FurCovered_" + bodyType;
                vanillaPaths[bodyType] = VanillaBodies + "FurCovered_" + bodyType;
            }
            // 幼年与婴儿在原模组里没有重绘，两档都用原版那张裸体毛绒贴图。列进来是为了查表结果明确，而不是留空。
            cutePaths["Child"] = FurChild;
            cutePaths["Baby"] = FurChild;
        }

        private static bool CheckCuteTextures()
        {
            ModContentPack pack = FindOriginalMod();
            if (pack == null)
            {
                return false;
            }
            ModContentHolder<Texture2D> holder = pack.GetContentHolder<Texture2D>();
            if (holder == null)
            {
                return false;
            }
            // 缺一张也算不可用：宁可整体退回，也不要出现半套重绘、半套原版的画面。
            for (int i = 0; i < OverriddenBodyTypes.Length; i++)
            {
                string path = cutePaths[OverriddenBodyTypes[i]] + ProbeSuffix;
                if (holder.Get(path) == null)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool CheckVanillaTextures()
        {
            for (int i = 0; i < OverriddenBodyTypes.Length; i++)
            {
                string path = vanillaPaths[OverriddenBodyTypes[i]] + ProbeSuffix;
                if (ContentFinder<Texture2D>.Get(path, false) == null)
                {
                    return false;
                }
            }
            return true;
        }

        private static ModContentPack FindOriginalMod()
        {
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            if (mods == null)
            {
                return null;
            }
            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack pack = mods[i];
                if (pack != null && string.Equals(pack.PackageId, OriginalModPackageId, StringComparison.OrdinalIgnoreCase))
                {
                    return pack;
                }
            }
            return null;
        }
    }
}
