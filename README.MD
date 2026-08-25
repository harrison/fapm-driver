# FAPM-Display 服务端 (PC Driver)

FAPM-Display 是一款专为飞行模拟爱好者打造的软硬件联动工具。本项目为 PC 端服务端程序，负责通过 SimConnect 实时读取微软模拟飞行 (MSFS 2020 / MSFS 2024) 的底层遥测数据，同时整合 SimBrief 飞行计划和 VATSIM 连飞网络状态，通过 WebSocket 将数据结构化下发给 ESP32 硬件显示终端。

## ✨ 核心特性

- ✈️ **全自动飞行状态机**：智能识别准备、滑行、爬升、巡航、下降、进近等 9 大飞行阶段。
- 🚨 **航空级分级报警系统**：内置高保真的优先级警报逻辑（失速 > 超速 > AP 断开 > 主警报 > 起落架未放 > VATSIM 掉线），支持硬件拨轮物理消音及状态打断。
- ☁️ **云端气象同步**：自动抓取起飞、降落与备降机场的 METAR、TAF 及 NOTAM 报文，并支持智能长文折行滚动。
- 🛠️ **全脱机调试模式**：脱离游戏环境，仅靠修改 JSON 文件即可完成硬件 UI 与声光反馈的全部调试。

---

## 🚀 使用说明

### 1. 环境准备
- 操作系统：Windows 10 / 11
- 运行环境：.NET 运行时
- 模拟器：Microsoft Flight Simulator 2020 或 2024

### 2. 启动与初始化
1. 运行生成的 `FapmDriver.exe`。
2. 程序启动时会自动检测 MSFS 社区文件夹，并静默注入必要的 WASM 通信模块（`fsuipc-lvar-module`）。**首次运行请务必重启模拟飞行以使模块生效。**

### 3. 配置账号与同步
1. 在 UI 界面的“网络账号配置”中填入你的 **SimBrief ID** 和 **VATSIM CID**。
2. 按需勾选“开启 VATSIM 离线报警”。
3. 点击 **保存** 按钮，程序将立即唤醒网络线程，拉取你的最新航线、沿途天气及 VATSIM 在线状态。

### 4. 硬件连接
- 将刷入 FAPM-Display 固件的 ESP32 设备接入与 PC 相同的局域网。
- 硬件会自动通过 UDP 广播 (`8766` 端口) 发现 PC 服务端，并建立稳定的 WebSocket 连接 (`8765` 端口)。
- 建立连接后，你可以在 PC 面板上实时拖动滑块，调节硬件屏幕的背光、警告灯亮度以及蜂鸣器音量。

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