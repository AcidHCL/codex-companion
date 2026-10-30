AI Pet 本地语音服务（1.30.2）

音色工坊与用户固定音色（1.28.0）
“设置 → 模型与语音 → 音色工坊”按角色提供文字设计、候选试听、命名与显式保存；“已保存音色”列表提供切换、重命名和删除。生成只使用临时 VoiceDesign 工作进程，不修改角色当前声音；固定后保存 WAV、台词、描述与元数据，在角色配置中引用 saved-voice + ReferenceId。试听重放原候选字节，不重新随机生成。
用户参考位于 Win32 LocalApplicationData/AIPet/voices/<32位小写十六进制ID>/reference.wav 与 reference.txt。客户端只传 reference_id，服务端不接受请求指定路径。加载前检查 ID、目录边界与重解析点、WAV 格式和大小、参考长度及台词；不存在的引用报错，不回退到其他角色的声音。
saved-voice 使用 Base 权重和 ICL 参考克隆，音频请求的 voice 为 saved-voice:<id>。固定后的 instruct 不参与新台词生成。旧 Base、GPT 固定音色、CustomVoice、VoiceDesign 协议保持兼容。
生成 / 试听暂时释放普通播报模型，结束、取消或关闭设置窗口时回收临时工作进程。用户 voices 目录独立于软件安装目录，升级保留；源码包不包含用户音色与设置。Ollama 配置保持不变。

GPT 龙娘固定音色（1.27.1）
在“设置 → 模型与语音”选择编辑角色，再从“已保存音色”列表选择“GPT 龙娘固定音色”，点击“使用所选音色”。该选项复用 Base 权重，以随包附带的 GPT 专属 reference.wav + reference.txt 做 ICL 语音克隆，后续朗读不再调用 VoiceDesign 或重复音色描述。最初的文字设计提示词保存在 references/gpt-dragon-v1/reference-info.json 中；每次说话的语调仍会自然变化。DeepSeek 的 Let Me Go 参考音频和原有克隆方式保持独立。
固定音色需要已安装 Base 模型；使用固定音色时不需要加载 VoiceDesign。

新增模式与角色配置
“设置 → 模型与语音 → 语音生成方式”按 9 个桌宠角色分别保存配置。未配置时 DeepSeek 默认 Let Me Go，GPT 默认 GPT 龙娘固定音色，其余角色默认关闭；已有明确保存的音色选择或关闭状态保持不变。编辑对象不会改变当前皮肤或 Ollama 聊天模型；保存不会自动下载或加载声音模型。
Base 1.7B：复用所选已保存音色；DeepSeek 的 Let Me Go 沿用原 reference.wav、x-vector-only 模式及自然音高。
CustomVoice 1.7B：内置 9 种固定说话人，可附加语气指令，无需参考音频。
VoiceDesign 1.7B：用文字描述音色，无需参考音频，但不同请求 / 句段之间的音色可能有变化。
三种模式均走当前语音服务和播放器。当前角色切换声音时停止旧播报并回收旧工作进程，一个工作进程只加载一个声音模型。不会改动或自动卸载 Ollama。

已有环境下载命令（在源码根目录）
& .\.tools\deepseek-voice-fast-venv\Scripts\python.exe .\desktop-pet\voice-service\download_model.py --model voice-design
& .\.tools\deepseek-voice-fast-venv\Scripts\python.exe .\desktop-pet\voice-service\download_model.py --model custom-voice
--model all 下载 Base、VoiceDesign、CustomVoice；省略时下载 Base。可用 --model-root 指定模型根目录。下载固定官方仓库 revision，并校验公开文件大小 / LFS SHA-256；只下载文件，不占用 GPU 载入模型。新模型使用 Base 已解析目录的同级文件夹，不修改现有 config.json。

