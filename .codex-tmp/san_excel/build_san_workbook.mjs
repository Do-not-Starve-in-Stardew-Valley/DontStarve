import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const rootDir = path.resolve(".");
const inputJson = path.join(rootDir, ".codex-tmp", "san_excel", "san_doc.json");
const outputDir = path.join(rootDir, "outputs", "san_excel");
const outputPath = path.join(outputDir, "san值系统设计数据整理.xlsx");

const source = JSON.parse(await fs.readFile(inputJson, "utf8"));

const colors = {
  title: "#17324D",
  header: "#1F4E78",
  subHeader: "#D9EAF7",
  blue: "#DCEBFF",
  green: "#E2F0D9",
  yellow: "#FFF2CC",
  red: "#FCE4D6",
  gray: "#F3F4F6",
  border: "#D9E2EC",
  text: "#1F2937",
  muted: "#64748B",
};

const featureRows = [
  ["F-001", "基础设定", "待核对/已有口述", "基础数值", "默认", "理智值上限 = 200", "玩家 San 值默认上限为 200 点。", "", "源段落 8"],
  ["F-002", "基础设定", "待核对/已有口述", "叠加规则", "所有理智影响", "加算", "所有理智影响均为加算。", "", "源段落 9"],
  ["F-003", "时间与装备", "待核对/已有口述", "装备光环", "装备穿戴/拿起显示", "每刻 +/- X；显示 /计时秒数", "装备按内部时间刻度回 San 或降 San；拿起时显示“理智光环：/{计时秒数}”。", "", "源段落 7"],
  ["F-004", "食物", "待核对/已有口述", "食物恢复", "吃东西", "文档未列具体数值", "掉 San 与吃东西回 San 口述为“应该做完了”。", "", "源段落 6"],
  ["F-005", "环境理智", "待核对/已有口述", "环境扣减", "矿井 / 黄昏 / 夜晚", "文档未列具体数值", "矿井和黄昏夜晚掉 San 口述为“应该做了”。", "", "源段落 10"],
  ["F-006", "周边单位", "待核对/已有口述", "NPC/怪物影响", "靠近 NPC 或怪物", "文档未列具体数值", "靠近 NPC 或怪物影响 San 口述为“应该也做了”。", "", "源段落 11"],
  ["F-007", "睡眠", "未做/待实现", "恢复/惩罚", "早睡每游戏内 10 分钟；直接晕倒", "+3 San / 10 游戏分钟；晕倒 -20 且不回复", "早睡按游戏内时间多回复 San；直接晕倒不回复并扣 20 San。", "", "源段落 14"],
  ["F-008", "死亡/重生", "未做/待实现", "死亡恢复", "玩家死亡重生后", "恢复为上限的一半", "不用写死 100，后续人物系统会有不同 San 上限。", "", "源段落 15"],
  ["F-009", "劳累昏迷", "未做/待实现", "惩罚", "玩家劳累昏迷后", "-20 San", "劳累昏迷扣除 20 San。", "", "源段落 16"],
  ["F-010", "夜间昏迷", "未做/待实现", "特殊死亡/剧情", "晚上 2 点昏迷在非农场房屋", "判定死亡；凤凰戒指无效", "第二天在哈维诊所醒来，并在邮件里添加一封信件。", "需要邮件文本", "源段落 17"],
  ["F-011", "低理智阈值", "未做/部分待核对", "生成", "San < 83.5%", "5-10 格", "生成 Mr.Skitts；无血量和攻击；靠近后消失。", "暂无贴图", "源段落 19"],
  ["F-012", "低理智阈值", "未做/部分待核对", "干扰/视觉", "San < 75%", "10-20 格", "暗影之手生成，停止玩家机器、掐灭篝火光源；视野不稳定，饱和度降低。", "暂无贴图", "源段落 20"],
  ["F-013", "低理智阈值", "未做/部分待核对", "生成", "San < 65%", "5-10 格", "生成暗影观察者；无血量和攻击；靠近后消失。", "暂无贴图", "源段落 21"],
  ["F-014", "低理智阈值", "未做/部分待核对", "视觉", "San < 60% 且晚上", "5-10 格", "出现眼睛盯着玩家；屏幕开始摇晃。", "", "源段落 22"],
  ["F-015", "低理智阈值", "未做/部分待核对", "生成/音频", "San < 50%", "5-15 格；30 游戏分钟 / 21 秒；最多 2 只", "生成爬行恐惧和恐怖尖喙；不可攻击、不主动攻击、靠近消失；播放低理智背景声音。", "低san音效文件夹", "源段落 23-24"],
  ["F-016", "低理智阈值", "未做/待实现", "音频", "San < 45%", "连续/随机播放", "可以听见低语；和低 San 音效同时播放，做不到则两者同时随机。", "低san低语音效文件夹", "源段落 25"],
  ["F-017", "低理智阈值", "未做/待实现", "物品/贴图替换", "San < 40%", "无", "白兔/灰兔变胡须兔；地上觅食的兔绒、肉类、兔子腿分别变胡须、怪物肉、虚空精华。", "胡须兔贴图暂无", "源段落 26"],
  ["F-018", "低理智阈值", "未做/待实现", "战斗化/视觉", "San < 15%；San > 17.5% 恢复", "20 游戏分钟 / 14 秒补 1 只；最多 2 只", "播放阈值音效；屏幕四周红色卷曲贴图；已有爬行恐惧/恐怖尖喙拥有血量、可攻击、主动攻击低 San 玩家。", "低san阈值音效；红色卷曲贴图", "源段落 27"],
  ["F-019", "低理智阈值", "未做/待实现", "击杀恢复", "击杀可战斗怪物", "爬行恐惧 +15 San；恐怖尖喙 +33 San", "刷新位置仍为玩家周围 5-15 格。", "", "源段落 28"],
  ["F-020", "低理智阈值", "未做/待实现", "强化生成/滤镜", "San < 10%", "10 游戏分钟 / 7 秒；最多 3 只", "恐怖尖喙可被刷出且总上限提升；刷新速度加快；画面失真变灰白。", "灰白滤镜", "源段落 29"],
  ["F-021", "低理智阈值", "未做/待实现", "待机动作", "低 San 背景音段落中提到：玩家一段时间不操作", "时间未定", "玩家不操作一段时间后做“坐下”动作。", "需定义无操作时长与动作资源", "源段落 24"],
];

