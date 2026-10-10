"""LLM 工厂：根据 config.json 动态创建对话模型。"""
import json
from pathlib import Path

from langchain_ollama import ChatOllama
from langchain_openai import ChatOpenAI

# OpenAI 兼容接口的厂商预设表
PROVIDERS = {
    "deepseek": "https://api.deepseek.com/v1",
    "zhipu":    "https://open.bigmodel.cn/api/paas/v4",
    "qwen":     "https://dashscope.aliyuncs.com/compatible-mode/v1",
    "kimi":     "https://api.moonshot.cn/v1",
}

CONFIG_PATH = Path(__file__).parent / "config.json"


def load_config() -> dict:
    if not CONFIG_PATH.exists():
        raise FileNotFoundError(
            f"缺少配置文件 {CONFIG_PATH}\n"
            f"请复制 config.example.json 为 config.json 并填写 api_key。"
        )
    return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))


def create_llm(cfg: dict):
    provider = cfg.get("provider", "ollama")

    if provider == "ollama":
        return ChatOllama(
            model=cfg.get("model", "qwen2.5:3b"),
            base_url=cfg.get("base_url", "http://127.0.0.1:11434"),
            temperature=0.7,
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
        temperature=0.7,
    )