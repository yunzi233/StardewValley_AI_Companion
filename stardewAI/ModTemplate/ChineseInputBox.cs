// ============================================================
//  ChineseInputBox.cs —— 中文 IME 输入框（最小原型）
//  职责：只维护开关状态和文本内容；绘制（RenderedHud）、按键分发
//  （ButtonPressed）、字符接收（Window.TextInput）都在 ModEntry.cs。
// ============================================================
namespace StardewAIAssistant
{
    /// <summary>
    /// 中文 IME 输入框的最小实现。
    /// 字符来源：MonoGame 的 GameWindow.TextInput 事件（SDL_TEXTINPUT 链路）——
    /// 中文 IME 候选确认后每个字符触发一次事件，经 ModEntry 过滤控制字符后 Append 进来。
    /// </summary>
    public class ChineseInputBox
    {
        /// <summary>输入框是否打开。打开期间所有按键会被 Suppress（角色不移动）。</summary>
        public bool IsOpen { get; private set; }

        /// <summary>当前输入的文本（支持中文）。</summary>
        public string Text { get; private set; } = "";

        /// <summary>打开输入框并清空上次内容。</summary>
        public void Open()
        {
            IsOpen = true;
            Text = "";
        }

        /// <summary>关闭输入框并清空文本（Esc 关闭 / 回车提交后调用）。</summary>
        public void Close()
        {
            IsOpen = false;
            Text = "";
        }

        /// <summary>追加一个字符（由 TextInput 事件调用，控制字符已在 ModEntry 过滤）。</summary>
        public void Append(char c) => Text += c;

        /// <summary>删除末尾一个字符（Backspace）。</summary>
        public void Backspace()
        {
            if (Text.Length > 0)
                Text = Text[..^1];   // C# 范围语法：去掉最后一个字符
        }

        /// <summary>清空文本，但保持输入框打开状态。</summary>
        public void Clear() => Text = "";
    }
}
