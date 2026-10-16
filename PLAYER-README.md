# 星露谷 AI 伴侣 Mod — 安装说明

## 你需要什么

1. 星露谷物语
2. SMAPI
3. GenericModConfigMenu

## 安装步骤

1. 先装 SMAPI（双击安装器，一路下一步）
2. 把 GMCM 解压到 `Mods/GenericModConfigMenu/`
3. 把本压缩包里的 **StardewAIAssistant** 文件夹解压到 `Mods/`
4. 进游戏 → ESC → Mod 选项 → Stardew AI Assistant
5. 填三项：
   - **AI 厂商**
   - **API Key**:如果使用本地模型无需填APIkey
   - **模型名**
6. 点保存 → 关游戏 → 重新进游戏 → 读档
7. 看到聊天框提示 **"AI 伴侣已上线！"** → 安装成功

## 怎么用

- 按 **F7**：打开中文输入框，打字提问
- 按 **T**：原版聊天框，输入 `AI 你的问题`
- 例：海莉喜欢什么礼物 / 春天种什么赚钱 / 我现在该干嘛

## 常见问题

| 现象 | 原因与解决 |
|---|---|
| 没提示"已上线" | 后端启动慢，等 30 秒；还不行看 `Mods/StardewAIAssistant/backend/backend.log` |
| 提示"没配置 AI 厂商" | ESC → Mod 选项里填 API Key |
| 提示"后端未启动" | 杀毒软件可能拦截了 backend.exe，去杀软里加白名单 |
| 游戏崩溃 | 先移除 Mod 试试；把报错发到 [issue 链接占位] |

## 数据来源

- 知识库来自星露谷物语 Wiki（CC BY-NC-SA 4.0）
- 本项目开源，遵循 MIT 协议