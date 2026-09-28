# FAPM-Display 服务端 (PC Driver)

FAPM-Display 是一款专为飞行模拟爱好者打造的软硬件联动工具。本项目为 PC 端服务端程序，负责通过 SimConnect 实时读取微软模拟飞行 (MSFS 2020 / MSFS 2024) 的底层遥测数据，同时整合 SimBrief 飞行计划和 VATSIM 连飞网络状态，通过 WebSocket 将数据结构化下发给 ESP32 硬件显示终端。

## ✨ 核心特性

- ✈️ **全自动飞行状态机**：智能识别准备、滑行、爬升、巡航、下降、进近等 9 大飞行阶段。
- 🚨 **航空级分级报警系统**：内置高保真的优先级警报逻辑（失速 > 超速 > AP 断开 > 主警报 > 起落架未放 > VATSIM 掉线），支持硬件拨轮物理消音及状态打断。
- ☁️ **云端气象同步**：自动抓取起飞、降落与备降机场的 METAR、TAF 及 NOTAM 报文，并支持智能长文折行滚动。
- 🛠️ **全脱机调试模式**：脱离游戏环境，仅靠修改 JSON 文件即可完成硬件 UI 与声光反馈的全部调试。

---

## 🚀 使用说明

## 📦 1. 准备工作
确保您的资料包内包含以下文件，并已安装 [**.NET Desktop Runtime**] 运行环境：
* `fapmDriver.exe` (PC端驱动程序)
* `fsuipc-lvar-module` (MSFS 社区通信模块)

## 🛠️ 2. 安装通信模块 (关键)
将 `fsuipc-lvar-module` 文件夹复制并放入 MSFS 的 `Community` (社区) 文件夹中。
> *注 1：放入后需重启模拟飞行，以便引擎加载该模块。*
> *注 2：**若您的社区文件夹中已有该文件夹**（其他软件或外设可能已安装过），则**无需重复放置**，直接跳过此步即可。*

## 💻 3. 运行 PC 端驱动
1. **右键以“管理员身份”运行** `fapmDriver.exe`（必须使用管理员权限，否则无法读取 CPU/GPU 硬件温度与负载）。
2. 首次运行请在 Windows 防火墙提示中勾选**允许通过网络**，以确保 UDP 和 WebSocket 通信正常。
3. 在软件界面填入您的 **SimBrief ID** 和 **VATSIM CID**，点击“保存设置”。

## 📶 4. 仪表配网连接
1. 给桌面仪表通电。
2. 使用手机或电脑连接 Wi-Fi 热点：**`FAPM-Setup`**。
3. 在自动弹出的配网网页中（或浏览器访问 `192.168.4.1`），填入：
   * 家中 2.4G Wi-Fi 的名称与密码。
   * **PC IP Address**: **留空即可（支持局域网自动搜索连接）**。若自动搜索失败，可重置设备后在此手动填入 PC 端的局域网 IP（在电脑端软件界面中可查看）。
4. 点击保存，仪表将重启并自动连接电脑。

## 🕹️ 5. 仪表操作说明
* **页面切换（拨轮上下滚动）**：
  * 第 1 页：核心飞行数据与自动驾驶/起落架状态。
  * 第 2/3 页：起飞/降落机场实时气象。
  * 第 4 页：电脑硬件性能监控（CPU/GPU/内存）。
* **功能按键（短按拨轮）**：
  * 在气象页：一键切换中/英文 METAR 报文。
  * 触发警报时：消除红色主警报与本地声光轰鸣。
* **参数调节**：
  * 在 PC 软件界面中，可直接拖动滑块实时调节仪表的**屏幕亮度、警告灯亮度及音量**。

## 🔄 6. 恢复出厂设置 (重置网络)
若需更换路由器或修改电脑 IP：
断开仪表电源后重新通电，**在屏幕亮起的瞬间（显示 INITIALIZING 时）长按拨轮不松手**，直到屏幕全红提示擦除数据，松开后即可重新配网。

---

## 📡 WebSocket 接口字段详解

PC 端作为 WebSocket Server 监听 `0.0.0.0:8765`，下发给硬件的核心 JSON 数据结构如下（`type: "flight_data"`）：

### `vatsim` (网络连飞状态)
| 字段名 | 类型 | 说明 |
| --- | --- | --- |
| `online` | Boolean | 当前是否连接在 VATSIM 网络上 |
| `callsign` | String | VATSIM 呼号 |

