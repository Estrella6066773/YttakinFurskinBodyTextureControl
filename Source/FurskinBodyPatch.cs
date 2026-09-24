using HarmonyLib;
using RimWorld;
using Verse;

namespace YttakinFurskinBodyTextureControl
{
    /// <summary>
    /// 改写 <c>FurDef.GetFurBodyGraphicPath</c> 的返回值，也就是「毛绒种该用哪张身体贴图」这个问题的唯一出口。
    ///
    /// 联动关系：
    /// 1. 渲染侧的调用链是 PawnRenderNode_Fur.GraphicFor → FurDef.GetFurBodyGraphicPath。
    ///    只改这一个方法的返回值，就能同时影响小人、尸体、头像等所有画毛绒身体的地方。
    /// 2. 该方法的实现是遍历 <c>bodyTypeGraphicPaths</c> 找当前体型对应的 texturePath，
    ///    原模组的贴图替代正是通过 XML 改写这个字段做到的。因此这里用事后补丁覆盖它的结果，
    ///    <c>__result</c> 保留着「没人插手时」的取值，本模组不想管的体型一律原样放行。
    ///
    /// 需要注意：
    /// 1. 本补丁常驻，不随开关启停：开关是运行期读设置的，一旦卸载补丁，关闭档就再也没机会纠正路径。
    /// 2. 补丁只在本模组能完整接管时才装（见 <see cref="Prepare"/>）。条件不满足时干脆不装，
    ///    此时游戏维持原模组原本的行为，比装上去再返回一堆 null 更可预期。
    /// </summary>
    [HarmonyPatch(typeof(FurDef), nameof(FurDef.GetFurBodyGraphicPath))]
    internal static class FurskinBodyPatch
    {
        [HarmonyPrepare]
        private static bool Prepare()
        {
            if (!ModsConfig.BiotechActive)
            {
                return false;
            }
            return FurskinBodyTextures.OriginalModLoaded;
        }

        private static string Postfix(string __result, Pawn pawn)
        {
            YttakinFurskinBodyTextureControlSettings settings = YttakinFurskinBodyTextureControlMod.Settings;
            if (settings == null)
            {
                return __result;
            }
            BodyTypeDef bodyType = pawn?.story?.bodyType;
            if (bodyType == null)
            {
                return __result;
            }
            string path = FurskinBodyTextures.Resolve(bodyType.defName, settings.useOriginalModTextures);
            return path ?? __result;
        }
    }
}
