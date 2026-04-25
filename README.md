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

## 使用方法

### 1. 配置 API Key
进入 Jellyfin 控制台 -> 插件 -> AI Translator：
- **API 提供商**: 选择 DeepSeek / OpenAI / Ollama
- **API 密钥**: 填写你的 API Key（以 sk- 开头）
- **模型名称**: 默认 `deepseek-chat`，OpenAI 可填 `gpt-4o`
- **目标语言**: 选择需要翻译成的语言（如 中文）
- **批量大小**: 默认 10，每批翻译的字幕条目数

点击"保存"，然后点击"测试 API"验证连接。

### 2. 搜索视频
在配置页面下方的搜索框中输入视频名称（支持电影和剧集），
点击"搜索"按钮。结果列表会显示匹配的视频。

### 3. 选择字幕并翻译
点击搜索结果中的视频卡片，页面会加载该视频的所有字幕轨道。
每个字幕轨道显示：
- 语言标签（AI 智能检测）
- 编码格式（srt / ass 等）
- 来源（内嵌 / 外部）

每个字幕轨道有三个操作按钮：

| 按钮 | 说明 |
|------|------|
| [原] 原字幕 | 将原字幕备份为 `.original.srt`，不做翻译 |
| [译] 目标语言 | 翻译为目标语言，生成 `.chi.srt` 等 |
| [双] 目标语言-源语言 | 生成双语字幕 `.chi-en.srt`，上行原文下行译文 |

点击按钮后，翻译任务在后台启动，页面底部显示进度面板。

### 4. 使用翻译后的字幕
翻译完成后：
1. 进入 Jellyfin 媒体库，找到该视频
2. 点击"刷新元数据"或等待自动扫描
3. 播放视频时，点击字幕按钮选择新添加的字幕轨道
4. 翻译后的字幕会作为新的外部字幕出现在列表中

### 5. 查看翻译进度
页面底部有全局进度面板，显示：
- **[RUN]** 正在运行的任务
- **[OK]** 最近完成的任务
- **[ERR]** 失败的任务

面板每 10 秒自动刷新，也可以手动刷新页面查看最新状态。

## 字幕文件说明
翻译完成后，字幕文件保存在视频同目录下：
- `.original.srt` — 原始字幕备份
- `.chi.srt` / `.jpn.srt` 等 — 翻译后的字幕
- `.chi-en.srt` — 双语字幕（中文在上，英文在下）

## 常见问题

### 点击翻译后提示"API 错误 (401)"
API Key 未配置或已过期。检查配置页面的 API 密钥是否填写正确。

### 字幕列表显示"没有可翻译的字幕"
该视频没有内嵌字幕，也没有外部字幕文件。需要先有字幕才能翻译。

### 翻译后的字幕没有出现在播放器的字幕列表中
Jellyfin 需要重新扫描媒体库才能识别新添加的外部字幕文件。
进入视频详情页，点击"刷新元数据"。

### macOS 上安装后所有 API 返回 404
确保使用的是 v1.0.1+ 版本，该版本修复了 macOS 上 MVC 控制器注册问题。
如果仍有问题，检查 Jellyfin 日志确认插件已加载：
```
cat ~/Library/Application\ Support/jellyfin/log/log_*.log | grep "AI Translator"
```

## 系统要求
- Jellyfin 10.11+
- .NET 9.0 Runtime

## 版本
1.0.1 - 支持 Linux（docker jellyfin/jellyfin:10.11.8版测试通过） / macOS ARM（10.11.8版测试通过） / Windows(未测试) 三平台