### `simbrief` (飞行计划与气象)
| 字段名 | 类型 | 说明 |
| --- | --- | --- |
| `callsign` | String | 航班号 (如 CSN1234) |
| `origin` / `destination` / `alternate` | String | 起飞 / 降落 / 备降机场的 ICAO 代码 |
| `orig_wx` / `dest_wx` / `altn_wx` | Object | 包含 `metar`, `taf`, `notam_preview` 的气象与通告对象 |

### `telemetry` (核心遥测数据与状态机)
| 字段名 | 类型 | 说明 |
| --- | --- | --- |
| `ias` / `mach` / `gs` | Number | 指示空速(节) / 马赫数 / 地速(节) |
| `alt_baro` / `alt_radio` | Number | 气压高度(英尺) / 无线电高度(英尺) |
| `vs` | Number | 垂直速度 (英尺/分钟) |
| `ap1` / `ap2` / `athr` | Boolean | 自动驾驶1 / 自动驾驶2 / 自动油门 接通状态 |
| `gear_down` / `spoilers` | Boolean/Int | 起落架是否放下 / 减速板放出百分比 |
| `wp_dist` / `fuel_kg` | Number | 距下一航点距离(海里) / 剩余总油量(千克) |
| `com1` | Number | COM1 激活频率 (如 122.800) |
| `eng1` / `eng2` / `on_ground` | Boolean | 引擎1运行状态 / 引擎2运行状态 / 是否在地面 |
| `gs_overspeed` | Boolean | **滑行超速标志** (滑行阶段地速 > 25 节时为 true) |
| `flight_phase` | String | **当前飞行阶段** (`Prep`, `TaxiOut`, `Climb`, `Cruise`, `Descent`, `Approach`, `Landing`, `TaxiIn`, `Complete`) |
| `active_alarm` | String | **当前最高优先级警报** (`STALL`, `OVERSPEED`, `AP_DISC`, `WARNING`, `CAUTION`, `GEAR`, `VATSIM`, `NONE` 即无警报) |
| `master_warning` / `master_caution`| Boolean | 原始主警报 / 主注意状态灯 |
| `sim_time` | String | 游戏内 Zulu 时间 (格式 `HH:mm:ss`) |

*(注：ESP32 硬件可通过发送 `{"action": "alarm_ack"}` 指令给 PC 端，以静音当前的 `active_alarm` 警报。)*

---

## 🛠️ 调试模式 (Debug Mode)

为了在未开启《微软模拟飞行》的情况下开发和排错 ESP32 硬件 UI，本程序内置了强大的“上帝脱机调试模式”。

### 开启方法
1. 在本程序 `FapmDriver.exe` 所在的**同级目录**下，新建一个文本文档并重命名为 `debug.json`。
2. 将下方的模板内容粘贴进该文件并保存。
3. PC 程序将立刻识别到该文件，UI 面板将变橙并提示进入调试模式。此时程序会彻底切断与 MSFS 和网络的连接，转而以 **1Hz** 的频率将 `debug.json` 的内容原样下发给 ESP32。

### 实时热更新
在调试模式下，你无需重启 PC 服务端。直接使用记事本修改 `debug.json` 里的参数（例如把 `active_alarm` 改为 `"AP_DISC"` 或改变 `gs` 数值），按下 `Ctrl+S` 保存的瞬间，硬件屏幕将立即展现对应的 UI 变化和警报声光反馈！

### `debug.json` 测试模板

```json
{
  "type": "flight_data",
  "vatsim": {
    "online": true,
    "callsign": "CSN1234"
  },
  "simbrief": {
    "callsign": "CSN1234",
    "origin": "ZJHK",
    "destination": "ZUUU",
    "alternate": "ZGGG",
    "orig_wx": {
      "metar": "ZJHK 240000Z 12004MPS 9999 BKN030 25/20 Q1012 NOSIG",
      "taf": "TAF ZJHK 240000Z 2400/2506 12004MPS 9999 BKN030 \nTEMPO 2406/2412 15006G12MPS -SHRA",
      "notam_preview": "A0001/26 NOTAMN\nQ) ZJSA/QMXLC/IV/M/A/000/999/2000N11020E005\nA) ZJHK\nB) 2608240000\nC) 2608252359\nE) TWY A CLOSED DUE TO WIP."
    },
    "dest_wx": {
      "metar": "ZUUU 240000Z 12004MPS 9999 BKN030 20/15 Q1015 NOSIG",
      "taf": "",
      "notam_preview": ""
    },
    "altn_wx": {
      "metar": "ZGGG 240000Z 12004MPS 9999 SCT030 26/22 Q1010 NOSIG",
      "taf": "",
      "notam_preview": ""
    }
  },
  "telemetry": {
    "ias": 250,
    "mach": 0.650,
    "gs": 420,
    "alt_baro": 25000,
    "alt_radio": 2