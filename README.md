# ScoutLauncher · Microsoft Scout 中文汉化加载器


Microsoft Scout 是微软在 Build 2026 开发者大会期间推出的一款 AI 代理，定位为全天候
AI 个人助理，深度整合 Microsoft 365 生态中的 Outlook、OneDrive、Teams 等应用，是
微软首款定位为个人助理的 AI 代理。

Scout 挺好用的，就是界面全是英文，看着有点累。我平时也是中文用着舒服，就顺手做了个
汉化，让它打开就是中文，省得每次还要在脑子里过一遍。

---

## 🚀 快速开始

1. 装好并登录 Microsoft Scout（0.22.x 及以上）【注意你必须自行安装主程序并且获取到使用权限】。
2. 下载本仓库 release版本并安装
3. 运行程序，它会自动接管正在运行的 Scout 并把界面变成中文，然后缩到
   右下角托盘常驻。
   - **再次双击** = 展开/收起运行日志窗口；
   - 托盘图标**右键** → 「退出并关闭汉化」= 变回原版英文（不影响 Scout）；
   - 托盘图标右键 → 「项目主页」= 打开本仓库。


---

## 安全

说实话，装这种第三方小工具，心里多少会犯嘀咕，我自己也一样。所以这里把它到底做了
什么讲清楚：

它没有去动 Scout 本身，一个文件都没改，就相当于在界面上盖了一层中文。你想用就开着，
不想用关掉就还是原来的英文版，两边互不影响。

它也不会去碰你的东西。整个过程它就干一件事：把屏幕上显示的英文换成中文。你的账号、
对话、文件这些，它既不读也不传，压根不参与。

另外它也很干净，不会往系统里塞别的东西，卸掉之后不留痕迹。

简单讲，就是个能随时摘掉的"中文皮肤"，放心用就行。

---

## 它其实是个模板

中文只是先做的第一个。翻译的内容是单独放的，跟软件本身分开，所以想做**繁体中文、
日语、韩语**之类的，照着换一份词表就行，软件一行都不用改。

再往上说一层，这套其实是 **Electron 应用做外挂汉化的一条标准路子**。Scout 是
Electron 写的，就能在不动原程序的前提下，给界面"盖"一层翻译上去。所以不光是
Scout，市面上一大票 Electron 桌面软件，理论上都能用同样的思路做 i18n——这份东西
也算给想折腾的人留个参考。

---

## 兼容哪些版本

支持 **0.22.x 及以上**，**内部版和前沿版都能用**——反正它俩本来就是同一个程序。

> 下面的截图是内部版 v0.23.127 的。

---

## 都翻了哪些地方

日常能看到的地方基本都是中文了，挨个放几张图 👇

**主界面和左边导航**——新建对话、自动化、共同创作、输入框这些。
![主界面](assets/home-zh.png)

**要授权的时候**——它要联网或者开网页，会先问你一句，「允许 / 本次会话允许 /
始终允许 / 拒绝」都写得清清楚楚。
![授权提示](assets/perm-zh.png)

**干活的过程**——Scout 正在做什么，「打开网页 / 已允许 / 输出」这些都能看明白。
![执行过程](assets/moremenu-zh.png)

**自动化**——那种定时自动跑的任务，整页都翻了。
![自动化](assets/auto-zh.png)

**共同创作**——跟 AI 一起写文档、做 PPT 的地方，每一步说明都是中文。
![共同创作](assets/cocreate-zh.png)

**设置**——各项配置的标题和说明都翻好了，一看就知道是干嘛的。
![设置](assets/settings-zh.png)

**选模型**——切换的菜单翻了；模型名字（GPT-5.5、Claude 这些）留着英文，看着反而更顺。
![模型选择](assets/modelpicker-zh.png)

---

## 目录结构

```
ScoutLauncher/
├─ README.md              你正在看的这份
├─ assets/                截图
├─ dist/                  开箱即用（下载即跑）
│  ├─ Scout Loader.exe
│  └─ dictionary.zh-CN.json   语言包（想加新语言照着复制一份即可）
└─ src/                   加载器源码（想自己编译看这里）
   ├─ ScoutZh.cs          主程序（C#，.NET Framework）
   ├─ overlay-engine.js   语言无关的翻译引擎（编译时嵌入 exe）
   ├─ scout.ico           图标
   └─ build-exe.ps1       一键编译脚本
```

---

## 自己编译

只要 Win10/11（自带 .NET Framework 4.6+），不用装任何 SDK：

```powershell
cd src
powershell -ExecutionPolicy Bypass -File build-exe.ps1
```

会在 `src/` 下生成 `Scout Loader.exe`。运行时记得把 `dictionary.zh-CN.json`
（在 `dist/`）放到 exe 同目录。

想做别的语言：复制一份 `dictionary.zh-CN.json` 改成 `dictionary.<语言标签>.json`
（如 `dictionary.ja-JP.json`），翻译里面的值，然后 `Scout Loader.exe --lang ja-JP`
即可，**exe 不用重新编译**。

---

## 发布新版本（自动出 MSI）

发布走 CI/CD：**推一个版本 tag，GitHub Actions 自动编译并生成 MSI 安装包**，
挂到对应版本号的 Release 上。MSI 里打包了两个产物——`Scout Loader.exe` 和
`dictionary.zh-CN.json`，装好即用（含开始菜单快捷方式，卸载干净）。

```bash
git tag v1.2.0
git push origin v1.2.0
```

- 版本号从 tag 解析（去掉前缀 `v`，格式须为 `x.y.z`），并写进程序集版本。
- 流水线定义见 [.github/workflows/release.yml](.github/workflows/release.yml)，
  安装包定义见 [installer/ScoutLauncher.wxs](installer/ScoutLauncher.wxs)。

---

## 翻得不对的地方，欢迎说

先声明：这些词基本都是 **Opus** 翻的，所以哪句读着别扭、不准，或者还有没翻到的，
锅归它 —— 提个 [issue](https://github.com/kukisama/ScoutLauncher/issues) 骂它就行，
反正修也是它修。😌

我就是个负责点鼠标、跑跑程序的工具人，纯属打杂。谢谢大家 🙏
