# -*- coding: utf-8 -*-
"""组装面向玩家的发布包：release/StardewAIAssistant/ → StardewAIAssistant-v0.9.zip

用法（在项目根或任意目录都行，路径全部绝对化）：
    python tools/build_release.py

前置条件：
    1. mod/ModTemplate 已编译（bin/Debug/net6.0/StardewAIAssistant.dll 存在）
    2. backend 已 PyInstaller 打包（backend/dist/backend/backend.exe 存在）
       → cd backend; uv run pyinstaller backend.spec --noconfirm
"""
import shutil
import sys
import zipfile
from pathlib import Path

# 项目根（脚本在 tools/ 下，parents[0]=tools/, parents[1]=项目根）
PROJECT_ROOT = Path(__file__).resolve().parents[1]

# ---- 路径常量 ----
MOD_DIR = PROJECT_ROOT / "mod" / "ModTemplate"
MOD_BIN_DIR = MOD_DIR / "bin" / "Debug" / "net6.0"
BACKEND_DIR = PROJECT_ROOT / "backend"
BACKEND_DIST_DIR = BACKEND_DIR / "dist" / "backend"          # PyInstaller onedir 产物
RELEASE_DIR = PROJECT_ROOT / "release"
PACKAGE_DIR = RELEASE_DIR / "StardewAIAssistant"             # zip 内的根文件夹
ZIP_PATH = RELEASE_DIR / "StardewAIAssistant-v0.9.zip"


def fail(msg: str):
    """明确报错并退出（指出缺哪个路径，玩家侧排障友好）"""
    print(f"\n[错误] {msg}")
    sys.exit(1)


def check_prerequisites():
    """前置检查：所有源文件/目录必须存在，缺哪个报哪个"""
    print("[1/6] 前置检查...")
    required_files = [
        (MOD_DIR / "manifest.json", "mod/ModTemplate/manifest.json"),
        (MOD_DIR / "assets", "mod/ModTemplate/assets/"),
        (MOD_BIN_DIR / "StardewAIAssistant.dll",
         "mod/ModTemplate/bin/Debug/net6.0/StardewAIAssistant.dll（请先编译 Mod：dotnet build -p:EnableModDeploy=false）"),
        (BACKEND_DIST_DIR / "backend.exe",
         "backend/dist/backend/backend.exe（请先运行：cd backend; uv run pyinstaller backend.spec --noconfirm）"),
        (BACKEND_DIR / "chroma_db", "backend/chroma_db/（向量库，随包分发）"),
        (BACKEND_DIR / "models", "backend/models/（嵌入模型缓存，随包分发）"),
        (BACKEND_DIR / "wiki_data", "backend/wiki_data/（知识库源文件，随包分发）"),
        (BACKEND_DIR / "config.example.json", "backend/config.example.json"),
        (PROJECT_ROOT / "PLAYER-README.md", "PLAYER-README.md"),
    ]
    for path, hint in required_files:
        if not path.exists():
            fail(f"缺少：{hint}")
    print("  所有源文件就绪")


def clean_release():
    """清空 release/ 目录（幂等：重复执行结果一致）"""
    print("[2/6] 清理 release/ 目录...")
    if RELEASE_DIR.exists():
        shutil.rmtree(RELEASE_DIR)
    RELEASE_DIR.mkdir(parents=True)
    PACKAGE_DIR.mkdir()
    print(f"  {RELEASE_DIR} 已重建")