1.27.0 启动协议补充
stdin 第一行仍为随机认证令牌，第二行可选 JSON：model_id（base / custom-voice / voice-design）、speaker、language、instruct。缺省第二行沿用 Base；白名单模式不接受模型路径或 URL。指令最多 600 字，VoiceDesign 必填；启动后固定配置。
GET /health 的 model / voice 返回实际选择。音频请求的 voice 对应 deepseek-let-me-go、custom-voice:<speaker> 或 voice-design；服务验证请求与启动配置一致。CustomVoice / VoiceDesign 不检查或读取参考 WAV。
以下为原 Base 音色后端的历史验收和部署说明；多角色及新增模型行为以本节为准。

当前验收状态（2026-10-27）
用户再次允许测试后，已使用生产 server.py 的完整认证协议完成真实 GPU 验收。
独立服务配置复用原权重和 reference.wav；没有请求 Ollama、修改其设置或卸载其模型。
当前桌面负载下，冷预加载22.54秒；暖机短/长句首包约0.25–0.28秒，RTF约0.27–0.33。
1.2秒音频缓冲在本次全部样本的实际到达时间中没有欠载；这不是所有负载下的速度保证。
已验证请求取消、聊天租约保留、手动卸载和空闲卸载。手动卸载约1.01秒退出工作进程；
空闲卸载使用独立fixture的2秒阈值，生产60秒设置不变。两轮退出后自身GPU进程均无残留。
CUDA Graph普通GC可能保留显存，因此生产fast卸载时退出自身，下一次使用重新建立进程。
验收报告和四段WAV保存在 development-checks\fast-voice-acceptance-20261027。
原始参考的说话人embedding与新引擎提取结果逐元素相同。9个样本的CPU质量审查未发现漏句、削波或明显接缝异常。
新旧同句生成声音的speaker cosine约0.986；与歌曲参考约0.957。长答可保留模型自身较长的句间停顿。
这些客观指标不等于主观音色完全相同，歌曲与口语仍需试听评价。
桌宠实机流式验证14项通过：首次加载14.39秒，预热后实际开始播放0.44–0.87秒，两个测试均无中途重新缓冲。
聊天定时续租、手动卸载进程退出、旧回答不复活也通过；没有真实Ollama或账号请求。
config.json 现已启用 faster；改回 legacy 并重启桌宠即可使用保留的旧环境。

模型：Qwen/Qwen3-TTS-12Hz-1.7B-Base，原有权重和参考音色保持不变。
加速环境使用 faster-qwen3-tts 0.5.4、qwen-tts-hf 0.1.1.post1、Transformers 5.15.1。
Transformers 5.21 与当前 codec 实现不兼容，因此必须使用 requirements-fast-lock.txt。
旧 qwen-tts 0.1.1 环境保留，可将 config.json 的 backend 改为 legacy 回退。
本机环境：Python 3.12、PyTorch/torchaudio 2.8.0+cu128，SDPA，RTX 5090。
模型权重和依赖安装在工作区 .tools；没有改动系统 Python。

音色来源与限制
reference.wav 是用户提供的 Let Me Go(共创版) 经本机 HTDemucs 人声分离后的
80–89 秒片段，9秒、单声道、44.1kHz。未向远程服务上传歌曲或对话。
reference-info.json 记录处理步骤。由于未人工核对逐字转录，使用
x_vector_only_mode=True，仅提取参考说话人音色；没有编造歌词文字。
没有对系统女声改音高冒充参考音色，没有强制移调。歌曲与口语的韵律、
气声及身份相似度仍可能存在差异，需要试听评价；基频接近不代表音色完全一致。

运行
由桌宠按 config.json 的 backend 选择隔离环境：
faster -> .tools\deepseek-voice-fast-venv\Scripts\python.exe server.py
legacy -> .tools\deepseek-voice-venv\Scripts\python.exe server.py
--port 18766 --parent-pid <桌宠PID>，随机会话令牌通过 stdin 第一行传入。
不要把令牌写入命令行、磁盘或日志。所有接口要求 X-AIPet-Voice-Token。
只监听 127.0.0.1；拒绝非空 Origin 和非本机 Host；不提供文件、URL抓取接口。
运行时启用 HF/Transformers 离线模式，不记录用户对话正文。

