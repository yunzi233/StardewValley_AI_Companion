// ============================================================
// 浮动精灵（第一版占位实现）：
//   - 不加载任何图片素材，用 Game1.staminaRect（游戏自带的 1x1 白色像素）
//     放大画一个 16x16 方块；后续换成真实贴图时只需替换 Draw 里的纹理参数。
//   - 每帧 Update 推进浮动相位，Draw 里用 sin 曲线实现上下浮动。
// ============================================================
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace StardewAIAssistant
{
    public class FloatingCompanion
    {
        /// <summary>相对玩家的偏移（玩家右侧、头顶上方），单位：世界像素。</summary>
        private static readonly Vector2 OffsetFromPlayer = new Vector2(40f, -70f);

        /// <summary>
        /// 精灵贴图（assets/robot.png），由 ModEntry 在启动时加载并注入；
        /// 为 null（素材缺失）时 Draw 里自动回退到白色方块占位，不崩溃。
        /// </summary>
        public Texture2D? Texture { get; set; }

        /// <summary>浮动动画相位（每次 Update 累加，用于 sin 曲线）。</summary>
        private float _bobPhase = 0f;

        /// <summary>每帧更新：推进浮动动画相位（3f = 每秒约半圈，浮动节奏适中）。</summary>
        public void Update()
        {
            _bobPhase += (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds * 3f;
        }

        /// <summary>在世界层绘制：跟随玩家、上下浮动。</summary>
        public void Draw(SpriteBatch b)
        {
            if (!Context.IsWorldReady || Game1.player is null) return;

            // 1. 玩家世界坐标 + 偏移。
            //    世界坐标是"地图上的绝对位置"（单位：像素），玩家移动时它跟着变，
            //    但不随镜头滚动而变。
            Vector2 worldPos = Game1.player.getStandingPosition() + OffsetFromPlayer;

            // 2. 上下浮动（sin 曲线，幅度 4 像素）：给 _bobPhase 套一个正弦函数，
            //    让方块在基准点附近平滑地上下摆动，模拟"漂浮"感。
            worldPos.Y += (float)Math.Sin(_bobPhase) * 4f;

            // 3. 世界坐标 → 屏幕坐标的转换原理：
            //    游戏画面是"镜头"看世界的一个窗口，玩家移动时镜头跟着滚，
            //    同一个世界坐标在屏幕上的位置会随镜头移动而变化。
            //    Game1.GlobalToLocal(world) 就是把绝对的世界坐标减去镜头左上角的
            //    世界位置，得到"该点在当前屏幕上的像素位置"（本地坐标）。
            //    绘制必须用屏幕坐标，否则方块会固定在地图上而不跟随镜头。
            Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, worldPos);

            // 4. 绘制精灵：
            //    - 有贴图（assets/robot.png）→ 按**贴图实际尺寸** 1:1 绘制（不缩放，
            //      像素最清晰；以后换任何尺寸的图都不用改代码），Color.White 不染色
            //    - 贴图缺失（Texture 为 null）→ 回退到 Game1.staminaRect 放大画 16x16
            //      浅蓝方块占位（游戏自带的 1x1 白点纹理），保证任何情况下都能画出来
            //    深度 0.9f：layerDepth 越大越后绘制（越靠前），0.9 能盖住地面和
            //    大部分场景物件，但仍会被玩家/NPC（深度更大）遮挡。
            bool hasTexture = Texture is not null;
            b.Draw(
                hasTexture ? Texture! : Game1.staminaRect,
                hasTexture
                    ? new Rectangle((int)screenPos.X, (int)screenPos.Y, Texture!.Width, Texture!.Height)
                    : new Rectangle((int)screenPos.X, (int)screenPos.Y, 16, 16),
                null,
                hasTexture ? Color.White : Color.LightSkyBlue,
                0f,
                Vector2.Zero,
                SpriteEffects.None,
                0.9f
            );
        }
    }
}