const thresholdRows = [
  [0.835, "", "低于", "Mr.Skitts", "5-10 格", "无血量/无攻击，靠近消失", "", "", "", "暂无贴图", "源段落 19"],
  [0.75, "", "低于", "暗影之手", "10-20 格", "停止机器、掐灭篝火", "视野不稳定；饱和度降低", "", "", "暂无贴图", "源段落 20"],
  [0.65, "", "低于", "暗影观察者", "5-10 格", "无血量/无攻击，靠近消失", "", "", "", "暂无贴图", "源段落 21"],
  [0.6, "", "低于", "眼睛", "5-10 格", "晚上出现眼睛盯着玩家", "屏幕摇晃", "", "", "", "源段落 22"],
  [0.5, "", "低于", "爬行恐惧 + 恐怖尖喙", "5-15 格", "30 游戏分钟 / 21 秒刷新 1 只；最多 2 只；不主动攻击、不可攻击、靠近消失", "", "低理智背景声音", "", "低san音效", "源段落 23-24"],
  [0.45, "", "低于", "", "", "", "", "低语；与低 San 音效同时播放或同时随机", "", "低san低语音效", "源段落 25"],
  [0.4, "", "低于", "", "", "", "", "", "兔子/拾取物转化", "胡须兔贴图暂无", "源段落 26"],
  [0.15, 0.175, "低于出现/高于恢复", "爬行恐惧 + 恐怖尖喙战斗化", "5-15 格", "可攻击、有血量、主动攻击低 San 玩家；20 游戏分钟 / 14 秒补爬行恐惧；最多 2 只", "红色卷曲贴图", "阈值音效", "", "低san阈值音效；卷曲贴图", "源段落 27-28"],
  [0.1, "", "低于", "恐怖尖喙强化", "5-15 格", "恐怖尖喙可刷出；最多 3 只；10 游戏分钟 / 7 秒刷新", "灰白滤镜/画面失真", "", "", "灰白滤镜", "源段落 29"],
];

