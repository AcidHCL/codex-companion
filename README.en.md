# AI Pet 1.30.2

[简体中文](README.md) | **English**

Bring AI characters to your Windows desktop: change their appearance, let them walk and interact with windows, chat with them, and receive reminders when work is finished.

AI Pet combines desktop character animation, **local chat through Ollama**, a **Codex task dashboard**, and **local speech**. Enable the connections you need; the pet and local animations also work without Codex or an AI model.

This is the English documentation. The application interface is currently in Chinese; key UI labels are included below to help you find the controls.

| Feature | Available now |
| --- | --- |
| Characters and animation | 9 character skins; state animations for DeepSeek and GPT; switch between classic and newer DeepSeek animations |
| Desktop interaction | Dragging, walking, falling and landing, standing on windows, jumping, and climbing |
| Chat and image understanding | Configurable Ollama address, switching between installed models, streamed replies, and image drag-and-drop or paste |
| History and memory | Local conversation history, conversation switching, shared memory across conversations, optional web search and webpage reading |
| Speech | Local voice input, separate voice settings for all 9 characters, Voice Studio generation / audition / named saves, a voice library with selection / renaming / deletion, streamed playback, and model loading controls |
| Work dashboard | Codex usage allowances and tasks, optional message reading, and Codex / Dot / All filters |
| Settings and models | A dedicated settings window for pet interaction and speech; model selection synchronized with chat, manual loading / unloading, and color-coded GPU memory usage |

**This repository contains source code and assets. You must build the application yourself; prebuilt EXE / DLL files, Python environments, and model weights are not included.**

**Settings:** Right-click the pet and select Settings (设置), or open it from the work dashboard, character wardrobe, or system tray. Pet & Interaction (桌宠与互动) manages display, walking, behavior, music, and performances. Models & Speech (模型与语音) manages chat and speech models, GPU memory, character voices, and Voice Studio. The wardrobe focuses on character selection and asset imports; the dashboard focuses on Codex work progress. Existing preferences and saved voices are preserved. Model status refreshes only while the corresponding settings page is visible.

**Performances:** Under Pet & Interaction → Performances & Emotions (桌宠与互动 → 表演与情绪), select “Sing Let Me Go” (唱 Let Me Go), “保持 Q 弹 · 邦友的酒,” or another expression or dance, then use the shared play and stop controls. Music & Singing (音乐与唱歌) contains only the music toggle and volume control.

## Character previews

These are selectable **character skins**. Their names do not mean the application includes nine corresponding large language models. Chat uses the Ollama model you configure. **DeepSeek and GPT** currently have basic state animations; standalone songs and dances are available only for DeepSeek.

<table>
  <tr>
    <td align="center"><img src="docs/previews/skins/deepseek.png" width="180" alt="DeepSeek character skin"><br><strong>DeepSeek</strong></td>
    <td align="center"><img src="docs/previews/skins/gemini.png" width="180" alt="Gemini character skin"><br><strong>Gemini</strong></td>
    <td align="center"><img src="docs/previews/skins/claude.png" width="180" alt="Claude character skin"><br><strong>Claude</strong></td>
  </tr>
  <tr>
    <td align="center"><img src="docs/previews/skins/gpt.png" width="180" alt="GPT character skin"><br><strong>GPT</strong></td>
    <td align="center"><img src="docs/previews/skins/grok.png" width="180" alt="Grok character skin"><br><strong>Grok</strong></td>
    <td align="center"><img src="docs/previews/skins/glm.png" width="180" alt="GLM character skin"><br><strong>GLM</strong></td>
  </tr>
  <tr>
    <td align="center"><img src="docs/previews/skins/kimi.png" width="180" alt="Kimi character skin"><br><strong>Kimi</strong></td>
    <td align="center"><img src="docs/previews/skins/minimax.png" width="180" alt="MiniMax character skin"><br><strong>MiniMax</strong></td>
    <td align="center"><img src="docs/previews/skins/qwen.png" width="180" alt="Qwen character skin"><br><strong>Qwen</strong></td>
  </tr>
</table>

## DeepSeek animation previews

Six selected **classic animations**, shown as silent looping GIFs. These are **not the redrawn 24 FPS versions**. The previews use a light background and preserve the original poses and frame durations. Looping here is only for README presentation; it does not change how one-shot actions such as anger play in the application. The singing preview has no sound; in-app music has its own toggle.

