AI Pet：可选本地语音输入

faster-whisper + 多语言 Whisper small / CTranslate2，CPU int8运行，不占GPU，不训练或微调。
Python环境和模型需要另装，参见根目录“便携语音说明.txt”。
setup.ps1 -PythonExe <Python3.12路径> -WorkspaceRoot <安装根目录> -DownloadModel
环境为 .tools/deepseek-asr-venv，模型为 .tools/deepseek-asr-models/faster-whisper-small。
config.json默认相对路径支持向上查找 .tools。不要把安装后的本机路径配置、下载记录、venv或缓存提交仓库。
依赖版本见 requirements-lock.txt，来源与许可见 THIRD-PARTY.txt。

点击聊天麦克风开始录音，再点一次结束，最长60秒。结果只加入输入框，确认或修改后才发送。
此识别脚本本身不访问麦克风；桌宠仅在明确操作期间录音。录音与转写经内存和父子进程管道传递，不保存原始录音、不上传识别服务。
确认发送的文字遵循当前Ollama地址、联网与聊天记录设置。口音、噪声、专有名词和同音字会影响准确性。

接口
python transcribe.py --parent-pid <父进程PID>
stdin接收16kHz单声道PCM16 RIFF WAV，最长60秒。
stdout输出一行UTF-8 JSON：text、language、language_probability。无语音时text为空；错误为非零退出码和安全说明。
只读取本地权重，运行时离线，不开放HTTP端口；取消或父进程退出时结束。