const monsterStatRows = [
  ["爬行恐惧", 300, 2.5, "无法小数则向上取整", 20, "1-2 个虚空精华；99% + 51% 口径待实现确认", "5-15 格", "20 格", "3 格", "6 格", "低于 50% 首次无攻击；低于 15% 战斗化", "暂无，画师处理中", "源段落 30-44"],
  ["恐怖尖喙", 400, 6, "", 50, "1-2 个虚空精华", "5-15 格", "20 格", "4 格", "8 格", "低于 50% 首次无攻击；低于 10% 上限/刷新增强", "附件文件；含跑动、攻击、死亡、生成、静止、恐吓动画行", "源段落 45-70"],
];

const behaviorRows = [
  ["爬行恐惧", 1, "生成动画完成后", "索敌到玩家 20 格", "恐吓，然后直线最短路径移动", "可穿墙，无视地形", "源段落 38"],
  ["爬行恐惧", 2, "靠近玩家", "距离玩家 3 格", "播放攻击动画并冲刺", "冲刺 6 格；无蓄力", "源段落 39"],
  ["爬行恐惧", 3, "攻击命中", "命中玩家", "恐吓判定后继续靠近/攻击循环", "50% 恐吓；25% 反方向移动 6 格；25% 直接下一步", "源段落 40"],
  ["爬行恐惧", 4, "被攻击", "玩家攻击它", "播放死亡动画，随机传送到玩家周围 5-15 格", "50% 恐吓；50% 直接下一步；若正在攻击则打断攻击动画", "源段落 41"],
  ["爬行恐惧", 5, "攻击扑空", "攻击未命中", "恐吓判定后继续靠近/攻击循环", "50% 恐吓；50% 直接下一步", "源段落 42"],
  ["爬行恐惧", 6, "未索敌", "没有索敌到玩家", "静息，游戏内 1 小时后消失", "42 秒；一旦索敌，仇恨无限范围且不丢失", "源段落 43"],
  ["爬行恐惧", 7, "死亡", "真正死亡", "播放死亡动画", "", "源段落 44"],
  ["恐怖尖喙", 1, "生成动画完成后", "索敌到玩家 20 格", "恐吓，然后直线最短路径移动", "可穿墙，无视地形", "源段落 64"],
  ["恐怖尖喙", 2, "靠近玩家", "距离玩家 4 格", "播放攻击动画并冲刺", "冲刺 8 格；无蓄力", "源段落 65"],
  ["恐怖尖喙", 3, "攻击命中", "命中玩家", "恐吓判定后继续靠近/攻击循环", "50% 恐吓；25% 反方向移动 6 格；25% 直接下一步", "源段落 66"],
  ["恐怖尖喙", 4, "被攻击", "玩家攻击它", "播放死亡动画，随机传送到玩家周围 5-15 格", "50% 恐吓；50% 直接下一步；若正在攻击则打断攻击动画", "源段落 67"],
  ["恐怖尖喙", 5, "攻击扑空", "攻击未命中", "恐吓判定后继续靠近/攻击循环", "50% 恐吓；50% 直接下一步", "源段落 68"],
  ["恐怖尖喙", 6, "未索敌", "没有索敌到玩家", "静息，游戏内 1 小时后消失", "42 秒；一旦索敌，仇恨无限范围且不丢失", "源段落 69"],
  ["恐怖尖喙", 7, "死亡", "真正死亡", "播放死亡动画", "", "源段落 70"],
];