def copy_mod_files():
    """复制 Mod 文件：manifest.json + assets/ + DLL（+ deps.json 若有，不带 pdb）"""
    print("[3/6] 复制 Mod 文件...")
    shutil.copy2(MOD_DIR / "manifest.json", PACKAGE_DIR / "manifest.json")
    shutil.copytree(MOD_DIR / "assets", PACKAGE_DIR / "assets")
    shutil.copy2(MOD_BIN_DIR / "StardewAIAssistant.dll", PACKAGE_DIR / "StardewAIAssistant.dll")
    # deps.json 有就带上（依赖清单，SMAPI 加载用）；pdb 不带（调试符号，发布包保持干净）
    deps = MOD_BIN_DIR / "StardewAIAssistant.deps.json"
    if deps.exists():
        shutil.copy2(deps, PACKAGE_DIR / "StardewAIAssistant.deps.json")
        print("  + StardewAIAssistant.deps.json")
    print(f"  manifest.json + assets/ + StardewAIAssistant.dll")


def copy_backend():
    """复制后端：PyInstaller 产物 + 运行时数据（不带 config.json / data/ / *.log）"""
    print("[4/6] 复制后端...")
    dest_backend = PACKAGE_DIR / "backend"

    # PyInstaller onedir 产物（含 _internal/，玩家侧零依赖）
    shutil.copytree(BACKEND_DIST_DIR, dest_backend)
    print("  + backend.exe + _internal/（PyInstaller 产物）")

    # 运行时数据（覆盖/补充到 dest_backend 下）。
    # 注意：dist/backend/ 里可能残留之前"模拟发布形态"手动拷进去的 chroma_db/models/ 等，
    # 先删掉目标同名目录再 copytree，避免 FileExistsError。
    for data_name in ["chroma_db", "models", "wiki_data"]:
        target = dest_backend / data_name
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(BACKEND_DIR / data_name, target)
        label = {"chroma_db": "向量库", "models": "嵌入模型缓存", "wiki_data": "知识库源文件"}[data_name]
        print(f"  + {data_name}/（{label}）")
    shutil.copy2(BACKEND_DIR / "config.example.json", dest_backend / "config.example.json")
    print("  + config.example.json（配置模板，玩家首次保存后由后端生成真实 config.json）")

    # 安全兜底：发布包里绝不允许带真实 API Key / 对话历史 / 运行日志
    for banned in ["config.json", "backend.log"]:
        banned_path = dest_backend / banned
        if banned_path.exists():
            banned_path.unlink()
            print(f"  [清理] 移除意外带入的 {banned}")
    data_dir = dest_backend / "data"
    if data_dir.exists():
        shutil.rmtree(data_dir)
        print("  [清理] 移除意外带入的 data/（对话历史）")


def copy_player_readme():
    """玩家 README 重命名为包内 README.md（面向玩家，和开发者 README.md 区分）"""
    print("[5/6] 复制玩家 README...")
    shutil.copy2(PROJECT_ROOT / "PLAYER-README.md", PACKAGE_DIR / "README.md")
    print("  PLAYER-README.md → StardewAIAssistant/README.md")


def make_zip():
    """手动 zipfile（不用 shutil.make_archive：要精确控制 zip 内路径和压缩算法）"""
    print("[6/6] 压缩为 zip...")
    total_files = 0
    total_size = 0
    with zipfile.ZipFile(ZIP_PATH, "w", zipfile.ZIP_DEFLATED) as zf:
        for file_path in sorted(PACKAGE_DIR.rglob("*")):
            if file_path.is_file():
                # zip 内路径保留 "StardewAIAssistant/..." 这一层（解压即得 Mod 文件夹）
                arcname = file_path.relative_to(RELEASE_DIR)
                zf.write(file_path, arcname)
                total_files += 1
                total_size += file_path.stat().st_size

    zip_size = ZIP_PATH.stat().st_size
    print("\n" + "=" * 50)
    print("打包完成")
    print(f"  包路径：    {ZIP_PATH}")
    print(f"  文件数：    {total_files}")
    print(f"  未压缩大小：{total_size / 1024 / 1024:.1f} MB")
    print(f"  压缩后大小：{zip_size / 1024 / 1024:.1f} MB")
    print("=" * 50)


def main():
    check_prerequisites()
    clean_release()
    copy_mod_files()
    copy_backend()
    copy_player_readme()
    make_zip()


if __name__ == "__main__":
    main()
