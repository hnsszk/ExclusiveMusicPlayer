<div align="center">

# 🎵 ExclusiveMusicPlayer

**网易云音乐第三方播放器 · WASAPI 独占播放**

</div>

> [!NOTE]
> 本项目为**个人兴趣爱好 + AI 辅助生成**的作品。目的是「能用」，不保证代码或功能完美，也不保证长期维护。介意请谨慎使用。

---

## ✨ 功能

- **本地优先的 API**：内置网易云音乐 API 服务，播放器启动时自动拉起，无需单独安装
- **WASAPI 独占播放**：独占模式下音质最佳，其他程序声音被静音；也可切共享模式与其他程序共存
- **二维码登录**：扫码登录你的网易云账号
- **首页推荐**：每日推荐歌曲、个性化推荐歌单、私人/日系/欧美雷达
- **歌单 / 专辑 / 歌手**：搜索、浏览、收藏、删除
- **音质选择**：标准 → Hi-Res → 杜比全景声等多档
- **播放模式**：顺序 / 随机 / 单曲循环，偏好自动记忆
- **播放列表与浏览解耦**：点开歌单不打断正在播的队列
- **音频输出设备选择**：可指定输出设备与独占/共享模式

## 📦 运行环境

| 依赖 | 说明 |
|---|---|
| **.NET 8** | 播放器为自包含发布，用户无需安装 |
| **Node.js** | 内置 API 使用 `runtime\node.exe`，用户无需安装 |

> 从 Release 下载的包，**开箱即用**，无需安装任何运行时。

## 🚀 快速开始

1. 从 **Release** 下载最新发布包，解压到任意目录
2. 双击 `ExclusiveMusicPlayer.exe` 启动
3. 首次启动播放器会自动拉起内置 API，等转圈结束进入主界面
4. 登录、搜索、播放 🎧

> **首次运行注意**：Windows 可能会弹出防火墙/网络提示（关于 Node.js 的权限请求），请选择**允许**，否则内置 API 无法工作。

## 🛠 特别情况：自行配置 API

如果内置 API 无法正常工作（例如端口被占用、或你想用自己的服务器），可以手动配置 API 服务。

### 方式一：本地部署（推荐）

1. 打开客户端，进入 **设置** → 取消勾选 **「自动启动本地 API」**
2. 保证电脑已安装 [Node.js](https://nodejs.org/)
3. 克隆 API 项目到本地：

   ```bash
   git clone https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced
   cd api-enhanced
   ```

4. 启动 API 服务：

   ```bash
   node app.js
   ```

5. 回到客户端，在 **设置** → **API 服务** 中填写本地地址（默认 `http://localhost:3000`），点 **测试连接** → **保存**
6. 之后正常使用即可

### 方式二：在线部署

也可以将 API 部署到在线平台（如 Vercel 等），获得一个公网地址，然后在客户端的 **设置 → API 服务** 中填入该地址。

> 在线部署的具体步骤不在此展开，可参考 API 项目文档。

注意，若使用在线部署，由于本地每次请求需要将网易云cookie上传，需额外注意安全问题。

---

## 📄 API 服务

本项目使用的网易云音乐 API 来自开源项目：

- [NeteaseCloudMusicApiEnhanced/api-enhanced](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced)

播放器本身不连接网易云官方接口，所有数据经由上述 API 项目获取。

## 📜 开源协议

本项目源码使用 **MIT License** 开源。

> 第三方依赖（如 NAudio）遵循各自的开源协议。内置 API 遵循其上游项目 [api-enhanced](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced) 的协议（MIT）。

## ⚠️ 免责声明

- 本项目仅用于学习与技术交流，请勿用于商业用途
- 音乐版权归网易云音乐及其版权方所有，请尊重版权，合法使用
- 使用本项目产生的任何问题，作者不承担责任

---

*Made with ❤️ & AI*