<table>
  <tr>
    <td align="center"><img src="docs/previews/classic/idle.gif" width="180" alt="DeepSeek classic idle animation"><br><strong>Idle</strong></td>
    <td align="center"><img src="docs/previews/classic/working.gif" width="180" alt="DeepSeek classic working animation"><br><strong>Working</strong></td>
    <td align="center"><img src="docs/previews/classic/walk-right.gif" width="180" alt="DeepSeek classic walking-right animation"><br><strong>Walking</strong></td>
  </tr>
  <tr>
    <td align="center"><img src="docs/previews/classic/drag.gif" width="180" alt="DeepSeek classic being-picked-up and struggling animation"><br><strong>Picked up</strong></td>
    <td align="center"><img src="docs/previews/classic/angry.gif" width="180" alt="DeepSeek classic angry animation"><br><strong>Angry</strong></td>
    <td align="center"><img src="docs/previews/classic/sing.gif" width="180" alt="DeepSeek classic singing animation, without audio"><br><strong>Singing</strong></td>
  </tr>
</table>

The application also includes landing, happy, startled, shy, jumping, climbing, and other performances. Expressions and performances can be selected manually. With LLM behavior mode enabled, the model can also choose installed actions based on the conversation. Actual movement remains controlled by desktop physics; the model cannot override passive states such as dragging or falling.

The new **“保持 Q 弹 · 邦友的酒”** performance is available in both animation sets. Select it under Performances & Emotions (表演与情绪), or say “播放邦友的酒” in chat. It uses all 122 frames from the original GIF, with the background removed frame by frame, preserving the original motion and 12.5 FPS on a 512×512 transparent canvas. After the first complete playback, the continuous turning sequence at the end repeats until the accompanying music finishes, then the pet returns to idle / working. The original GIF's opening hard cut is preserved, but excluded from the loop. Existing animations and Let Me Go are unchanged.

## GPT: the little white dragon

Selecting the **GPT** skin activates its own animation set in the classic format: **17 actions and 120 transparent PNGs**. These include idle, working, walking in both directions, dragging, falling, landing, four expressions, and jumping, climbing, and pulling onto window ledges in both directions. The set uses a 512×512 canvas and the classic DeepSeek frame timings. Left and right actions are mirrored pairs, with window contact points calibrated for the GPT artwork. Songs, bowl-tapping, and standalone dances are not included yet. The front/back leg alternation in this first walking version is still not clear enough; this is a known visual improvement item.

The eight GIFs below come directly from the current GPT animation assets. They preserve the original frame durations and use a light background to make the white hair and dress easier to see. These are silent looping previews. Jumping and climbing show only the character poses; the application calculates actual desktop movement from window positions.

<table>
  <tr>
    <td align="center"><img src="docs/previews/gpt/idle.gif" width="160" alt="GPT little dragon idle animation"><br><strong>Idle</strong></td>
    <td align="center"><img src="docs/previews/gpt/working.gif" width="160" alt="GPT little dragon working animation"><br><strong>Working</strong></td>
    <td align="center"><img src="docs/previews/gpt/walk-right.gif" width="160" alt="GPT little dragon walking-right animation"><br><strong>Walking</strong></td>
    <td align="center"><img src="docs/previews/gpt/drag.gif" width="160" alt="GPT little dragon being-picked-up and struggling animation"><br><strong>Picked up</strong></td>
  </tr>
  <tr>
    <td align="center"><img src="docs/previews/gpt/happy.gif" width="160" alt="GPT little dragon happy expression animation"><br><strong>Happy</strong></td>
    <td align="center"><img src="docs/previews/gpt/angry.gif" width="160" alt="GPT little dragon angry expression animation"><br><strong>Angry</strong></td>
    <td align="center"><img src="docs/previews/gpt/jump-right.gif" width="160" alt="GPT little dragon jumping-right animation"><br><strong>Jumping</strong></td>
    <td align="center"><img src="docs/previews/gpt/climb-right.gif" width="160" alt="GPT little dragon climbing-right animation"><br><strong>Climbing</strong></td>
  </tr>
</table>

See the [preview asset notes](docs/previews/README.md) for export details and frame counts.

