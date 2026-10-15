namespace StardewAIAssistant
{
    /// <summary>玩家通过 GMCM 或手动编辑 ModConfig.json 配置的 AI 厂商参数。</summary>
    public class ModConfig
    {
        /// <summary>AI 厂商：deepseek / qwen / zhipu / kimi / ollama</summary>
        public string Provider { get; set; } = "deepseek";

        /// <summary>云端厂商 API Key；本地 Ollama 留空</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>模型名，如 deepseek-chat / qwen-plus / glm-4-flash / qwen2.5:3b</summary>
        public string Model { get; set; } = "deepseek-chat";

        /// <summary>回答温度 0.0-2.0：越高越有创意但越容易胡说，推荐 0.5-0.9</summary>
        public float Temperature { get; set; } = 0.7f;

        /// <summary>backend 目录的绝对路径（含 main.py 和 pyproject.toml），用于自动拉起后端</summary>
        public string BackendDir { get; set; } = "";

        /// <summary>游戏启动时是否自动拉起后端（需 BackendDir 已填）</summary>
        public bool AutoStartBackend { get; set; } = true;
    }
}