const resourceRows = [
  ["贴图", "Mr.Skitts", "缺失", "San < 83.5% 生成怪物", "需要画师提供", "源段落 19"],
  ["贴图", "暗影之手", "缺失", "San < 75% 干扰", "需要画师提供", "源段落 20"],
  ["贴图", "暗影观察者", "缺失", "San < 65% 生成怪物", "需要画师提供", "源段落 21"],
  ["贴图", "胡须兔", "缺失", "San < 40% 替换白兔/灰兔", "需要画师提供", "源段落 26"],
  ["贴图", "红色卷曲屏幕边缘", "需求", "San < 15%", "需要资源文件", "源段落 27"],
  ["滤镜", "饱和度降低", "需求", "San < 75%", "技术可行性待确认", "源段落 20"],
  ["滤镜", "灰白滤镜/画面失真", "需求", "San < 10%", "技术可行性待确认", "源段落 29"],
  ["音频", "低san音效", "已有文件夹口述", "San < 50% 连续随机背景声", "需要确认资源路径和循环/随机策略", "源段落 24"],
  ["音频", "低san低语音效", "已有文件夹口述", "San < 45% 低语", "做不到同时播放则两类同时随机", "源段落 25"],
  ["音频", "低san阈值音效", "已有文件口述", "San < 15% 阈值触发", "需要确认播放一次/冷却", "源段落 27"],
  ["动画", "恐怖尖喙动画行", "附件口述", "跑动/攻击/死亡/生成/静止/恐吓", "每横排行为：下/右/上/左跑、四向攻击、死亡、生成、静止、恐吓", "源段落 50-62"],
  ["文本", "哈维诊所邮件", "待提供", "晚上 2 点非农场房屋昏迷死亡后", "用户可提供文本", "源段落 17"],
  ["动画", "玩家坐下动作", "需求", "低 San 且玩家一段时间不操作", "需定义无操作时长和动画来源", "源段落 24"],
];

const timingRows = [
  ["游戏内 1 分钟", "0.7 秒", "由文档 10 分钟=7 秒、20 分钟=14 秒、30 分钟=21 秒、1 小时=42 秒推导"],
  ["游戏内 10 分钟", "7 秒", "睡眠恢复、低理智刷新例子"],
  ["游戏内 20 分钟", "14 秒", "San < 15% 补充爬行恐惧"],
  ["游戏内 30 分钟", "21 秒", "San < 50% 刷新一只"],
  ["游戏内 1 小时", "42 秒", "怪物未索敌静息消失"],
  ["现实 1 秒", "约 1.43 个内部分钟 tick", "1000ms / 700ms"],
];

const workbook = Workbook.create();

function addSheet(name) {
  const sheet = workbook.worksheets.add(name);
  sheet.showGridLines = false;
  return sheet;
}

function colLetter(n) {
  let s = "";
  while (n > 0) {
    const m = (n - 1) % 26;
    s = String.fromCharCode(65 + m) + s;
    n = Math.floor((n - 1) / 26);
  }
  return s;
}

function writeTitle(sheet, title, subtitle, colCount) {
  const end = colLetter(colCount);
  sheet.getRange(`A1:${end}1`).merge();
  sheet.getRange("A1").values = [[title]];
  sheet.getRange("A1").format = {
    fill: colors.title,
    font: { bold: true, color: "#FFFFFF", size: 16 },
  };
  sheet.getRange("A1").format.rowHeight = 30;
  sheet.getRange(`A2:${end}2`).merge();
  sheet.getRange("A2").values = [[subtitle]];
  sheet.getRange("A2").format = {
    fill: colors.gray,
    font: { color: colors.muted, size: 10 },
    wrapText: true,
  };
  sheet.getRange("A2").format.rowHeight = 36;
}

function writeTable(sheet, startRow, headers, rows, tableName) {
  const colCount = headers.length;
  const rowCount = rows.length + 1;
  const startCol = "A";
  const endCol = colLetter(colCount);
  const rangeAddress = `${startCol}${startRow}:${endCol}${startRow + rowCount - 1}`;
  sheet.getRange(rangeAddress).values = [headers, ...rows];
  sheet.getRange(`${startCol}${startRow}:${endCol}${startRow}`).format = {
    fill: colors.header,
    font: { bold: true, color: "#FFFFFF" },
    wrapText: true,
  };
  sheet.getRange(rangeAddress).format.borders = {
    insideHorizontal: { style: "thin", color: colors.border },
    insideVertical: { style: "thin", color: colors.border },
    top: { style: "thin", color: colors.border },
    bottom: { style: "thin", color: colors.border },
    left: { style: "thin", color: colors.border },
    right: { style: "thin", color: colors.border },
  };
  sheet.getRange(rangeAddress).format.wrapText = true;
  sheet.tables.add(rangeAddress, true, tableName);
  sheet.freezePanes.freezeRows(startRow);
  return { rangeAddress, startRow, rowCount, colCount };
}

function setWidths(sheet, widths) {
  widths.forEach((width, index) => {
    const letter = colLetter(index + 1);
    sheet.getRange(`${letter}:${letter}`).format.columnWidth = width;
  });
}