Chat, action planning, and click responses use the user-provided fan interpretation of the little white dragon: confident, capable, fond of treasure, and caring beneath a proud exterior, with playful jokes about treasure and tokens. The character does not request real payments, claim official status, or change your selected Ollama backend. Configure voices separately for each character under Settings → Models & Speech (设置 → 模型与语音). DeepSeek defaults to the Let Me Go voice, and GPT defaults to its saved dragon-girl voice. Existing custom selections and explicitly disabled voice settings are preserved.

The original character design is credited to **ZipZipPipe** and is an unofficial personification. See the [AI Moe Atlas](https://selinlee.github.io/ai-moe-atlas/zh/characters/zipzippipe-chatgpt/) for attribution and conditions associated with the original work. The new animations were generated from the small character illustration supplied by the user. The personality is this project's creative interpretation, not a universal or official character setting.

## Quick start

### Requirements

- **Windows 11 x64** and the **.NET 10 SDK**.
- **Microsoft Edge WebView2 Runtime** for animation, video, and Live2D.
- Optional chat and speech dependencies are described below; the basic desktop pet does not require them.

The project targets `net10.0-windows10.0.22000.0`. Restoring development dependencies for the first time requires internet access. Everyday pet rendering and installed speech models can run offline.

### Build and launch

Open PowerShell at the repository root:

```powershell
dotnet --version
.\desktop-pet\build.ps1 -Release
.\desktop-pet\launch.ps1
```

Build output goes to `desktop-pet/dist/`. Keep the entire output directory, including `assets/`; do not move only the generated EXE. A normal build requires the .NET 10 Desktop Runtime on the target computer.

To include the .NET runtime, run `.\desktop-pet\build.ps1 -Release -SelfContained`. The script preserves the self-contained mode of an existing output directory and checks that runtime files match the launch configuration. When using `dotnet build` directly, choose a fresh output directory so you do not overwrite a self-contained publish directory.

<details>
<summary>Build directly with dotnet, or publish an application directory that includes the runtime</summary>

```powershell
dotnet restore .\desktop-pet\DesktopPet.csproj
dotnet build .\desktop-pet\DesktopPet.csproj -c Release --output .\desktop-pet\dist
.\desktop-pet\dist\DesktopPet.exe
```

To publish a version that includes the .NET runtime:

```powershell
dotnet publish .\desktop-pet\DesktopPet.csproj -c Release -r win-x64 --self-contained true --output .\artifacts\app
```

The repository does not include the compiled output of these commands. WebView2, Python, and model weights must still be prepared separately.

</details>

### Basic controls

- **Right-click the character:** show icons for Chat (对话), Settings (设置), Dashboard (看板), and more.
- **Left-click the character:** one click makes it happy, a second consecutive click surprises it, and the third or later click makes it angry. The count resets after 3 seconds without a click. Double-clicks also count as interactions; open chat through the right-click Chat icon.
- **Character wardrobe (角色换装):** select a skin. **Settings → Pet & Interaction (设置 → 桌宠与互动):** adjust size and always-on-top behavior, enable walking and window interaction, and control music and performances.
- **Scene animations (场景动画):** preview actions, switch between classic and newer animation sets, or import custom packs.
- **System tray:** show, hide, or quit the pet.

Click reactions do not require a model connection. DeepSeek and GPT currently have matching animations; other skins respond with bubbles. When LLM behavior mode (LLM 行为模式) is enabled and Ollama is connected, click counts and the expressions already played are combined into a notification to the model. Background reactions are limited to once every 10 seconds, with user chat taking priority. Regular chat models can respond verbally; tool-capable models can also choose expressions. These events are not saved as user chat messages. Dragging requires holding and moving the mouse; clicks do not interrupt a manually started performance or a physics-driven action.

Further instructions, currently in Chinese: [First use](首次使用.txt) · [Scene animations](desktop-pet/动画说明.txt) · [Live2D](desktop-pet/Live2D说明.txt)

## Optional: Ollama chat

Start Ollama and install a model appropriate for your hardware. In the chat connection settings, test and save the service's root address; the default is `http://127.0.0.1:11434`. The application automatically reads that service's installed model list so you can select and switch models.

Models with image support accept images dropped, selected, or pasted into chat. Capabilities and results depend on the selected model; the character skin does not change the underlying model. The current connection settings do not provide API key or login authentication.

The chat panel supports streamed replies, stopping generation, new conversations, conversation history, and shared memory. Records are stored locally, and reopening a conversation lets you continue it. Models use a limited context window, so the full saved history and the context sent with each request are not necessarily the same length. Enable Web (联网) to search for current information or read public webpage links, with sources displayed.

The chat model preload and unload controls affect only the selected model. They do not delete weights or change Ollama's global settings. Speech models have separate controls, and speech playback does not automatically unload the chat model.

### Model and GPU memory settings

Open **Settings → Models & Speech (设置 → 模型与语音)** to choose an LLM installed on the current Ollama service. Selection is **synchronized immediately in both directions with the chat panel**, preserving the current conversation and draft. Selecting a model does not preload it automatically. Click Load LLM (加载 LLM), or let the next message load it on demand. Speech models have separate load / unload controls; enable the speech master switch at the top of the same page and save a valid voice configuration for the current character first.

The GPU memory section shows each loaded model's name and usage in a different color, along with speech model usage, other / unattributed usage, and free memory. Model colors remain consistent across refreshes and sorting. Remote Ollama usage is listed separately and excluded from the local chart. Multiple local GPUs are represented by aggregate values, without assuming how a model is distributed across individual cards.

The page shows actual model loading status, the speech backend, available per-model memory figures, and local GPU used / total memory and utilization. It queries status every **5 seconds** only while the page is visible, and also supports manual refresh. Viewing status does not load models. Missing metrics are shown as “Not provided” (未提供); failed connections or queries display a reason.

The speech memory chart uses PyTorch's **reserved** memory. Allocated memory and cache are already included and are not added again. Ollama figures come from the service's reported model GPU memory. Local totals come from the NVIDIA driver and include other applications. Sampling times and measurement scopes differ; discrepancies are explicitly noted when the figures do not align. Driver queries are optional: model controls remain usable without NVIDIA's query utility.

Unloading releases a resident model without deleting its files or changing Ollama's global settings or automatic retention time. **Other applications sharing the same Ollama model are also affected by unloading it**, and their next request may need to reload it.

## Optional: local speech and voice input

| Feature | Implementation | Runtime requirements |
| --- | --- | --- |
| Character speech | faster-qwen3-tts + Qwen3-TTS 1.7B Base / CustomVoice / VoiceDesign | Python 3.12 and a compatible NVIDIA GPU / driver; the loading check requires at least 7 GiB of free GPU memory |
| Voice input | faster-whisper-small, multilingual recognition, CPU int8 | Python 3.12 and a CPU; does not use the chat model's GPU memory |

These features can be installed independently. Prepare Python 3.12, then run the scripts you need from the repository root:

```powershell
$python = (Get-Command python -ErrorAction Stop).Source

# Speech environment and the Base model for the existing voices
.\desktop-pet\voice-service\setup-fast.ps1 -PythonExe $python -WorkspaceRoot (Get-Location).Path -DownloadModel

# Microphone voice input
.\desktop-pet\asr-service\setup.ps1 -PythonExe $python -WorkspaceRoot (Get-Location).Path -DownloadModel
```

The setup scripts download public dependencies and weights into `.tools/`. TTS uses the `faster` backend by default. Base uses `desktop-pet/voice-service/reference.wav`; CustomVoice and VoiceDesign do not require reference audio. See the [portable speech notes](便携语音说明.txt), currently in Chinese.

If the speech environment is already installed, download the additional models directly from the repository root:

```powershell
& .\.tools\deepseek-voice-fast-venv\Scripts\python.exe .\desktop-pet\voice-service\download_model.py --model voice-design
& .\.tools\deepseek-voice-fast-venv\Scripts\python.exe .\desktop-pet\voice-service\download_model.py --model custom-voice
# --model all downloads Base, VoiceDesign, and CustomVoice; the default still downloads Base only.
```

Downloading retrieves and validates files without loading a model onto the GPU. Speech model weights, Python environments, and download records are not distributed with the source repository.

Under **Models & Speech → Speech Generation Method (模型与语音 → 语音生成方式)**, choose a method separately for each of the 9 characters, then click Apply Generation Method (应用生成方式):

| Generation method | Character configuration |
| --- | --- |
| Disable speech for this character (不启用此角色语音) | Turn off text readout for this character |
| Use a saved voice — Base (使用已保存音色（Base）) | Choose Let Me Go, the saved GPT dragon-girl voice, or a voice you created and named from the list below |
| Use a preset speaker — CustomVoice (使用预置说话人（CustomVoice）) | 9 preset speakers, with optional delivery instructions; no reference audio required |
| Generate from a description — VoiceDesign (按文字即时生成（VoiceDesign）) | Describe a voice in text, without reference audio; voice characteristics may vary between sentences or generations |

### Use an existing voice: let the pet speak

Install the **Base** model using the steps above, then:

1. Open **Settings → Models & Speech (设置 → 模型与语音)**, enable **Pet Speech (桌宠语音播报)** at the top, and set the speech volume.
2. Choose a character under **Speech Generation Method → Edit Character (语音生成方式 → 编辑角色)**.
3. Select a name in **Saved Voices (已保存音色)** and click **Use Selected Voice (使用所选音色)**. This automatically saves the “Use a saved voice — Base” mode. DeepSeek can use Let Me Go, and GPT can use the saved GPT dragon-girl voice.
4. Switch the pet to that character, then click **Audition (试听)** at the top of the page or send a chat message. The first readout needs to load the model; you can also click **Preload Speech (预加载语音)** first.

Edit Character changes whose voice settings you are editing; it does not switch the pet's skin. The top audition, load, and unload controls apply to the **current desktop pet character**. Singing accompaniment and spoken readout have independent toggles.

### Voice Studio: create a voice from a prompt

Voice Studio requires **VoiceDesign + Base**. VoiceDesign generates the audition sample, and Base reuses that sample after it is saved. You do not need to supply reference audio yourself. After downloading VoiceDesign as described above:

1. Select the character to configure, find **Voice Studio (音色工坊)** below, and enter a voice name, such as “White Dragon · Elegant Voice.”
2. Enter the voice description, audition text, and language. For example, this prompt describes a Mandarin-speaking voice:

   > A young adult female voice, clear and resonant, with warm support in the low and middle register. Natural, clearly articulated Mandarin at a slightly unhurried pace. Elegant and confident, with a hint of restrained pride and a smile in her voice. Speak naturally; avoid a forced high-pitched voice or exaggerated theatrical delivery.

   Example audition text in Chinese:

   > 你好，今天也交给本小姐吧。事情一件件做就好，忙完了也别忘记休息。

   Meaning: “Hello, leave things to me today. Take them one at a time, and don't forget to rest when you're done.”

3. Click **Generate Audition (生成试听)**, then **Audition (试听)** inside the studio when generation finishes. You can listen to the same audio repeatedly. To change it, edit the voice description and generate again.
4. When satisfied, click **Save and Use This Voice (固定并保存使用)**. The voice appears under **Saved Voices (已保存音色)** with your chosen name and becomes the selected character's voice.
5. Next time, select the saved name directly. **You do not need to re-enter the prompt for each use.** You can rename it later without generating new audio.

Names must contain 1–60 characters, voice descriptions allow up to 600, and audition text allows up to 100. A natural short passage of 20–80 characters is recommended; the generated reference audio must be 1–30 seconds long. Editing the description or audition text requires another generation before saving. Closing Settings discards an unsaved candidate. Generation can be canceled, and audition playback can be stopped. Saving preserves the voice identity; new lines still have natural variations in intonation.

### Select, rename, and delete voices

**Saved Voices (已保存音色)** is a separate list that shows the saved name directly, with protection status, save time, and other details on a second line. Select a name and click Use Selected Voice (使用所选音色) to activate it. Each character can keep multiple named voices for later selection, renaming, or deletion. Older unnamed voices receive a display name based on their character and save date. Renaming preserves both the audio and reference ID. Let Me Go and the saved GPT dragon-girl voice are the protected built-in defaults for DeepSeek and GPT; they cannot be deleted or renamed. When an active custom voice is deleted, affected characters first return to their own defaults, then the voice is removed from the list. A recoverable copy remains locally in `voices/.trash-<reference ID>`; model weights are not deleted.

- **Select:** choose a name → Use Selected Voice (使用所选音色).
- **Rename:** select a custom voice → edit its name → Rename (重命名).
- **Delete:** select a custom voice → Delete (删除) → confirm. Default voices marked Protected (已保护) cannot be deleted.

Custom voices are stored locally in `%LOCALAPPDATA%\AIPet\voices`. Character settings store only a reference ID, and upgrades preserve these settings. To move to another computer, migrate both the character settings and this directory. Personal voices, model weights, and runtime environments are not distributed with the source. While Voice Studio is working, it temporarily pauses regular speech and releases the previous speech model; it does not change Ollama settings or unload the LLM.

For characters without an existing configuration, **DeepSeek defaults to Let Me Go, GPT defaults to the saved GPT dragon-girl voice, and speech is disabled for the other characters**. Previously saved custom selections and explicitly disabled states remain unchanged. Selecting a character to edit does not switch the pet skin or Ollama model, and saving does not automatically load a model. The speech master switch and volume are shared, while character voice configurations are independent. Preload, unload, and audition apply to the current desktop pet character. Changing the current voice stops the previous readout; the next readout uses the new configuration without changing Ollama settings.

The settings page provides speech on/off, audition, stop, preload, and unload controls. When speech is enabled, opening chat warms up and retains the speech model; leaving chat restores idle unloading. Manual unload takes priority. Streamed playback preserves the model's natural pauses between sentences. Initial loading and hardware load can still affect how quickly speech begins.

Without LLM behavior control, click responses are also read aloud for characters with speech configured. A new click response, preset notification, or model reply immediately stops the previous readout and begins preparing the new one. Sentences within the same reply continue sequentially, and interrupted content is not replayed later.

Click the microphone to start recording and click again to finish. The transcription goes into the input box for review before you send it. Recording is limited to 60 seconds and can be canceled. Spoken readout and scene songs have independent sound toggles; voice input can also be used on its own.

## Optional: Codex work dashboard

Follow the [first connection instructions](Codex首次连接.txt), currently in Chinese, to pair through your own local Codex task. The connection requires the official Codex App on the same computer, under the same Windows user and session.

The dashboard reads usage allowances and the task list by default. **Reading message bodies must be enabled separately.** It supports Codex / Dot / All filters; the main pet's task notifications currently follow Codex only. Account usage allowances are not split by task source.

This feature uses internal Codex App interfaces, so app updates may affect compatibility. The pet will not connect if identity or signature validation fails. Pair again when changing computers or Windows users.

## Project structure and development

```text
desktop-pet/
├─ DesktopPet.csproj    # WPF desktop application
├─ *.cs                # UI, tasks, chat, animation, speech, and local data
├─ assets/             # Characters, scene animations, and browser runtime assets
├─ live2d-web/          # Live2D web source
├─ scene-web/           # Scene rendering tests and notes
├─ voice-service/       # Local character speech service
├─ asr-service/         # Local speech recognition
├─ build.ps1
└─ launch.ps1
```

A normal C# build uses the browser runtime assets already in `assets/` and does not require Node. If you change the Live2D web source, rebuild it using the [frontend build instructions](desktop-pet/live2d-web/README.md), preserving the original Cubism Framework/Core licenses and separate files.

Configuration templates use relative paths. At runtime, the application searches upward from its directory for `.tools/`; it does not depend on the developer's machine-specific directories.

## Data and privacy

- Personal data lives in the current user's `%LOCALAPPDATA%\AIPet`. Chat text, images, and pairing material are protected with Windows current-user encryption. The repository does not contain personal data.
- Ollama requests go to the local computer by default. If you configure another computer's address, messages and images are sent there. Enabling web search sends search queries to the search service and provides webpage text to the selected model.
- Microphone recording starts only through an explicit user action. Local recognition does not upload recordings. Once you confirm and send the transcribed text, it follows the current chat and web settings.
- Do not commit `.tools/`, build outputs, caches, logs, databases, installed machine-specific path configurations, or connection records.

## Licensing and assets

The project **does not yet declare a project-wide open-source license; the maintainer still needs to define the permitted scope of use**. Third-party code, runtimes, and models remain subject to their own licenses. Preserve their LICENSE, NOTICE, THIRD-PARTY, and author information.

Rights to character artwork, songs, reference voices, and sample models are separate from rights to the application code. A software or model license does not automatically grant permission to distribute these assets. Attribution for Let Me Go is not permission to redistribute the recording; publishers should verify the permitted scope of use themselves.
