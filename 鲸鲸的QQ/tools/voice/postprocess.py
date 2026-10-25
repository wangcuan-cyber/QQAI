#!/usr/bin/env python
"""语音后处理：混响 + 齿音毛边 + 软化尾音。

为什么需要它：edge-tts 合成的音频"太干净"——句尾干净利落地切断、齿音被压平、
没有空间感。真人说话有房间混响、有气声毛边、句尾会自然拖一点。TTS 的 SSML
contour（音高曲线）在 Edge 免费接口上不被支持（实测 NoAudioReceived），
所以只能在这一层用 ffmpeg 补。

用法：
  python postprocess.py <in> <out> [--preset soft|softer|softest]
"""
import argparse
import json
import os
import subprocess
import sys

FFMPEG = r"C:\Users\xt621\Desktop\鲸鲸的QQ\tools\ffmpeg.exe"

# 三档：越往后混响越明显、尾音越软。
# 关键设计：
#   highpass  去掉 70~75Hz 以下的低频浑浊（TTS 常有），让声音更"轻"
#   treble    提升 5kHz 以上——这就是"齿音毛边"，TTS 默认把齿音压平了
#   aecho     短延迟多次反射（40~180ms），衰减轻，模拟小房间而非电话亭；
#             它顺带让句尾自然拖一点，不再硬切
#   loudnorm  ⚠️ 必须最后做：aecho 的 out_gain 会衰减主体信号（实测整体响度
#             掉 6.7dB、高频掉 4.4dB），不加归一化会让语音变得又轻又闷，
#             正好和「齿音留毛边」的目标相反。统一拉到 -16 LUFS（语音标准）。
PRESETS = {
    "sweet": {
        # 「糯糯」：柔滑、黏连、有厚度。
        # 和干声的区别不是加效果，而是改 EQ 重心 + 轻压缩：
        #   · highpass 降到 55Hz —— 保留更多低频，声音才有厚度，不飘不薄
        #   · bass 提 180Hz 附近 —— 「糯」的胸腔感就在这一段，齿音区反而不该亮
        #   · treble 只留 +0.8dB —— 之前 +2.5dB 是"毛边"，但毛边多会显脆，
        #     脆和糯是反的；这里只留一丝气声，防止闷住
        #   · acompressor 轻压缩 —— 动态压小，字与字之间更"黏"、更贴耳
        #   · loudnorm 收尾
        "highpass": 55,
        "bass_gain": 2.5,
        "bass_freq": 180,
        "treble_gain": 0.8,
        "treble_freq": 6000,
        "compressor": "threshold=-20dB:ratio=2:attack=12:release=220:makeup=1",
        "reverb": False,
        "loudnorm_i": -16,
    },
    "sweet_more": {
        # 更糯：低频更厚、压缩更明显、齿音几乎不留
        "highpass": 50,
        "bass_gain": 4.0,
        "bass_freq": 170,
        "treble_gain": 0.2,
        "treble_freq": 6500,
        "compressor": "threshold=-24dB:ratio=2.8:attack=10:release=260:makeup=1.5",
        "reverb": False,
        "loudnorm_i": -16,
    },
    "sweet_most": {
        # 最糯：低频最厚、动态最小（最"黏"），接近耳语质感
        "highpass": 48,
        "bass_gain": 5.0,
        "bass_freq": 160,
        "treble_gain": -0.5,
        "treble_freq": 6500,
        "compressor": "threshold=-28dB:ratio=3.5:attack=8:release=300:makeup=2",
        "reverb": False,
        "loudnorm_i": -16,
    },
    "dry": {
        # 无混响、无染色：只做低频清理 + 齿音毛边 + 响度归一
        "highpass": 75,
        "treble_gain": 2.5,
        "treble_freq": 5500,
        "reverb": False,
        "loudnorm_i": -16,
    },
    "soft": {
        # 轻微混响
        "highpass": 75,
        "treble_gain": 2.5,
        "treble_freq": 5500,
        "echo_in": 0.92, "echo_out": 0.72,
        "echo_delays": "40|78", "echo_decays": "0.10|0.05",
        "reverb": True,
        "loudnorm_i": -16,
    },
    "softer": {
        # 中等混响
        "highpass": 70,
        "treble_gain": 3.5,
        "treble_freq": 5200,
        "echo_in": 0.90, "echo_out": 0.70,
        "echo_delays": "44|86|131", "echo_decays": "0.13|0.07|0.04",
        "reverb": True,
        "loudnorm_i": -16,
    },
    "softest": {
        # 最软：混响最足、尾音拖最长
        "highpass": 65,
        "treble_gain": 4.5,
        "treble_freq": 5000,
        "echo_in": 0.88, "echo_out": 0.68,
        "echo_delays": "48|94|143|188", "echo_decays": "0.16|0.09|0.05|0.03",
        "reverb": True,
        "loudnorm_i": -16,
    },
}


def build_filter(preset: dict) -> str:
    parts = [f"highpass=f={preset['highpass']}"]
    # 低频厚度（「糯」的核心）
    if preset.get("bass_gain"):
        parts.append(f"bass=g={preset['bass_gain']}:f={preset['bass_freq']}")
    parts.append(f"treble=g={preset['treble_gain']}:f={preset['treble_freq']}")
    if preset.get("compressor"):
        parts.append(f"acompressor={preset['compressor']}")
    if preset.get("reverb"):
        parts.append(
            f"aecho={preset['echo_in']}:{preset['echo_out']}:{preset['echo_delays']}:{preset['echo_decays']}"
        )
    # 响度归一化必须在最后：aecho/压缩的衰减要拉回来，也统一各档音量。
    parts.append(f"loudnorm=I={preset['loudnorm_i']}:TP=-1.5:LRA=11")
    return ",".join(parts)


def postprocess(src: str, dst: str, preset_name: str = "softer") -> dict:
    preset = PRESETS.get(preset_name) or PRESETS["softer"]
    if not os.path.isfile(src):
        return {"ok": False, "error": f"input not found: {src}"}
    if not os.path.isfile(FFMPEG):
        return {"ok": False, "error": f"ffmpeg not found: {FFMPEG}"}
    args = [
        FFMPEG, "-v", "error", "-y",
        "-i", src,
        "-af", build_filter(preset),
        "-c:a", "libmp3lame", "-q:a", "4",
        dst,
    ]
    try:
        proc = subprocess.run(args, capture_output=True, timeout=120)
    except Exception as e:
        return {"ok": False, "error": f"ffmpeg 启动失败：{e}"}
    if proc.returncode != 0:
        err = (proc.stderr or b"").decode("utf-8", "replace").strip()
        return {"ok": False, "error": f"ffmpeg 退出码 {proc.returncode}: {err[:300]}"}
    if not os.path.isfile(dst) or os.path.getsize(dst) == 0:
        return {"ok": False, "error": "后处理没有产出文件"}
    return {"ok": True, "path": dst, "bytes": os.path.getsize(dst), "preset": preset_name}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--preset", choices=list(PRESETS), default="softer")
    opts = ap.parse_args()
    result = postprocess(opts.src, opts.dst, opts.preset)
    sys.stdout.buffer.write(json.dumps(result, ensure_ascii=False).encode("utf-8"))
    sys.stdout.flush()
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
