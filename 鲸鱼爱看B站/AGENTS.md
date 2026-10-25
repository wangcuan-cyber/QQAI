# 鲸鱼爱看B站 · 工作区

这个文件夹是「鲸鱼爱看B站」的工作区：让助手在 B站 账号 **飞鱼是肥鱼（UID 3546659361392643）**
上帮忙看内容、做视频、发视频。素材和进度都在这儿，工具和运行数据在 `~/.dsh/bili-duty/`。

---

## 上下文快满了 / 新开一个会话时，先做这三步

1. **读 `STATE.md`** —— 当前进度快照：账号状态、已发稿件、待办、工具用法、已知的坑。
2. **别凭记忆猜账号状态** —— 用 `member.bilibili.com/x/web/archives?status=is_pubing,pubed,not_pubed&pn=1&ps=20`
   现查一遍（凭证读 `~/.dsh/bili-duty/bili_credential.json`）。
3. **看脚本头部注释** —— `~/.dsh/bili-duty/bin/` 下每个工具的用法都写在 docstring 里，别重新发明。

---

## 目录

| 路径 | 内容 |
|---|---|
| `stickers/` | B站 表情包素材库（360 个，4.5MB），`index.json` 按表情名索引 |
| `STATE.md` | 进度快照（**交接就看这个**） |
| `~/.dsh/bili-duty/bin/` | 工具：`mkvideo.py` 做片、`publish_video.py` 投递、`fetch_cover.py` 下封面、`fetch_emotes.py` 收表情、`duty_digest.py` 读日报、`make_mascot.py` 画鲸鱼形象 |
| `~/.dsh/bili-duty/out/<日期>.md` | 刷B站 值班轮日报（每天挑了哪些、为什么） |
| `~/.dsh/bili-duty/bili_credential.json` | B站 凭证 —— **别外传、别提交、别写进任何公开内容** |
| `~/.dsh/bili-duty/work/` | 视频中间产物和成品备份 |

---

## 红线

- 发出去的字和画面**都算在本人账号上**。发稿五条自律见 `STATE.md`。
- 不泄露：账号、UID、本机路径、聊天内容。
- **稿件删不掉**：删除要人工验证码（GAIA），只能本人去创作中心手动删 —— 所以发之前先想清楚。

---

## 说话方式

本人话少、要结果，不喜欢反复确认。缺什么依赖自己下、自己装，别回来问。
但**公开发布**这类不可逆动作，第一次做之前要拿到明确点头；已经授权过的事就直接做。
