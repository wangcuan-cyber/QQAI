#!/usr/bin/env python
"""测量语音的基频（F0），用来客观判断音色高低。

为什么需要它：用户听不了/看不了，没法靠耳朵判断"够不够萌"。而萝莉音的核心特征
就是基频高——动漫少女常见区间约 280~330Hz，成年女声约 180~230Hz，再往上
（>350Hz）会变成"捏着嗓子"的假声。

算法：自相关（autocorrelation）。
  1. ffmpeg 把任意音频解码成 16kHz 单声道 WAV
  2. 分帧（25ms 窗、10ms 步），每帧做归一化自相关
  3. 在 60~500Hz 对应的滞后区间找自相关峰值
  4. 峰值够强（>0.3）才认作浊音帧，取该帧的基频
  5. 对所有浊音帧取中位数（抗异常值），并给出百分位分布

输出 JSON：{ ok, f0_median, f0_mean, f0_p10, f0_p90, voiced_ratio, frames }

用法：
  python measure_f0.py <audio-file>
"""
import json
import os
import subprocess
import sys
import tempfile
import wave

import numpy as np

FFMPEG = r"C:\Users\xt621\Desktop\鲸鲸的QQ\tools\ffmpeg.exe"

SR = 16000
WIN_MS = 40
HOP_MS = 10
F0_MIN = 60
F0_MAX = 500
# 自相关峰值低于该值视为清音/静音，不计入统计
VOICED_THRESHOLD = 0.30


def decode_to_wav(src: str, dst: str) -> bool:
    args = [
        FFMPEG, "-v", "error", "-y",
        "-i", src,
        "-ar", str(SR), "-ac", "1", "-c:a", "pcm_s16le",
        dst,
    ]
    try:
        proc = subprocess.run(args, capture_output=True, timeout=120)
        return proc.returncode == 0 and os.path.isfile(dst)
    except Exception:
        return False


def read_wav(path: str) -> np.ndarray:
    with wave.open(path, "rb") as w:
        n = w.getnframes()
        raw = w.readframes(n)
        data = np.frombuffer(raw, dtype=np.int16).astype(np.float64)
    return data / 32768.0


def estimate_f0(samples: np.ndarray) -> dict:
    win = int(SR * WIN_MS / 1000)
    hop = int(SR * HOP_MS / 1000)
    if len(samples) < win:
        return {"ok": False, "error": "audio too short"}

    lag_min = int(SR / F0_MAX)
    lag_max = int(SR / F0_MIN)

    f0s = []
    for start in range(0, len(samples) - win, hop):
        frame = samples[start:start + win]
        rms = float(np.sqrt(np.mean(frame ** 2)))
        if rms < 0.005:          # 静音帧
            continue
        frame = frame - np.mean(frame)                      # 去直流
        norm = float(np.sqrt(np.sum(frame ** 2)))
        if norm < 1e-9:
            continue
        # 自相关（用 FFT 加速）
        n = 2 * win
        spec = np.fft.rfft(frame, n)
        acorr = np.fft.irfft(spec * np.conj(spec), n)[:win]
        acorr = acorr / (norm * norm + 1e-12)               # 归一化到 [-1,1]
        if lag_max >= len(acorr):
            continue
        seg = acorr[lag_min:lag_max]
        if len(seg) == 0:
            continue
        peak_idx = int(np.argmax(seg))
        peak_val = float(seg[peak_idx])
        if peak_val < VOICED_THRESHOLD:                     # 清音/噪声
            continue
        lag = lag_min + peak_idx
        if lag <= 0:
            continue
        # 抛物线插值，提高亚采样精度
        if 0 < lag < len(acorr) - 1:
            y0, y1, y2 = acorr[lag - 1], acorr[lag], acorr[lag + 1]
            denom = (y0 - 2 * y1 + y2)
            if abs(denom) > 1e-12:
                lag = lag + 0.5 * (y0 - y2) / denom
        f0 = SR / lag
        if F0_MIN <= f0 <= F0_MAX:
            f0s.append(f0)

    total_frames = max(1, (len(samples) - win) // hop)
    if not f0s:
        return {"ok": False, "error": "no voiced frames detected", "frames": total_frames}

    arr = np.array(f0s)
    return {
        "ok": True,
        "f0_median": round(float(np.median(arr)), 1),
        "f0_mean": round(float(np.mean(arr)), 1),
        "f0_p10": round(float(np.percentile(arr, 10)), 1),
        "f0_p90": round(float(np.percentile(arr, 90)), 1),
        "voiced_frames": len(f0s),
        "frames": total_frames,
        "voiced_ratio": round(len(f0s) / total_frames, 3),
    }


def main() -> int:
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "error": "usage: measure_f0.py <audio>"}))
        return 1
    src = sys.argv[1]
    if not os.path.isfile(src):
        print(json.dumps({"ok": False, "error": f"not found: {src}"}))
        return 1
    tmp = os.path.join(tempfile.gettempdir(), f"f0-{os.getpid()}.wav")
    try:
        if not decode_to_wav(src, tmp):
            print(json.dumps({"ok": False, "error": "ffmpeg decode failed"}))
            return 1
        samples = read_wav(tmp)
        result = estimate_f0(samples)
    finally:
        try:
            os.unlink(tmp)
        except Exception:
            pass
    sys.stdout.buffer.write(json.dumps(result, ensure_ascii=False).encode("utf-8"))
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
