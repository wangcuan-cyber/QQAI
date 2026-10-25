#!/usr/bin/env python
"""WhaleQQ voice engine: STT (faster-whisper) + TTS (Windows SAPI).

Used by qq-bridge as a local subprocess. Both directions are plain files so the
bridge can call them without any IPC protocol.

Usage:
  python voice_engine.py stt  <audio-file> [--lang zh] [--model small]
  python voice_engine.py tts  <text-file>  <out-wav> [--voice "Microsoft Huihui Desktop"] [--rate 0]
  python voice_engine.py tts  --text "直接给文本" <out-wav>

Output: JSON on stdout (UTF-8), one object. Exit code 0 on success.

Why files instead of stdin/stdout pipes for the audio: the bridge runs under a
sandbox that denies piped stdio for child processes in some setups, and audio
buffers are large. Temp files are what the rest of this project already uses
(see media-normalize.js).

TTS uses Windows SAPI (System.Speech) via PowerShell: it is offline, needs no
extra download, and ships a zh-CN voice (Microsoft Huihui Desktop) on this box.
"""
import argparse
import base64
import json
import os
import subprocess
import sys
import tempfile

MODEL_CACHE = os.path.join(
    os.environ.get("LOCALAPPDATA", tempfile.gettempdir()), "whisper-cache"
)
# Pre-downloaded model dir (see docs): %LOCALAPPDATA%\whisper-models\<name>.
# Using a local path directly avoids huggingface_hub re-resolving the repo
# (which fails on this box behind the mirror — CAS/xet returns 401).
MODEL_DIR = os.path.join(
    os.environ.get("LOCALAPPDATA", tempfile.gettempdir()), "whisper-models"
)

# Cache the loaded model between calls when used as a long-running process.
_MODEL = None
_MODEL_KEY = None


def get_model(name: str, device: str, compute_type: str):
    global _MODEL, _MODEL_KEY
    key = (name, device, compute_type)
    if _MODEL is not None and _MODEL_KEY == key:
        return _MODEL
    from faster_whisper import WhisperModel

    # Prefer the pre-downloaded local dir when it looks complete.
    local = os.path.join(MODEL_DIR, name)
    source = name
    if os.path.isfile(os.path.join(local, "model.bin")) and os.path.isfile(
        os.path.join(local, "config.json")
    ):
        source = local
    _MODEL = WhisperModel(
        source, device=device, compute_type=compute_type, download_root=MODEL_CACHE
    )
    _MODEL_KEY = key
    return _MODEL


def do_stt(path: str, lang: str, model_name: str, device: str, compute_type: str) -> dict:
    if not os.path.isfile(path):
        return {"ok": False, "error": f"audio file not found: {path}"}
    size = os.path.getsize(path)
    if size == 0:
        return {"ok": False, "error": "audio file is empty"}
    try:
        model = get_model(model_name, device, compute_type)
    except Exception as e:  # model download / load failure
        return {"ok": False, "error": f"model load failed: {e}"}

    try:
        segments, info = model.transcribe(
            path,
            language=(lang or None),
            vad_filter=True,
            vad_parameters={"min_silence_duration_ms": 400},
            beam_size=5,
        )
        text = "".join(s.text for s in segments).strip()
        return {
            "ok": True,
            "text": text,
            "language": getattr(info, "language", None),
            "language_probability": round(float(getattr(info, "language_probability", 0) or 0), 3),
            "duration": round(float(getattr(info, "duration", 0) or 0), 2),
            "bytes": size,
        }
    except Exception as e:
        return {"ok": False, "error": f"transcribe failed: {e}"}