function styleStatusRows(sheet, startRow, rows, statusColIndex) {
  rows.forEach((row, index) => {
    const status = row[statusColIndex - 1] || "";
    const rowNumber = startRow + 1 + index;
    const target = sheet.getRange(`A${rowNumber}:${colLetter(row.length)}${rowNumber}`);
    if (status.includes("未做")) {
      target.format.fill = colors.red;
    } else if (status.includes("已有")) {
      target.format.fill = colors.yellow;
    } else {
      target.format.fill = "#FFFFFF";
    }
  });
}

const overview = addSheet("总览");
writeTitle(
  overview,
  "San 值系统设计数据整理",
  `来源：${source.source}。本工作簿按文档口径整理，不代表当前源码已全部实现。原 Word 未检测到可读文字颜色属性，因此红/绿/蓝优先级只保留为说明。`,
  8
);
overview.getRange("A4:B9").values = [
  ["来源文档", source.source],
  ["抽取段落数", source.block_count],
  ["Word 表格数", source.table_count],
  ["整理功能项", featureRows.length],
  ["理智阈值项", thresholdRows.length],
  ["怪物类型", 2],
];
overview.getRange("A4:A9").format = { fill: colors.subHeader, font: { bold: true } };
overview.getRange("A4:B9").format.borders = { preset: "all", style: "thin", color: colors.border };
overview.getRange("D4:F7").values = [
  ["分类", "数量", "说明"],
  ["未做/待实现", featureRows.filter((r) => r[2].includes("未做")).length, "文档明确列在未做或阈值待实现部分"],
  ["待核对/已有口述", featureRows.filter((r) => r[2].includes("已有")).length, "文档口述为已有或需确认"],
  ["资源需求", resourceRows.length, "贴图、音频、滤镜、动画、文本等"],
];
overview.getRange("D4:F4").format = { fill: colors.header, font: { bold: true, color: "#FFFFFF" } };
overview.getRange("D4:F7").format.borders = { preset: "all", style: "thin", color: colors.border };
overview.getRange("A11:C17").values = [
  ["时间换算", "现实时间", "备注"],
  ...timingRows,
];
overview.getRange("A11:C11").format = { fill: colors.header, font: { bold: true, color: "#FFFFFF" } };
overview.getRange("A11:C17").format.borders = { preset: "all", style: "thin", color: colors.border };
overview.getRange("A18:C21").values = [
  ["文档颜色规则", "含义", "本次抽取结果"],
  ["红色", "重要部分，一定需要有", "原 Word 未检测到可读 run 颜色"],
  ["绿色", "不太重要，偏氛围设定，可放弃", "原 Word 未检测到可读 run 颜色"],
  ["蓝色", "疑似难以实现，需商量替代方案", "原 Word 未检测到可读 run 颜色"],
];
overview.getRange("A18:C18").format = { fill: colors.header, font: { bold: true, color: "#FFFFFF" } };
overview.getRange("A19:A19").format.fill = "#F4CCCC";
overview.getRange("A20:A20").format.fill = "#D9EAD3";
overview.getRange("A21:A21").format.fill = "#CFE2F3";
overview.getRange("A18:C21").format.borders = { preset: "all", style: "thin", color: colors.border };
setWidths(overview, [18, 46, 56, 18, 12, 42, 14, 14]);

const features = addSheet("功能清单");
writeTitle(features, "功能清单", "按模块、状态和实现类型拆分，适合筛选下一步开发范围。", 9);
writeTable(
  features,
  4,
  ["ID", "模块", "文档状态", "类型", "触发/条件", "数值/频率", "行为/效果", "资源/依赖", "来源/备注"],
  featureRows,
  "FeatureList"
);
styleStatusRows(features, 4, featureRows, 3);
setWidths(features, [10, 16, 18, 16, 28, 30, 58, 28, 24]);

const thresholds = addSheet("理智阈值");
writeTitle(thresholds, "理智阈值表", "把不同 San 百分比阈值对应的生成、视觉、音频、物品与战斗效果拆成列。", 11);
writeTable(
  thresholds,
  4,
  ["触发阈值", "恢复阈值", "关系", "生成/对象", "距离", "行为/战斗", "视觉", "音频", "物品变化", "素材/技术依赖", "来源"],
  thresholdRows,
  "SanThresholds"
);
thresholds.getRange("A5:B13").format.numberFormat = "0.0%";
setWidths(thresholds, [12, 12, 18, 22, 16, 52, 28, 32, 28, 34, 18]);

