"""LLM 工厂：根据 config.json 动态创建对话模型。"""
import json

from langchain_ollama import ChatOllama
from langchain_openai import ChatOpenAI

# 路径统一到 aipy.config（开发模式/打包模式共用同一套逻辑：
# 开发模式 → backend/config.json，打包模式 → exe 同级 config.json）
from aipy.config import CONFIG_FILE

# OpenAI 兼容接口的厂商预设表
PROVIDERS = {
    "deepseek": "https://api.deepseek.com/v1",
    "zhipu":    "https://open.bigmodel.cn/api/paas/v4",
    "qwen":     "https://dashscope.aliyuncs.com/compatible-mode/v1",
    "kimi":     "https://api.moonshot.cn/v1",
}


def load_config() -> dict:
    if not CONFIG_FILE.exists():
        raise FileNotFoundError(
            f"缺少配置文件 {CONFIG_FILE}\n"
            f"请复制 config.example.json 为 config.json 并填写 api_key。"
        )
    return json.loads(CONFIG_FILE.read_text(encoding="utf-8"))


def save_config(cfg: dict) -> None:
    """把配置写入 config.json（覆盖写，UTF-8，缩进 2）。
       供 /config 接口调用，C# 端从游戏里推送的配置落盘用。"""
    CONFIG_FILE.write_text(
        json.dumps(cfg, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def create_llm(cfg: dict):
    provider = cfg.get("provider", "ollama")
    # temperature 默认 0.7；越大回答越随机，越小越稳定（范围 0.0-2.0）。
    # 老的 config.json 没有该字段时自动回落到默认值，向后兼容。
    temperature = float(cfg.get("temperature", 0.7))

    if provider == "ollama":
        return ChatOllama(
            model=cfg.get("model", "qwen2.5:3b"),
            base_url=cfg.get("base_url", "http://127.0.0.1:11434"),
            temperature=temperature,  # 可配置；原来是硬编码 0.7
        )

    if provider not in PROVIDERS:
        raise ValueError(f"不支持的 provider: {provider}，可选：ollama / {list(PROVIDERS)}")

    api_key = cfg.get("api_key", "").strip()
    if not api_key:
        raise ValueError(f"provider={provider} 时必须填写 api_key")

    return ChatOpenAI(
        model=cfg["model"],
        api_key=api_key,
        base_url=PROVIDERS[provider],
        temperature=temperature,  # 可配置；原来是硬编码 0.7
    )