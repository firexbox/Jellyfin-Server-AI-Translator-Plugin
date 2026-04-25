# Jellyfin AI Translator Plugin

Jellyfin 服务端 AI 字幕翻译插件

## 功能
- 支持 DeepSeek / OpenAI / Ollama 多种 AI 提供商
- 三种字幕模式：原始备份、翻译字幕、双语字幕
- 后台异步任务，避免 HTTP 超时
- AI 智能语言检测
- 实时翻译进度监控

## 安装

### Docker / Linux
1. 创建插件目录：`plugins/AITranslator/`
2. 复制 `Jellyfin.Plugin.AITranslator.dll` 到该目录
3. 重启 Jellyfin 服务器
4. 在控制台 -> 插件 -> AI Translator 中配置 API Key

### macOS (ARM / Intel)
1. 完全退出 Jellyfin（包括菜单栏图标）
2. 找到插件目录：
   - 官方 .app: `~/Library/Application Support/jellyfin/plugins/`
   - Homebrew: `~/.local/share/jellyfin/plugins/`
3. 创建子目录 `AITranslator/`
4. 将 `Jellyfin.Plugin.AITranslator.dll` 复制进去
5. 重新启动 Jellyfin
6. 控制台 -> 插件 -> AI Translator 配置 API Key

### Windows
1. 完全退出 Jellyfin
2. 找到插件目录：`%LOCALAPPDATA%\jellyfin\plugins\`
3. 创建子目录 `AITranslator\`
4. 将 `Jellyfin.Plugin.AITranslator.dll` 复制进去
5. 重新启动 Jellyfin

## 验证安装
打开 Jellyfin 控制台，左侧菜单应出现 **AI Translator** 选项。
点击后在设置区域点击"测试 API"按钮，显示【OK】即表示安装成功。

## 系统要求
- Jellyfin 10.11+
- .NET 9.0 Runtime

## 版本
1.0.1 - 支持 Linux（10.11.8版测试通过） / macOS ARM（10.11.8版测试通过） / Windows(未测试) 三平台
