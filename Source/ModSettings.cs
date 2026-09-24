using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using UnityEngine;
using Verse;

namespace YttakinFurskinBodyTextureControl
{
    /// <summary>
    /// 模组入口类（设置窗口挂在这里）。
    ///
    /// 联动关系：
    ///   游戏启动 → LoadedModManager.CreateModClasses 反射构造本类 → GetSettings 读出设置文件
    ///   基因数据装载完成 → [StaticConstructorOnStartup] 的 ModEntry 按设置写入尾巴偏移
    ///   玩家改设置 → 本类.DoSettingsWindowContents → Settings.DoWindowContents
    ///   关闭设置窗口 → Dialog_ModSettings.PreClose → Mod.WriteSettings 写回设置文件
    ///   补丁读取 → FurskinBodyPatch.Postfix 每次现读 Settings，改完立刻生效，不需要重开游戏
    ///
    /// 注意：
    /// 1. RimWorld 反射查找「继承 Mod、且带一个 ModContentPack 参数构造函数」的类型，所以本类必须 public，
    ///    构造函数签名也不能改。
    /// 2. Settings 是静态字段，供补丁在渲染时读取；补丁会判空，万一还没构造出来就退回原版取值。
    /// 3. 构造函数里不写尾巴偏移：本类构造发生在基因数据装载之前（加载顺序是
    ///    CreateModClasses → LoadModXML → 解析 Def），那时基因库还是空的。
    ///    偏移改由 ModEntry 这个 [StaticConstructorOnStartup] 入口在装载完成之后写入。
    /// </summary>
    public class YttakinFurskinBodyTextureControlMod : Mod
    {
        public static YttakinFurskinBodyTextureControlSettings Settings;

        public YttakinFurskinBodyTextureControlMod(ModContentPack content)
            : base(content)
        {
            Settings = GetSettings<YttakinFurskinBodyTextureControlSettings>();
        }

        public override string SettingsCategory()
        {
            return Content.Name;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings?.DoWindowContents(inRect);
        }
    }

    /// <summary>
    /// 存放在设置文件里的内容：身体贴图用哪一套，以及尾巴位置微调。
    ///
    /// 尾巴微调的数据结构：
    ///   两张表的键都由「哪条尾巴 + 哪种体型」共同决定，见 <see cref="TailOffsets.Key"/>，
    ///   例如 AFU_BA_FoxTail|Male。
    ///   也就是说每一条尾巴在每一种体型下都各存一套高度与前后数值：
    ///   换尾巴换的是另一份，换体型换的也是另一份，两者互不干扰。
    ///   值为 0 的条目会被移除，所以设置文件里只留真正调过的组合。
    ///   键与当前游戏里的尾巴、体型对不上时既不报错也不生效，只是闲置。
    ///
    /// 注意：本版把键从「只有体型」改成「尾巴 + 体型」，旧设置文件里的数值不会被沿用，需要重新调一次。
    /// </summary>
    public class YttakinFurskinBodyTextureControlSettings : ModSettings
    {
        /// <summary>滑条的可调范围与步长。「高度」与「前后」共用一组，方便对照着调。
        /// 量程取 ±1，是因为作者自己给最大的尾巴（九尾狐尾）写过 -1.1 的坐标，留出余量后仍够用。</summary>
        private const float TailOffsetLimit = 1f;

        private const float TailOffsetStep = 0.01f;

        /// <summary>预览框之间的间距。</summary>
        private const float PreviewGap = 6f;

        public bool useOriginalModTextures;

        /// <summary>键见 <see cref="TailOffsets.Key"/>，值为尾巴高度增量。作用于正北、正南、侧面三个朝向。</summary>
        public Dictionary<string, float> tailHeightOffsets = new Dictionary<string, float>();

        /// <summary>键见 <see cref="TailOffsets.Key"/>，值为尾巴前后增量。只作用于侧面朝向。</summary>
        public Dictionary<string, float> tailDepthOffsets = new Dictionary<string, float>();

        /// <summary>设置界面里当前选中的体型与尾巴部件。存下来只是为了下次打开窗口时停在原处。</summary>
        public string previewBodyTypeName;

        public string previewTailDefName;

        /// <summary>
        /// 滚动视图的内容高度，取上一帧量到的值。
        ///
        /// 滚动视图要先知道内容多高才能画滚动条，而这个高度只能边画边量，所以只能用上一帧的结果。
        /// 初值必须给得足够大：给小了首帧就会把超出部分裁掉，中间闪一下再恢复。
        /// 首帧画完会立刻被测得的真实高度覆盖，所以这里宁可偏大。
        /// </summary>
        private float contentHeight = 2000f;