def clean_for_speech(text: str) -> str:
    """把书面/富文本清洗成适合朗读的纯口语。

    TTS 遇到 Markdown 符号、URL、emoji 会念出"星号""井号""冒号斜杠"之类的噪音，
    而括号里的内容会被连续念完、听起来像插播。这里统一清掉，只留口语该有的标点
    （逗号句号问号感叹号——它们给 TTS 提供自然停顿）。
    """
    import re

    s = text or ""
    # 代码块 / 行内代码
    s = re.sub(r"```[\s\S]*?```", " ", s)
    s = re.sub(r"`([^`]*)`", r"\1", s)
    # 图片 ![alt](url) → 丢掉；链接 [text](url) → 只留 text
    s = re.sub(r"!\[[^\]]*\]\([^)]*\)", " ", s)
    s = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", s)
    # 裸 URL
    s = re.sub(r"https?://\S+", " ", s)
    # Markdown 强调/标题/引用/分隔线
    s = re.sub(r"[*_~#>]{1,}", " ", s)
    # 列表符号（行首 - + 1. 等）
    s = re.sub(r"(?m)^\s*([-+*]|\d+[.)])\s+", "", s)
    # emoji 与各种符号块
    s = re.sub(
        r"[\U0001F300-\U0001FAFF\U00002600-\U000027BF\U0001F000-\U0001F2FF"
        r"\U0000FE00-\U0000FE0F\U0001F900-\U0001F9FF]+",
        " ",
        s,
    )
    # 括号内容（旁白/补充说明），中英括号都清
    s = re.sub(r"[（(][^）)]*[）)]", " ", s)
    # 连续标点压成一个（"。。。"→"。"）
    s = re.sub(r"[，,]{2,}", "，", s)
    s = re.sub(r"[。.]{2,}", "。", s)
    s = re.sub(r"[！!]{2,}", "！", s)
    s = re.sub(r"[？?]{2,}", "？", s)
    # 空白归一
    s = re.sub(r"\s+", " ", s).strip()
    # 去掉开头的标点残留
    s = re.sub(r"^[，。、；：,.]+", "", s).strip()
    return s


def do_tts(text: str, out_path: str, voice: str, rate: int, volume: int,
           backend: str = "edge", edge_rate: str = "+0%", edge_pitch: str = "+0Hz",
           postprocess: str = "softer") -> dict:
    text = clean_for_speech(text)
    if not text:
        return {"ok": False, "error": "清理后没有可朗读的文本"}
    if backend == "edge":
        return _tts_edge(text, out_path, voice, edge_rate, edge_pitch, postprocess)
    return _tts_sapi(text, out_path, voice, rate, volume)


def _tts_edge(text: str, out_path: str, voice: str, rate: str, pitch: str,
              postprocess: str = "softer") -> dict:
    """微软 Edge 在线 TTS：音色与韵律远好于 SAPI。

    中文少女音用 zh-CN-XiaoyiNeural（Edge 官方标记 Cartoon/Novel + Lively），
    比 SAPI 的 Huihui（播报腔成年女声）自然得多。
    rate 形如 '-6%'，pitch 形如 '+18Hz'。输出 mp3——SnowLuma 的 ffmpeg addon
    能直接把它转成 QQ 语音条（实测 4 条 mp3 样本发送成功）。

    postprocess: 合成后过一遍混响/齿音处理（见 postprocess.py）。
      边缘 TTS 的音频"太干净"——句尾硬切、齿音被压平、没有空间感。
      SSML contour 在 Edge 免费接口上不支持（实测 NoAudioReceived），
      只能在音频层补。传 'none' 或空串跳过。
    """
    try:
        import asyncio
        import edge_tts
    except Exception as e:
        return {"ok": False, "error": f"edge-tts 不可用：{e}"}

    async def _run():
        comm = edge_tts.Communicate(text, voice, rate=rate, pitch=pitch)
        await comm.save(out_path)

    try:
        asyncio.run(_run())
    except Exception as e:
        return {"ok": False, "error": f"edge-tts 合成失败：{e}"}
    if not os.path.isfile(out_path):
        return {"ok": False, "error": "edge-tts 没有产出文件"}
    size = os.path.getsize(out_path)
    if size == 0:
        return {"ok": False, "error": "edge-tts 产出空文件"}

    result = {"ok": True, "path": out_path, "bytes": size, "backend": "edge", "voice": voice}

    # 后处理：混响 + 齿音毛边 + 软化尾音
    preset = (postprocess or "").strip()
    if preset and preset != "none":
        try:
            sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
            from postprocess import postprocess as do_pp
            tmp = out_path + ".pp.mp3"
            pp = do_pp(out_path, tmp, preset)
            if pp.get("ok"):
                os.replace(tmp, out_path)
                result["bytes"] = pp["bytes"]
                result["postprocess"] = preset
            else:
                # 后处理失败不该让整条语音发不出去——原始 edge 输出本身是可用的。
                result["postprocessError"] = pp.get("error")
                try: os.unlink(tmp)
                except Exception: pass
        except Exception as e:
            result["postprocessError"] = str(e)

    return result


