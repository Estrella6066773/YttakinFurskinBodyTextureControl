using System.Reflection;
using HarmonyLib;
using Verse;

namespace YttakinFurskinBodyTextureControl
{
    /// <summary>
    /// 模组入口：启动阶段统一打补丁。
    ///
    /// 联动关系：本类只负责调用 Harmony.PatchAll，把本程序集里带 [HarmonyPatch] 的类挂上去；
    /// 具体改了什么，写在各个补丁类自己的说明里。设置界面在 <see cref="YttakinFurskinBodyTextureControlMod"/>。
    ///
    /// 注意：
    /// 1. 用 [StaticConstructorOnStartup] 而不是 Mod 子类，是因为补丁要在任何游戏界面出现之前挂好，
    ///    否则主菜单里就开始渲染的小人（例如背景里的展示单位）会先按旧路径取过一次贴图。
    /// 2. HarmonyId 与 About/About.xml 里的 packageId 不同，且带 local. 前缀，
    ///    表示这是本地模组；卸载本模组不会连带撤销别的模组的补丁。
    /// 3. 这里按设置补一次尾巴偏移：本类在基因数据装载完成之后才运行，是唯一正确的启动时写入点。
    ///    设置对象此时已由 Mod 子类的构造函数建好；万一它还没建好，就什么都不做，
    ///    宁可让尾巴停在作者原始位置，也不要在不知道玩家档位的情况下擅自改动坐标。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ModEntry
    {
        public const string HarmonyId = "local.yttakinFurskinBodyTextureControl";

        static ModEntry()
        {
            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
            YttakinFurskinBodyTextureControlSettings settings = YttakinFurskinBodyTextureControlMod.Settings;
            if (settings != null)
            {
                settings.ApplyTailOffsets();
            }
        }
    }
}
