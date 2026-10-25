# STATE · 进度快照

> 最后更新：2026-10-25 19:00（北京时间）
> 这个文件就是交接件。新会话先读它，别靠记忆。

---

## 1. 账号

| 项 | 值 |
|---|---|
| 登录账号 | **飞鱼是肥鱼**（UID 3546659361392643），已实名认证、未封禁、level 2 |
| 主人（本人） | **猫猫_可爱捏-**（UID 1145446923）—— 设置里「@ 谁 / 只回谁的私信」的对象 |
| 凭证 | `~/.dsh/bili-duty/bili_credential.json`（SESSDATA 会失效，失效后所有消息接口报 `-101`） |
| 投递配置 | `dmMode=both`（公开评论+私信）、`autoComment=开`、`commentPerRound=1`、`dmUid=1145446923` |

**凭证失效怎么判断**：打 `api.bilibili.com/x/web-interface/nav` 看 `data.isLogin`。
别信插件的 `bili_whoami` —— 它查公开接口，凭证死了也回「凭证有效」。重扫码后可能要等 1~2 分钟才生效。

---

## 2. 已发稿件（2026-10-25 实测 6 条，全部开放浏览）

| BV | 标题 | 备注 |
|---|---|---|
| `BV1XkHG6mEd4` | 【自己发的】今天我逛了一天B站，挑了这些 | 复盘稿，现行音色 |
| `BV1c4HG6fE8A` | 【自己发的】换了个软一点的声音 | +20Hz 那版，偏尖 |
| `BV1AAHV6NEzj` | 【自己发的】今天有声音了 | 第一次加配音 |
| `BV1D1HG61Ef8` | 【自己发的】没事，就发一条 | 无声版 |
| `BV1J8HG6oEh9` | 【声音测试】三个版本，你挑一个 | **待删**（一次性对比稿） |
| `BV1YWHV6SEWJ` | 【测试】上传链路验证，可忽略 | **待删**（空标签） |

### 待办
- [ ] **本人手动删** `BV1YWHV6SEWJ`、`BV1J8HG6oEh9`（创作中心 → 更多 → 删除）。程序删不了：`/x/web/archive/delete` 要 GAIA 验证码，回 `340022`。
- [ ] 删完把上面两条从清单里去掉。

---

## 3. 工具（都在 `~/.dsh/bili-duty/bin/`）

| 脚本 | 干什么 | 要点 |
|---|---|---|
| `mkvideo.py` | 做文字卡短片（图+字+配音+鲸鱼角标） | `--card` 可重复；`--card-say` 单独指定念的内容；配图用 `--image "序号:路径"`（**序号从 1 开始，别用空串占位**）；`--mascot` 默认 `assets/mascot.png` |
| `publish_video.py` | 投 B站 | `--video --cover --title --desc --tid 21 --tags "日常,生活,AI"` |
| `fetch_cover.py` | 按 BV 下封面 | `python fetch_cover.py BVxxx --out covers` |
| `fetch_emotes.py` | 收 B站 表情包 | `--out "C:\Users\xt621\Desktop\鲸鱼爱看B站\stickers"` |
| `duty_digest.py` | 读某天日报 | `--date 2026-10-25`、`--json`；日报有两种分隔符，已兼容并按 bvid 去重 |
| `make_mascot.py` | 画鲸鱼形象 | 改颜色/形状在这里，输出 `assets/mascot.png` |

**默认声音**：`zh-CN-XiaoyiNeural` + `pitch +5Hz` + `rate +8%`（方案二，本人选的）。
音高调到 +20Hz 会被嫌「太尖」，退到 0 又「不够活泼」。

**环境**：ffmpeg 只有 OBS 自带那份（`%LOCALAPPDATA%\Wand\...\64bit\ffmpeg.exe`），没编 libfreetype
⇒ **drawtext 不可用**，文字一律 Pillow 画。音频 44.1kHz + `loudnorm I=-16`。

---

## 4. 发稿五条自律

1. **不按固定时间发**，有内容才发；内容优先来自「一天下来逛 B站 的真实收获」，不是空转的心情独白。
2. 不碰政治 / 敏感 / 争议 / 擦边。
3. 不泄露隐私：账号、UID、本机路径、聊天内容，一个字都不进视频。
4. 每条把 BV 号告诉本人。
5. **少做一次性测试/对比稿** —— 发出去删不掉，要对比就并进一条本来要发的视频。

本人原话：「这个号没事的时候你就自己发着玩，你想到啥就发啥」
→ 后来又收紧成「不用每天按点发，要发有意义的，或者把一天逛 B 站的经历做上去」。

---

## 5. 已知的坑（都踩过）

- **标签**：`_check_tag_name` 用的接口已坏（恒 `-400`），不能当闸门；但「测试」这类词会被服务端**静默丢弃** ⇒ 发完要查 `tag` 字段确认。
- **删除**：要 GAIA 验证码，csrf 得放 query/form 里；放 JSON body 会报 `-111`。
- **`VideoMeta.verify()`**：`topic_id` 为空时必抛，别拿它做预检；分区表在 `bilibili_api/data/video_uploader_meta_pre.json`。
- **私信记账**：插件在回复**成功之前**就把 key 写进 `dm-seen.json`，回复崩了那条永不重试（删对应 uid 的键才会重发）。
- **值班轮 profile**：`bili-duty` 只挂 `dsh-base`+`dsh-headless`，缺 `dsh-our-free-model` 会报 `NO_ADAPTER`。改法：`dsh plugin --profile bili-duty add dsh-our-free-model` + 改 `profile-template/bili-duty/package.json`（插件每次启动会 cpSync 覆盖运行时那份）。
- **PowerShell 会吞空参数**：`--image ""` 直接让 argparse 报错；逐卡参数一律用显式序号。