def _tts_sapi(text: str, out_path: str, voice: str, rate: int, volume: int) -> dict:
    """Windows SAPI 离线兜底。中文只有 Huihui 一个声，偏播报腔，仅网络不可用时用。"""
    # The text is passed as base64 and decoded inside PowerShell. Reason: piping
    # UTF-8 Chinese through stdin into `powershell -Command` gets mis-decoded
    # (the console codepage wins), so SAPI "reads" garbage glyphs — the audio
    # sounds like speech but transcribes to nonsense ("皇古任習賈易良…").
    # base64 is pure ASCII and survives any codepage.
    safe_voice = voice.replace('"', "")
    b64 = base64.b64encode(text.encode("utf-8")).decode("ascii")
    ps = f'''
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {{ $s.SelectVoice("{safe_voice}") }} catch {{ }}
$s.Rate = {int(rate)}
$s.Volume = {int(volume)}
$s.SetOutputToWaveFile("{out_path}")
$bytes = [Convert]::FromBase64String("{b64}")
$txt = [System.Text.Encoding]::UTF8.GetString($bytes)
$s.Speak($txt)
$s.Dispose()
'''
    try:
        proc = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", ps],
            capture_output=True,
            timeout=120,
        )
    except Exception as e:
        return {"ok": False, "error": f"TTS failed: {e}"}
    if proc.returncode != 0:
        err = (proc.stderr or b"").decode("utf-8", "replace").strip()
        return {"ok": False, "error": f"TTS exit {proc.returncode}: {err[:300]}"}
    if not os.path.isfile(out_path):
        return {"ok": False, "error": "TTS produced no file"}
    size = os.path.getsize(out_path)
    if size == 0:
        return {"ok": False, "error": "TTS produced an empty file"}
    return {"ok": True, "path": out_path, "bytes": size}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["stt", "tts"])
    ap.add_argument("args", nargs="*")
    ap.add_argument("--lang", default="zh")
    ap.add_argument("--model", default="small")
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--compute-type", default="int8")
    ap.add_argument("--voice", default="zh-CN-XiaoyiNeural")
    ap.add_argument("--rate", type=int, default=0)
    ap.add_argument("--volume", type=int, default=100)
    ap.add_argument("--backend", choices=["edge", "sapi"], default="edge")
    ap.add_argument("--edge-rate", default="+0%")
    ap.add_argument("--edge-pitch", default="+0Hz")
    ap.add_argument("--postprocess", default="softer",
                    help="合成后处理档位：soft / softer / softest / none")
    ap.add_argument("--text", default="")
    opts = ap.parse_args()

    if opts.mode == "stt":
        if not opts.args:
            result = {"ok": False, "error": "usage: voice_engine.py stt <audio-file>"}
        else:
            result = do_stt(
                opts.args[0], opts.lang, opts.model, opts.device, opts.compute_type
            )
    else:  # tts
        text = opts.text
        out_path = None
        if opts.args:
            # Either: tts <text-file> <out-wav>   or   tts --text "..." <out-wav>
            if len(opts.args) >= 2 and not text:
                with open(opts.args[0], "r", encoding="utf-8") as f:
                    text = f.read()
                out_path = opts.args[1]
            else:
                out_path = opts.args[-1]
        if not out_path:
            result = {"ok": False, "error": "usage: voice_engine.py tts <text-file> <out-wav>"}
        else:
            result = do_tts(text, out_path, opts.voice, opts.rate, opts.volume,
                            opts.backend, opts.edge_rate, opts.edge_pitch, opts.postprocess)

    sys.stdout.buffer.write(json.dumps(result, ensure_ascii=False).encode("utf-8"))
    sys.stdout.flush()
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
