using System;
using HarmonyLib;
using StardewValley.Menus;

namespace StardewAIAssistant
{
    /// <summary>
    /// Harmony 补丁：拦截原版聊天框的消息提交方法 ChatBox.textBoxEnter(string)。
    ///
    /// 调用链（通过反射游戏 1.6.15 的 IL 已确认）：
    ///   玩家按回车
    ///     → ChatBox.textBoxEnter(TextBox sender)   // 由输入框的 OnEnterPressed 事件触发
    ///       → ChatBox.textBoxEnter(string text)    // 真正的发送入口：
    ///       │                                     //   "/" 开头执行 runCommand（原版命令）
    ///       │                                     //   否则 sendChatMessage / receiveChatMessage（聊天）
    ///       → ChatTextBox.reset()                 // 无论上面是否发消息，都会清空输入框
    ///       → ChatBox.clickAway()                 // 并关闭聊天框
    ///
    /// 因此只需在 string 重载上做 Prefix：
    ///   - "AI " 开头：返回 false 跳过原版发送（消息不会发给其他玩家、也不会显示在头顶），
    ///     输入框的清空/关闭由外层调用方照常完成，无需手动处理；
    ///   - 其他消息：返回 true，一切照原版走。
    /// </summary>
    [HarmonyPatch(typeof(ChatBox), nameof(ChatBox.textBoxEnter), new[] { typeof(string) })]
    internal static class ChatBoxTextBoxEnterPatch
    {
        /// <summary>触发 AI 对话的消息前缀（A、I 两个大写字母 + 一个半角空格）。</summary>
        private const string AiPrefix = "AI ";

        /// <summary>
        /// 前置补丁。返回 true 表示继续执行原版方法；返回 false 表示跳过原版方法。
        /// </summary>
        /// <param name="text_to_send">原版参数名：玩家在聊天框里提交的整行文本。</param>
        public static bool Prefix(string text_to_send)
        {
            // 去掉首尾空白后再判断，玩家手滑多打空格也能识别
            string text = (text_to_send ?? string.Empty).Trim();

            // 不是 "AI " 开头：普通聊天/原版命令，一律放行
            if (!text.StartsWith(AiPrefix, StringComparison.Ordinal))
                return true;

            // 提取 "AI " 后面的问题正文，并再 Trim 一次（如 "AI  海莉..." 两个空格的情况）
            string question = text.Substring(AiPrefix.Length).Trim();

            // 只输入了 "AI" 或 "AI "（没有实际问题）：吞掉本次提交，给个用法提示，不请求后端
            if (question.Length == 0)
            {
                ModEntry.Instance?.EnqueueChatMessageFromPatch(
                    "AI伴侣：用法是 “AI + 空格 + 你的问题”，例如：AI 海莉喜欢什么礼物");
                return false;
            }

            // 转交给 ModEntry：收集游戏状态 → 异步请求 Python 后端 → 回主线程在聊天框显示回复
            ModEntry.Instance?.HandleAiChatQuestion(question);

            // 返回 false：这条消息不走原版聊天发送/本地头顶显示
            return false;
        }
    }
}
