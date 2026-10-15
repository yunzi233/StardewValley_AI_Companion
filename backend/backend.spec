# -*- mode: python ; coding: utf-8 -*-
"""backend.spec — PyInstaller 打包配置（onedir 模式）。

用法：在 backend/ 下执行  uv run pyinstaller backend.spec --noconfirm
产物：dist/backend/backend.exe（+ 同级的一堆 dll/pyd，onedir 形态）

设计要点（路线 2 玩家零环境依赖）：
- onedir 而非 onefile：onefile 每次启动要解压到临时目录，慢 + 杀软误报率高
- 数据文件（chroma_db / models / wiki_data / config.json）不打进 exe，
  放 exe 同级目录 —— config.py 的 PROJECT_ROOT 在打包模式下指向
  sys.executable 所在目录，exe 同级就是"项目根"
- 不用 collect_all：会把测试数据和冗余资源塞进包里，体积爆炸
"""

from PyInstaller.utils.hooks import collect_submodules, collect_dynamic_libs, collect_data_files

# ---- onnxruntime 的原生 DLL（.dll/.pyd）：fastembed 推理全靠它，必须随包 ----
onnxruntime_binaries = collect_dynamic_libs("onnxruntime")

# ---- fastembed 的非 .py 资源（模型注册表、默认配置等元数据）----
fastembed_data = collect_data_files("fastembed")

# ---- aipy 项目包的全部子模块（config / llm_factory / agent / embeddings...）----
# PyInstaller 静态分析能抓到大部分，这里兜底保证一个不漏
aipy_hidden = collect_submodules("aipy")

# ---- chromadb 全部子模块 ----
# chromadb 的各组件（api 实现 / segment / db / 遥测）都是运行时用
# importlib.import_module("chromadb.xxx.Yyy") 按 config 字符串动态加载的，
# 静态分析一个都跟不到（第 1 轮打包实测挂在 chromadb.api.rust）。
# 单个 hiddenimports 补不完，直接收集全部子模块（只收 .py，体积可控）。
chromadb_hidden = collect_submodules("chromadb")

a = Analysis(
    ["main.py"],              # 入口：main.py 末尾的 __main__ 块会调 uvicorn.run
    pathex=["src"],           # 让 PyInstaller 能找到 src/ 下的 aipy 包
    binaries=onnxruntime_binaries,
    datas=fastembed_data,
    hiddenimports=[
        # ---- uvicorn 的动态导入点：运行时按配置用 importlib 挑选实现，
        #      静态分析跟不到，必须显式列出（.auto 内部再挑下面这些）----
        "uvicorn.logging",
        "uvicorn.loops.auto",
        "uvicorn.loops.asyncio",              # Linux/mac 用 epoll/kqueue；Windows 用 proactor
        "uvicorn.protocols.http.auto",
        "uvicorn.protocols.http.h11_impl",    # httptools 未装时的 HTTP 实现（本项目场景）
        "uvicorn.protocols.websockets.auto",
        "uvicorn.lifespan.on",
        # ---- LangChain 生态：个别模块经 importlib 延迟加载，静态分析会漏 ----
        "langchain",
        "langchain_core",
        "langchain_ollama",
        "langchain_openai",
        "langchain_chroma",
        "langchain_text_splitters",
        "langgraph",
        "langgraph.checkpoint",
        "langgraph.checkpoint.sqlite",        # 对话历史 SQLite checkpointer
        "aiosqlite",                          # AsyncSqliteSaver 底层驱动
        # ---- 向量库 / 嵌入 ----
        "fastembed",
        "onnxruntime",
        "chromadb",
        "chromadb.telemetry.product.posthog", # chromadb 懒加载的遥测模块，运行时 import 会炸
    ] + aipy_hidden + chromadb_hidden,
    hookspath=[],
    runtime_hooks=[],
    excludes=[
        # 排除确定不用的重型库，减小体积（报 ModuleNotFoundError 再移回来）
        "tkinter",        # GUI 工具包，后端用不到
        "matplotlib",     # 绘图，用不到
    ],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,    # onedir：二进制放外面，exe 只含引导器
    name="backend",           # 产物名 → dist/backend/backend.exe
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,                # 不 UPX 压缩：现代杀软对 UPX 壳误报率极高
    console=True,             # 保留控制台窗口：第一阶段先看日志，稳定后再隐藏
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    name="backend",           # dist/backend/ 目录
)
