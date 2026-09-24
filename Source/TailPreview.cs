using RimWorld;
using UnityEngine;
using Verse;

namespace YttakinFurskinBodyTextureControl
{
    /// <summary>
    /// 设置界面里的尾巴位置预览图。
    ///
    /// 联动关系：
    /// 1. 预览由 <see cref="YttakinFurskinBodyTextureControlSettings.DoWindowContents"/> 调用，
    ///    每个朝向画一格，四格并排（南、东、北、西），用来对照「前后只影响侧面」这件事。
    /// 2. 身体、毛皮、尾巴的层序照抄游戏：三者的先后由各节点的层号决定，不是固定的「身体在下、尾巴在上」。
    ///    身体取自 BodyTypeDef.bodyNakedGraphicPath，与 PawnRenderNode_Body.GraphicFor 用的是同一个字段；
    ///    毛皮取自 <see cref="FurskinBodyTextures.PreviewFurPath"/>；
    ///    尾巴取自尾巴基因的 renderNodeProperties，其层号按朝向取值，见 <see cref="LayerOrder"/>。
    /// 3. 坐标取自 <see cref="TailOffsets.EffectiveOffsetCells"/>，它按 BGF 的规则算好了朝向与体型，
    ///    因此预览里的位移与游戏里的位移是同一个数。
    ///
    /// 需要注意：
    /// 1. 预览用的是方案图（未上色）直接叠加，不经过着色器。游戏里皮肤与毛发会被小人自身的颜色染色，
    ///    这里没有小人可依据，所以改成给三层固定色，只求「看得出层次与位置」，不求色彩逼真。
    /// 2. 尺寸基准取自实际渲染节点：身体节点与原版一样没有写 drawSize，按默认的 1 格画；
    ///    尾巴用基因自己写的 drawSize（例如九尾狐尾是 1.5 格）。两者都用同一个「每格像素」换算，
    ///    所以位移的视觉比例与游戏一致。
    /// 3. 遮挡靠绘制顺序实现，不做逐像素遮罩：毛皮是不透明的整身贴图，
    ///    只要在尾巴之后画，压住的部分自然被盖掉，与游戏里靠层号压住的效果一致。
    /// 4. 每一层该加载哪个文件，交给 <see cref="TailOffsets.ArtworkPath"/> 决定，
    ///    它照抄游戏 Graphic_Multi 的规则：缺哪个朝向的图就用别的朝向顶替，缺 west 时用 east 再水平镜像。
    ///    实测这批尾巴贴图只有 north、south、east 三种，8 条尾巴连 south 都没有，
    ///    所以这两条回退必须照做，否则朝西与正面那两格会整层空掉。
    /// 5. 贴图缺失时只跳过那一层，不报错也不弹窗：预览是辅助，缺一层仍能判断位置。
    /// </summary>
    internal static class TailPreview
    {
        /// <summary>
        /// 预览框高度。取值偏矮是为了让四格预览落在首屏之内（模组设置窗口的可视高度约 590），
        /// 玩家不用先滚动就能看到调整结果。
        /// </summary>
        internal const float BoxHeight = 132f;

        /// <summary>身体节点的尺寸。原版 humanlike 的身体节点没有写 drawSize，取 PawnRenderNodeProperties 的默认值 1。</summary>
        private const float BodyCells = 1f;

        /// <summary>预览框代表的世界格数。数值越小，位移看得越明显。</summary>
        private const float PreviewCells = 1.8f;

        /// <summary>要预览的朝向，按南、东、北、西排列：先看正面，再看两个侧面，最后看背面。</summary>
        internal static readonly Rot4[] ShownFacings = { Rot4.South, Rot4.East, Rot4.North, Rot4.West };

        /// <summary>三层的固定色，只用于区分层次，不代表小人的真实配色。</summary>
        private static readonly Color BodyTint = new Color(0.86f, 0.78f, 0.70f);

        private static readonly Color FurTint = new Color(0.62f, 0.52f, 0.44f);

        private static readonly Color CenterLineColor = new Color(1f, 1f, 1f, 0.12f);

        /// <summary>裸体节点的层号。原版 humanlike 的身体节点没有写 baseLayer，取默认值 0。</summary>
        private const float BodyLayer = 0f;

        /// <summary>取不到毛皮节点时用的层号。取一个很大的值，等价于「没有哪一层能把尾巴挡住」。</summary>
        private const float NoFurLayer = float.MaxValue;

        /// <summary>毛皮节点层号的查询结果，只查一次。</summary>
        private static float furLayer = NoFurLayer;

        private static bool furLayerResolved;

