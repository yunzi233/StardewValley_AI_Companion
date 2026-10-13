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
    }
}