        private Vector2 scrollPosition;

        /// <summary>本帧可用于画控件的宽度，由 DoWindowContents 设定，供各选择器分行用。</summary>
        private float contentWidth = 800f;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref useOriginalModTextures, "useOriginalModTextures", defaultValue: false);
            Scribe_Collections.Look(ref tailHeightOffsets, "tailHeightOffsets", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref tailDepthOffsets, "tailDepthOffsets", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref previewBodyTypeName, "previewBodyTypeName");
            Scribe_Values.Look(ref previewTailDefName, "previewTailDefName");

            if (tailHeightOffsets == null)
            {
                tailHeightOffsets = new Dictionary<string, float>();
            }
            if (tailDepthOffsets == null)
            {
                tailDepthOffsets = new Dictionary<string, float>();
            }
        }

        public void DoWindowContents(Rect inRect)
        {
            // 内容比窗口高，所以整页放在滚动视图里，画面小或字体放大时也不会把下面的控件挤掉。
            Rect viewRect = new Rect(0f, 0f, inRect.width - 20f, contentHeight);
            contentWidth = viewRect.width;
            Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);
            // 这一行不能省，否则设置页只会显示开头一两行。
            // Listing 在内容超过 Begin 传进去的高度时会自动换到「第二列」（见 Listing.NewColumnIfNeeded），
            // 而第二列画在第一列右侧之外，正好落在 Begin 内部 BeginGroup 的裁剪范围外，整段都被吃掉。
            // 传给 Begin 的高度是上一帧量到的内容高度，而换列后 curY 会归零重算，
            // 于是量到的高度越变越小、换列越来越早，形成恶性循环，最后只剩开头一行可见。
            // 强制单列之后，内容一路向下排，CurHeight 才是真实高度，测量也随之稳定。
            listing.maxOneColumn = true;

            DrawBodyTextureSection(listing);
            DrawTailSection(listing);

            listing.GapLine();
            LabelDesc(listing, "FurskinBodyNote".Translate());
            listing.Gap(12f);
            if (listing.ButtonText("FurskinBodyReset".Translate()))
            {
                RestoreDefaults();
                FurskinBodyTextures.RefreshFurskinPawnGraphics();
                ApplyTailOffsets();
            }

            listing.End();
            // 量高用 MaxColumnHeightSeen：取本帧实际画到的最大高度，比单看 CurHeight 更稳妥。
            // 下限取窗口高度，内容比窗口矮时就不会因为高估而多出一截空白和多余的滚动条。
            contentHeight = Mathf.Max(listing.MaxColumnHeightSeen + 16f, inRect.height);
            Widgets.EndScrollView();
        }

        /// <summary>
        /// 把当前设置里的尾巴偏移写进基因数据。
        ///
        /// 联动关系：三处会调用它——启动时由 <see cref="ModEntry"/> 调一次，
        /// 身体贴图档位切换时调一次（档位决定微调是否生效），拖动滑条时调一次。
        ///
        /// 注意：自定义只在「停用内置贴图」这一档生效；处于另一档时这里会按零增量应用，
        /// 等价于把尾巴还原成作者原始坐标，而滑条上的数值仍然保留在设置文件里，切回来即恢复。
        /// </summary>
        internal void ApplyTailOffsets()
        {
            TailOffsets.Apply(!useOriginalModTextures, tailHeightOffsets, tailDepthOffsets);
        }

        /// <summary>身体贴图档位那一块。</summary>
        private void DrawBodyTextureSection(Listing_Standard listing)
        {
            LabelHeading(listing, "FurskinBodySection".Translate());
            LabelDesc(listing, "FurskinBodyDesc".Translate());
            listing.GapLine();

            bool before = useOriginalModTextures;
            listing.CheckboxLabeled("FurskinBodyToggle".Translate(), ref useOriginalModTextures, "FurskinBodyToggleTip".Translate());
            if (before != useOriginalModTextures)
            {
                // 渲染树的图形带缓存，改完要主动通知才会重画；处于主菜单等没有存档的场合时该调用什么也不做。
                FurskinBodyTextures.RefreshFurskinPawnGraphics();
                // 档位决定尾巴微调是否生效，所以切档时坐标也要按新档位重写一遍。
                ApplyTailOffsets();
            }

            LabelStatus(listing, StatusText());
        }

        /// <summary>
        /// 尾巴微调那一块。
        /// 自定义只在「停用内置贴图」这一档生效，所以只有这一档才画得出编辑控件；另一档只说明情况。
        /// </summary>
        private void DrawTailSection(Listing_Standard listing)
        {
            listing.Gap(14f);
            LabelHeading(listing, "FurskinTailSection".Translate());
            LabelDesc(listing, "FurskinTailDesc".Translate());
            listing.GapLine();

            if (useOriginalModTextures)
            {
                LabelStatus(listing, "FurskinTailStatusForcedMode".Translate(ConfiguredCount()));
                return;
            }

            IList<GeneDef> genes = TailOffsets.MatchedGenes;
            IList<BodyTypeDef> bodyTypes = TailOffsets.TunableBodyTypes;
            if (genes.Count == 0 || bodyTypes.Count == 0)
            {
                LabelStatus(listing, "FurskinTailStatusAbsent".Translate().Colorize(ColorLibrary.RedReadable));
                return;
            }

            // 先画选择器再取选择结果，这样点完按钮的当帧就能看到预览与滑条跟着换，不会拖一帧。
            LabelDesc(listing, "FurskinTailBodyType".Translate());
            DrawBodyTypeSelector(listing, bodyTypes);
            BodyTypeDef bodyType = CurrentBodyType(bodyTypes);

            listing.Gap(4f);
            LabelDesc(listing, "FurskinTailPartType".Translate());
            DrawTailSelector(listing, genes);
            GeneDef gene = CurrentTail(genes);

            listing.Gap(6f);
            DrawPreviewRow(listing, gene, bodyType);
            LabelDesc(listing, "FurskinTailPreviewNote".Translate());

            listing.Gap(8f);
            DrawOffsetSliders(listing, gene, bodyType);

            LabelStatus(listing, "FurskinTailStatusOk".Translate(genes.Count));
            LabelDesc(listing, "FurskinTailPartsNote".Translate());
        }

        /// <summary>四个朝向并排的预览。左右两侧用来确认「前后」，南北用来确认「前后不影响正面与背面」。</summary>
        private void DrawPreviewRow(Listing_Standard listing, GeneDef gene, BodyTypeDef bodyType)
        {
            Rect row = listing.GetRect(TailPreview.BoxHeight);
            float width = (row.width - PreviewGap * (TailPreview.ShownFacings.Length - 1)) / TailPreview.ShownFacings.Length;
            for (int i = 0; i < TailPreview.ShownFacings.Length; i++)
            {
                Rot4 rot = TailPreview.ShownFacings[i];
                Rect box = new Rect(row.x + i * (width + PreviewGap), row.y, width, row.height);
                TailPreview.Draw(
                    box,
                    gene,
                    bodyType,
                    rot,
                    useOriginalModTextures,
                    HeightFor(gene, bodyType),
                    DepthFor(gene, bodyType));
            }
        }

        /// <summary>
        /// 当前这一对「尾巴 + 体型」的两个滑条。
        /// 拖动后立即写回基因数据，预览同帧跟着变；写回只影响这一对，别的尾巴与体型不受影响。
        /// </summary>
        private void DrawOffsetSliders(Listing_Standard listing, GeneDef gene, BodyTypeDef bodyType)
        {
            float height = HeightFor(gene, bodyType);
            float depth = DepthFor(gene, bodyType);

            listing.Label(
                "FurskinTailHeight".Translate(FormatOffset(height)),
                -1f,
                "FurskinTailHeightTip".Translate().Resolve());
            float newHeight = Snap(listing.Slider(height, -TailOffsetLimit, TailOffsetLimit));

            listing.Label(
                "FurskinTailDepth".Translate(FormatOffset(depth)),
                -1f,
                "FurskinTailDepthTip".Translate().Resolve());
            float newDepth = Snap(listing.Slider(depth, -TailOffsetLimit, TailOffsetLimit));

            if (newHeight == height && newDepth == depth)
            {
                return;
            }
            SetHeightFor(gene, bodyType, newHeight);
            SetDepthFor(gene, bodyType, newDepth);
            ApplyTailOffsets();
        }

        private void DrawBodyTypeSelector(Listing_Standard listing, IList<BodyTypeDef> bodyTypes)
        {
            DrawSelectorGrid(
                listing,
                bodyTypes,
                BodyTypeLabel,
                def => def.defName,
                previewBodyTypeName,
                defName => previewBodyTypeName = defName,
                86f);
        }

        private void DrawTailSelector(Listing_Standard listing, IList<GeneDef> genes)
        {
            DrawSelectorGrid(
                listing,
                genes,
                def => def.LabelCap.Resolve(),
                def => def.defName,
                previewTailDefName,
                defName => previewTailDefName = defName,
                150f);
        }

        /// <summary>
        /// 画一组单选按钮，按可用宽度自动分行。
        /// 选中的那项会叠一层高亮底色，而不是置灰：置灰的按钮在 RimWorld 里看起来像不可点，
        /// 而这里点它是合法的（点到已选项等于没变）。
        /// </summary>
        private void DrawSelectorGrid<T>(
            Listing_Standard listing,
            IList<T> items,
            Func<T, string> labelOf,
            Func<T, string> keyOf,
            string currentKey,
            Action<string> onPick,
            float buttonWidth)
        {
            const float Gap = 4f;
            const float RowHeight = 28f;

            if (items.Count == 0)
            {
                return;
            }

            int perRow = Mathf.Max(1, Mathf.FloorToInt((contentWidth + Gap) / (buttonWidth + Gap)));
            int rows = Mathf.CeilToInt(items.Count / (float)perRow);
            Rect area = listing.GetRect(rows * RowHeight + (rows - 1) * Gap);

            for (int i = 0; i < items.Count; i++)
            {
                int row = i / perRow;
                int column = i % perRow;
                Rect button = new Rect(
                    area.x + column * (buttonWidth + Gap),
                    area.y + row * (RowHeight + Gap),
                    buttonWidth,
                    RowHeight);

                string key = keyOf(items[i]);
                bool selected = key == currentKey;
                if (selected)
                {
                    // 先铺一层底色，让选中的按钮带上色；按钮底图是半透明的，颜色能透出来。
                    Widgets.DrawBoxSolid(button, new Color(0.35f, 0.55f, 0.85f, 0.55f));
                }
                if (Widgets.ButtonText(button, labelOf(items[i])))
                {
                    onPick(key);
                }
                if (selected)
                {
                    // 再补一圈描边。底色能否透出来取决于按钮底图的透明度，描边则一定看得见，
                    // 两道记号叠加，选中项在任何情况下都分得清。
                    Widgets.DrawBox(button, 2);
                }
            }
        }

        /// <summary>
        /// 体型按钮上的文字。
        ///
        /// 原版的 BodyTypeDef 根本没有 label 字段：Core 的 BodyTypes.xml 里只有坐标、贴图路径与
        /// 伤口锚点，一个 label 都没写（可用 Def.LabelCap 的实现在 label 为空时返回 null 验证）。
        /// 所以直接取 def.LabelCap 会得到一排没有字的按钮，必须改用本模组自己的翻译键。
        /// 键按体型 defName 拼出，取不到时退回 defName，保证按钮一定有文字。
        /// </summary>
        private static string BodyTypeLabel(BodyTypeDef bodyType)
        {
            TaggedString translated;
            if (("FurskinBodyType_" + bodyType.defName).TryTranslate(out translated))
            {
                return translated.Resolve();
            }
            return bodyType.defName;
        }

        /// <summary>取出当前该用于预览的体型；选择失效（例如那个模组被停用）时退回第一个。</summary>
        private BodyTypeDef CurrentBodyType(IList<BodyTypeDef> bodyTypes)
        {
            for (int i = 0; i < bodyTypes.Count; i++)
            {
                if (bodyTypes[i].defName == previewBodyTypeName)
                {
                    return bodyTypes[i];
                }
            }
            previewBodyTypeName = bodyTypes[0].defName;
            return bodyTypes[0];
        }

        /// <summary>取出当前该用于预览的尾巴部件；选择失效时退回第一个。</summary>
        private GeneDef CurrentTail(IList<GeneDef> genes)
        {
            for (int i = 0; i < genes.Count; i++)
            {
                if (genes[i].defName == previewTailDefName)
                {
                    return genes[i];
                }
            }
            previewTailDefName = genes[0].defName;
            return genes[0];
        }

        /// <summary>读这一对「尾巴 + 体型」的高度增量。</summary>
        internal float HeightFor(GeneDef gene, BodyTypeDef bodyType)
        {
            return Lookup(tailHeightOffsets, gene, bodyType);
        }

        /// <summary>读这一对「尾巴 + 体型」的前后增量。</summary>
        internal float DepthFor(GeneDef gene, BodyTypeDef bodyType)
        {
            return Lookup(tailDepthOffsets, gene, bodyType);
        }

        private static float Lookup(Dictionary<string, float> table, GeneDef gene, BodyTypeDef bodyType)
        {
            if (gene == null || bodyType == null)
            {
                return 0f;
            }
            float value;
            return table.TryGetValue(TailOffsets.Key(gene.defName, bodyType.defName), out value) ? value : 0f;
        }

        private void SetHeightFor(GeneDef gene, BodyTypeDef bodyType, float value)
        {
            Set(tailHeightOffsets, gene, bodyType, value);
        }

        private void SetDepthFor(GeneDef gene, BodyTypeDef bodyType, float value)
        {
            Set(tailDepthOffsets, gene, bodyType, value);
        }

        private static void Set(Dictionary<string, float> table, GeneDef gene, BodyTypeDef bodyType, float value)
        {
            string key = TailOffsets.Key(gene.defName, bodyType.defName);
            value = Snap(value);
            if (value == 0f)
            {
                // 归零就删掉条目，设置文件里只留真正调过的组合。
                table.Remove(key);
            }
            else
            {
                table[key] = value;
            }
        }

        /// <summary>统计已经调过的组合数量，供非生效档位下说明「已保存多少组」。</summary>
        private int ConfiguredCount()
        {
            HashSet<string> keys = new HashSet<string>();
            foreach (string key in tailHeightOffsets.Keys)
            {
                keys.Add(key);
            }
            foreach (string key in tailDepthOffsets.Keys)
            {
                keys.Add(key);
            }
            return keys.Count;
        }

        /// <summary>说明当前档位实际在用哪一套贴图；选中的那一档贴图缺失时改用红字，说明已经退回。</summary>
        private TaggedString StatusText()
        {
            if (!FurskinBodyTextures.OriginalModLoaded)
            {
                // 本模组的依赖里写了原模组，正常不会走到这里；万一原模组被停用，如实说明本开关没有作用。
                return "FurskinBodyStatusAbsent".Translate().Colorize(ColorLibrary.RedReadable);
            }
            if (useOriginalModTextures)
            {
                return FurskinBodyTextures.CuteTexturesAvailable
                    ? "FurskinBodyStatusOn".Translate()
                    : "FurskinBodyStatusOnMissing".Translate().Colorize(ColorLibrary.RedReadable);
            }
            return FurskinBodyTextures.VanillaTexturesAvailable
                ? "FurskinBodyStatusOff".Translate()
                : "FurskinBodyStatusOffMissing".Translate().Colorize(ColorLibrary.RedReadable);
        }

        /// <summary>
        /// 画一级小标题：中号字，用来和说明文字拉开层次。
        /// 中号字比正文大一圈，所以顺带留出上下间距，免得挤在一起。
        /// </summary>
        private static void LabelHeading(Listing_Standard listing, TaggedString text)
        {
            GameFont previousFont = Text.Font;
            Text.Font = GameFont.Medium;
            listing.Label(text);
            Text.Font = previousFont;
            listing.Gap(4f);
        }

        /// <summary>
        /// 画说明文字：小号（Tiny）并压暗一档，与中号标题区分开。
        ///
        /// 字号与颜色都靠 Text.Font 和 GUI.color 这两个全局状态生效，
        /// 所以用完必须还原，否则会串到后面画的控件上。
        /// 行高由 Listing 内部按当前字体自动算，不需要在这里补高度。
        /// 只用于解释性文字。
        /// </summary>
        private static void LabelDesc(Listing_Standard listing, TaggedString text)
        {
            LabelSmall(listing, text, dim: true);
        }

        /// <summary>
        /// 画状态文字：同样用小号，但保持原色不压暗。
        ///
        /// 与说明文字分成两个方法，是因为状态里会有红字警告（贴图缺失、没有可调尾巴等），
        /// 压暗会把红色一起削弱，反而看不清。状态只缩字号，不改亮度。
        /// </summary>
        private static void LabelStatus(Listing_Standard listing, TaggedString text)
        {
            LabelSmall(listing, text, dim: false);
        }

        private static void LabelSmall(Listing_Standard listing, TaggedString text, bool dim)
        {
            GameFont previousFont = Text.Font;
            Color previousColor = GUI.color;
            Text.Font = GameFont.Tiny;
            if (dim)
            {
                GUI.color = new Color(0.75f, 0.75f, 0.75f);
            }
            listing.Label(text);
            GUI.color = previousColor;
            Text.Font = previousFont;
        }

        /// <summary>把滑条回传的连续值收拢到步长上，避免出现 0.30000001 这类数字。</summary>
        private static float Snap(float value)
        {
            return Mathf.Round(value / TailOffsetStep) * TailOffsetStep;
        }

        /// <summary>把增量显示成带正负号的两位小数，正负一眼可辨。</summary>
        private static string FormatOffset(float value)
        {
            return value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        }

        private void RestoreDefaults()
        {
            useOriginalModTextures = false;
            tailHeightOffsets.Clear();
            tailDepthOffsets.Clear();
        }
    }
}