GET /health -> status: idle/loading/synthesizing/ready/unloading/error，model，voice，message，loaded，retained，backend，streaming，retiring。
POST /v1/audio/speech -> {input, voice:"deepseek-let-me-go", request_id}，返回PCM WAV。
input 限2000字符；串行队列最多4项，内部约100字符一段。
POST /v1/audio/stream -> 相同请求，返回 application/x-ndjson；只在 faster 后端提供。
记录按 format/sample_rate=24000/channels=1/sample_format=s16le 开始，随后 audio/data(Base64 PCM16)，以 done 结束。
中途失败发送 error/code/message；客户端必须要求 done，不能把连接中断当作正常结束。
POST /preload -> {request_id}，预热成功才返回200；可通过 /cancel 取消等待或丢弃结果。
POST /retention -> {active:true/false}；保留租约45秒，客户端每15秒刷新，不会自行加载模型。
用户手动 /unload 优先于保留租约；后续租约也不会偷偷重新加载。
faster 工作进程卸载时置 retiring=true 并退出自身，确保 CUDA 上下文释放；客户端须确认自己的进程已退出。
下一次主动合成或预加载时，客户端重新启动已认证的独立工作进程。
POST /cancel -> {request_id}；POST /unload -> {}。
Qwen 0.1.1 外层会忽略停止条件，所以在单次合成期间为内部 HF talker.generate
临时注入 StoppingCriteria，调用结束恢复。不修改依赖源码。
生成阶段可在token边界取消；加载和声码器阶段不可强制中断，但取消后不返回音频。
没有聊天保留租约时，空闲60秒卸载模型并释放CUDA缓存；桌宠父进程退出时服务立即退出。
faster 的实际生成每8个codec帧输出一段声音。客户端必须在独立生产队列中持续读取，不能等上一段播放完再读取。
加速与旧版共用 BF16、SDPA、x-vector 模式及 non_streaming_mode=True；不更换声音，不调整音高或语速。
初次预热与 codec 首次使用仍有开销；不要把冷启动时间算作稳定流式首包。
加载前要求至少7GiB可用显存，不终止/卸载Ollama模型。不足返回gpu_busy。

路径与换电脑
使用 setup-fast.ps1 创建独立加速环境；不会改动旧 deepseek-voice-venv。
config.json 支持backend/model_path/reference_path/stream_chunk_size；绝对路径受到尊重。
默认相对模型路径会向服务目录祖先查找 .tools\deepseek-voice-models，因此
工作区下的构建/发布输出可复用本机模型。复制EXE并不包含约4.54GB模型和Python。
换电脑需要准备兼容CUDA/Python环境、权重、参考音频，必要时修改本地config.json。
requirements.txt 固定主要依赖，requirements-lock.txt 为本机已验证完整版本。
模型来源/commit保存在 .tools\deepseek-voice-models\model-download.json。
可使用 setup.ps1 -PythonExe <Python3.12完整路径> -WorkspaceRoot <桌宠工作区>
-DownloadModel 准备独立环境及官方权重；约需下载8GB并预留15GB空间。
setup.ps1 会把本机模型绝对路径写入服务config.json，不改变系统Python。
现有机器已经安装完成，不需要再次运行安装。请保留THIRD-PARTY.txt说明。

开发验证
development-checks\voice-engine\test_voice_service.py：本机鉴权、Origin/Host、字段/长度、
取消等不需要模型的测试。smoke_synthesis.py：固定测试句合成、实际取消、显存及基频。
样本deepseek-voice-sample.wav、deepseek-task-completed.wav及synthesis-report.json保存在
development-checks\voice-engine；测试没有自动播放音频。

官方来源
https://github.com/QwenLM/Qwen3-TTS
https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base
https://github.com/adefossez/demucs
https://pytorch.org/get-started/previous-versions/