const monsters = addSheet("怪物参数");
writeTitle(monsters, "怪物参数", "爬行恐惧和恐怖尖喙的数值、距离、攻击与资源情况。", 13);
writeTable(
  monsters,
  4,
  ["怪物", "生命值", "移速", "移速备注", "伤害", "掉落物", "刷新距离", "索敌范围", "攻击距离", "冲刺距离", "出现/战斗条件", "贴图/动画资源", "来源"],
  monsterStatRows,
  "MonsterStats"
);
monsters.getRange("B5:E6").format.numberFormat = "0.0";
setWidths(monsters, [16, 10, 10, 20, 10, 36, 14, 14, 14, 14, 36, 48, 18]);

const behavior = addSheet("怪物行为");
writeTitle(behavior, "怪物行为流程", "把 AI 行为按步骤拆开，便于直接转任务或状态机。", 7);
writeTable(
  behavior,
  4,
  ["怪物", "步骤", "阶段", "条件", "动作", "概率/距离/备注", "来源"],
  behaviorRows,
  "MonsterBehavior"
);
behavior.getRange("B5:B18").format.numberFormat = "0";
setWidths(behavior, [16, 8, 22, 28, 42, 54, 18]);

const resources = addSheet("资源需求");
writeTitle(resources, "资源与素材需求", "贴图、音频、滤镜、动画、文本等非纯代码依赖。", 6);
writeTable(
  resources,
  4,
  ["资源类型", "资源/对象", "状态", "关联功能", "处理建议", "来源"],
  resourceRows,
  "ResourceNeeds"
);
resources.getRange("C5:C17").format.fill = colors.yellow;
setWidths(resources, [14, 24, 18, 44, 48, 18]);

const raw = addSheet("原文拆解");
writeTitle(raw, "原文拆解", "从 Word 段落抽取的原始文本，便于回查整理是否遗漏。", 7);
const rawRows = source.blocks.map((block, index) => [
  index + 1,
  block.kind,
  block.level || "",
  (block.section_path || []).join(" > "),
  block.text || "",
  JSON.stringify(block.color_summary?.colors || {}),
  JSON.stringify(block.color_summary?.highlights || {}),
]);
writeTable(
  raw,
  4,
  ["段落号", "块类型", "标题级别", "章节路径", "原文", "文字颜色摘要", "高亮摘要"],
  rawRows,
  "SourceParagraphs"
);
raw.getRange("A5:A74").format.numberFormat = "0";
setWidths(raw, [10, 12, 10, 28, 92, 24, 24]);

for (const sheetName of ["总览", "功能清单", "理智阈值", "怪物参数", "怪物行为", "资源需求", "原文拆解"]) {
  const sheet = workbook.worksheets.getItem(sheetName);
  const used = sheet.getUsedRange();
  used.format.wrapText = true;
  used.format.autofitRows();
}

await fs.mkdir(outputDir, { recursive: true });

for (const sheetName of ["总览", "功能清单", "理智阈值", "怪物参数", "怪物行为", "资源需求", "原文拆解"]) {
  const preview = await workbook.render({
    sheetName,
    autoCrop: "all",
    scale: 1,
    format: "png",
  });
  await fs.writeFile(
    path.join(outputDir, `${sheetName}.png`),
    new Uint8Array(await preview.arrayBuffer())
  );
}

const errors = await workbook.inspect({
  kind: "match",
  searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A",
  options: { useRegex: true, maxResults: 300 },
  summary: "final formula error scan",
});
console.log(errors.ndjson);

const overviewCheck = await workbook.inspect({
  kind: "table",
  range: "总览!A1:H22",
  tableMaxRows: 30,
  tableMaxCols: 8,
  maxChars: 4000,
});
console.log(overviewCheck.ndjson);

const xlsx = await SpreadsheetFile.exportXlsx(workbook);
await xlsx.save(outputPath);
console.log(JSON.stringify({ outputPath, sheets: workbook.worksheets.items.length }, null, 2));
