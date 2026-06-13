# Jellyfin AI Translator Plugin

> session_id: jellyfin服务器端ai翻译插件开发

Jellyfin 服务端 AI 翻译字幕插件，支持实时翻译和手动翻译两种模式。

## 功能

- **实时翻译**：播放时自动将外文字幕翻译为中文
- **手动翻译**：选择已有字幕文件进行翻译并保存为新轨道
- **多 AI 后端**：支持 DeepSeek、OpenAI 兼容 API、Ollama 本地模型
- **Jellyfin 原生配置 UI**：在 Jellyfin 管理界面中配置

## 技术栈

- .NET 8 / C#
- Jellyfin Plugin API (MediaBrowser.Model, MediaBrowser.Controller)
- HttpClient + System.Text.Json
