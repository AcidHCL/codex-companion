AI Pet：可选本地参考音色服务

默认 faster-qwen3-tts，复用 Qwen3-TTS-12Hz-1.7B-Base 和本目录 reference.wav。使用x-vector说话人特征模式，不训练或微调，不额外变速或移调。
环境与权重需另装，参见根目录“便携语音说明.txt”。

安装
Python 3.12、兼容NVIDIA GPU和驱动；加载检查至少7GiB空闲显存。
setup-fast.ps1 -PythonExe <Python路径> -WorkspaceRoot <安装根目录> -DownloadModel
独立环境为 .tools/deepseek-voice-fast-venv，模型为 .tools/deepseek-voice-models/Qwen3-TTS-12Hz-1.7B-Base。
旧后端用 setup.ps1 安装到独立 deepseek-voice-venv。依赖见 requirements-fast-lock.txt / requirements-lock.txt。
安装联网下载公开依赖与模型；运行时离线。不会修改或自动卸载Ollama。

配置与使用
config.json 默认 backend=faster，model_path为相对路径，reference_path=reference.wav；默认模型位置支持向上查找 .tools。
安装脚本仅在安装者电脑写入实际路径；不要把安装后的路径配置、status.json或下载缓存提交到仓库。
参考人声需要来自有权使用的录音；未提供参考文件时只影响语音播报。
看板提供开关、预加载、卸载、试听、停止与音量。打开聊天可预热并保持模型；离开后空闲60秒释放。手动卸载优先，下次主动请求再加载。
流式按音频块输出完整句段，自然停顿、冷启动和硬件负载仍会影响响应时间；歌曲与口语的听感不能保证完全一致。

本机协议
服务由桌宠启动和回收，只监听127.0.0.1，默认18766端口。随机令牌通过父子进程管道传递，所有接口要求 X-AIPet-Voice-Token。
拒绝网页Origin与非本机Host，不提供远程URL抓取或任意文件读取。运行时不记录对话正文，不上传文本或声音。
GET /health；POST /preload、/unload、/retention、/cancel。
POST /v1/audio/stream 返回NDJSON：format、audio、done；必须收到done才能视为正常结束。
POST /v1/audio/speech 返回兼容PCM WAV。faster卸载时退出自己的工作进程，下次请求重新启动；父进程结束时服务也退出。

来源
https://github.com/QwenLM/Qwen3-TTS
https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base
https://github.com/andimarafioti/faster-qwen3-tts
保留 THIRD-PARTY.txt 及其作者、许可信息；软件许可不授予参考录音的分发权利。