        /// <summary>画一格预览。</summary>
        internal static void Draw(
            Rect rect,
            GeneDef gene,
            BodyTypeDef bodyType,
            Rot4 rot,
            bool useOriginalModTextures,
            float height,
            float depth)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f, 0.6f));
            Widgets.DrawBox(rect, 1);

            Rect inner = rect.ContractedBy(5f);
            // 取较窄的一边换算：宽扁或瘦高的框都不会把小人挤出边界。
            float pxPerCell = Mathf.Min(inner.width, inner.height) / PreviewCells;

            Color previousColor = GUI.color;
            GUI.BeginGroup(inner);
            Vector2 center = new Vector2(inner.width / 2f, inner.height / 2f);

            // 中线：标记小人的身体中心，方便判断尾巴是偏身前还是偏身后。
            Widgets.DrawBoxSolid(new Rect(center.x - 0.5f, 0f, 1f, inner.height), CenterLineColor);

            PawnRenderNodeProperties props = gene == null ? null : FirstRenderProps(gene);
            int order = LayerOrder(props, rot, gene == null || bodyType == null);

            if (order == 0)
            {
                GUI.color = Color.white;
                DrawTail(props, gene, bodyType, rot, center, pxPerCell, height, depth);
            }

            GUI.color = BodyTint;
            DrawCellTexture(bodyType == null ? null : bodyType.bodyNakedGraphicPath, rot, center, BodyCells, pxPerCell);

            if (order == 1)
            {
                GUI.color = Color.white;
                DrawTail(props, gene, bodyType, rot, center, pxPerCell, height, depth);
            }

            if (bodyType != null)
            {
                GUI.color = FurTint;
                DrawCellTexture(
                    FurskinBodyTextures.PreviewFurPath(bodyType.defName, useOriginalModTextures),
                    rot,
                    center,
                    BodyCells,
                    pxPerCell);
            }

            if (order == 2)
            {
                GUI.color = Color.white;
                DrawTail(props, gene, bodyType, rot, center, pxPerCell, height, depth);
            }

            GUI.EndGroup();
            GUI.color = previousColor;

            Widgets.Label(
                new Rect(rect.x + 5f, rect.y + 2f, rect.width - 10f, 22f),
                FacingLabel(rot));
        }

        /// <summary>
        /// 算出尾巴图该插在身体这三层里的哪一层：0 在裸体之前（被身体挡住）、1 夹在裸体与毛皮之间、2 在所有层之上。
        ///
        /// 为什么要算这个：尾巴基因的作者是按「身体」这一层给尾巴定层号的，
        /// 例如 AFU 的狐狸尾在南、东、西三面写 1，在北面写 90。
        /// 偏偏毛绒种的毛皮是一个独立的 PawnRenderNode_Fur，原模组把它的 baseLayer 写成 5。
        /// 于是在游戏里，南、东、西三面的尾巴落在毛皮之下，身体轮廓以内那一截被毛皮盖住，只露出体外的一段；
        /// 只有北面的尾巴（90）盖在毛皮之上，整条连根部一起露出来。
        /// 预览如果不照这个层序画、一律把尾巴摆到最上面，就会把本该被挡住的那一截也画出来，
        /// 看上去像是「换个朝向就换了个接点」——那是在比较两份并不相同的画面。
        /// 把层序照抄过来，预览与游戏才是同一张图。
        /// </summary>
        private static int LayerOrder(PawnRenderNodeProperties props, Rot4 rot, bool tailAbsent)
        {
            if (tailAbsent || props == null)
            {
                // 没有尾巴可画，放最后与放最前都一样；用 2 让调用方少一次判断。
                return 2;
            }
            float tail = props.drawData == null ? props.baseLayer : props.drawData.LayerForRot(rot, props.baseLayer);
            if (tail < BodyLayer)
            {
                return 0;
            }
            return tail < FurLayer() ? 1 : 2;
        }

        /// <summary>
        /// 毛皮节点占用的层号，从 Furskin 基因自带的那个 PawnRenderNode_Fur 上读。
        /// 取不到时返回 <see cref="NoFurLayer"/>，表示预览里没有哪一层能挡住尾巴。
        ///
        /// 基因表在启动之后不再变化，所以查一次就缓存下来，拖动滑条时不再重复查表。
        /// </summary>
        private static float FurLayer()
        {
            if (furLayerResolved)
            {
                return furLayer;
            }
            furLayerResolved = true;
            furLayer = NoFurLayer;
            GeneDef furskin = DefDatabase<GeneDef>.GetNamedSilentFail(FurskinBodyTextures.FurskinDefName);
            if (furskin == null || furskin.renderNodeProperties == null)
            {
                return furLayer;
            }
            for (int i = 0; i < furskin.renderNodeProperties.Count; i++)
            {
                PawnRenderNodeProperties node = furskin.renderNodeProperties[i];
                if (node != null && node.nodeClass != null && node.nodeClass.Name == "PawnRenderNode_Fur")
                {
                    furLayer = node.baseLayer;
                    break;
                }
            }
            return furLayer;
        }

        /// <summary>按「原始坐标 + 玩家增量」把尾巴贴图画到身体上。</summary>
        private static void DrawTail(
            PawnRenderNodeProperties props,
            GeneDef gene,
            BodyTypeDef bodyType,
            Rot4 rot,
            Vector2 center,
            float pxPerCell,
            float height,
            float depth)
        {
            if (props == null || gene == null || bodyType == null || !IsVisibleInFacing(props, rot))
            {
                return;
            }
            string path = FirstTexPath(props);
            if (path.NullOrEmpty())
            {
                return;
            }
            if (TailOffsets.ArtworkPath(path, rot).NullOrEmpty())
            {
                return;
            }

            Vector3 offset = TailOffsets.EffectiveOffsetCells(gene, bodyType, rot, height, depth);
            Vector2 cells = props.drawSize;

            float width = cells.x * pxPerCell;
            float cellHeight = cells.y * pxPerCell;
            // 屏幕 y 轴向下，而坐标里的 z 轴向上，所以高度取负号。
            float centerX = center.x + offset.x * pxPerCell;
            float centerY = center.y - offset.z * pxPerCell;

            DrawLayer(
                path,
                rot,
                new Rect(centerX - width / 2f, centerY - cellHeight / 2f, width, cellHeight));
        }

        /// <summary>把一张多方向贴图按格子尺寸居中画出来。缺贴图时什么都不画。</summary>
        private static void DrawCellTexture(string path, Rot4 rot, Vector2 center, float cells, float pxPerCell)
        {
            float size = cells * pxPerCell;
            DrawLayer(path, rot, new Rect(center.x - size / 2f, center.y - size / 2f, size, size));
        }

        /// <summary>
        /// 按游戏规则画一层贴图：挑出该朝向实际会画的那张图，需要时水平翻转。
        ///
        /// 挑图与翻转都交给 <see cref="TailOffsets"/>，这里只负责画。
        /// 之所以不能简单地拼 `_朝向` 后缀：这批贴图只有 north、south、east 三种，没有 west，
        /// 而 14 条尾巴里有 8 条连 south 也没有。直接拼后缀会加载不到，
        /// 于是朝西与正面那两格什么都不画，看上去就像「换个朝向就变了位置」。
        /// 游戏实际是靠 Graphic_Multi 的回退与镜像把这两格补出来的，这里照做。
        /// </summary>
        private static void DrawLayer(string path, Rot4 rot, Rect rect)
        {
            string artwork = TailOffsets.ArtworkPath(path, rot);
            if (artwork.NullOrEmpty())
            {
                return;
            }
            Texture2D texture = ContentFinder<Texture2D>.Get(artwork, false);
            if (texture == null)
            {
                return;
            }
            if (TailOffsets.ArtworkMirrored(path, rot))
            {
                // 水平翻转：把 UV 的横向区间倒过来取，等价于左右镜像。
                // 朝西的小人正是用朝东那张图镜像着画的。
                GUI.DrawTextureWithTexCoords(rect, texture, new Rect(1f, 0f, -1f, 1f));
            }
            else
            {
                GUI.DrawTexture(rect, texture);
            }
        }

        /// <summary>
        /// 该朝向是否会被画出来。
        /// visibleFacing 为空表示不限制朝向（原版对空值的处理就是不筛），此时视为可见。
        /// </summary>
        private static bool IsVisibleInFacing(PawnRenderNodeProperties props, Rot4 rot)
        {
            return props.visibleFacing == null || props.visibleFacing.Count == 0 || props.visibleFacing.Contains(rot);
        }

        private static PawnRenderNodeProperties FirstRenderProps(GeneDef gene)
        {
            if (gene.renderNodeProperties == null || gene.renderNodeProperties.Count == 0)
            {
                return null;
            }
            return gene.renderNodeProperties[0];
        }

        /// <summary>
        /// 取预览用的贴图路径。作者可以写多条（游戏里按小人的种子随机挑一条），
        /// 预览固定用第一条，这样同一张预览不会每次重画都换样子。女性专用那组不参与，同样是为了稳定。
        /// </summary>
        private static string FirstTexPath(PawnRenderNodeProperties props)
        {
            if (!props.texPaths.NullOrEmpty())
            {
                return props.texPaths[0];
            }
            return props.texPath;
        }

        private static string FacingLabel(Rot4 rot)
        {
            if (rot == Rot4.North)
            {
                return "FurskinPreviewFacingNorth".Translate();
            }
            if (rot == Rot4.East)
            {
                return "FurskinPreviewFacingEast".Translate();
            }
            if (rot == Rot4.West)
            {
                return "FurskinPreviewFacingWest".Translate();
            }
            return "FurskinPreviewFacingSouth".Translate();
        }
    }
}
