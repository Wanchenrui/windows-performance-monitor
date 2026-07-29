"use strict";

const COLORS = {
  green: "#34d399",
  yellow: "#fbbf24",
  red: "#f87171",
  purple: "#818cf8",
  unavailable: "#4a5165",
};

function byId(id) {
  return document.getElementById(id);
}

function finite(value) {
  return typeof value === "number" && Number.isFinite(value);
}

function clampPercent(value) {
  return Math.max(0, Math.min(100, value));
}

function percentColor(value) {
  return value < 60 ? COLORS.green : value < 85 ? COLORS.yellow : COLORS.red;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (character) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;",
  })[character]);
}

function formatGiB(bytes, digits = 1) {
  if (!finite(bytes)) {
    return "--";
  }
  return (bytes / (1024 ** 3)).toFixed(digits);
}

function formatMiB(bytes) {
  if (!finite(bytes)) {
    return "不可用";
  }
  return `${Math.round(bytes / (1024 ** 2))} MiB`;
}

function formatUptime(seconds) {
  if (!finite(seconds)) {
    return "不可用";
  }
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) {
    return `${days} 天 ${hours} 小时`;
  }
  return `${hours} 小时 ${minutes} 分钟`;
}

function formatTimestamp(isoString) {
  if (!isoString) {
    return "--";
  }
  const date = new Date(isoString);
  if (Number.isNaN(date.getTime())) {
    return isoString;
  }
  return date.toLocaleString("zh-CN", { hour12: false });
}

function makeGauge(id) {
  const element = byId(id);
  const circumference = 2 * Math.PI * 45;
  element.innerHTML =
    `<svg width="100%" height="100%" viewBox="0 0 110 110">` +
    `<circle class="track" cx="55" cy="55" r="45" fill="none" stroke-width="10"/>` +
    `<circle class="prog" cx="55" cy="55" r="45" fill="none" stroke-width="10" ` +
    `stroke-linecap="round" stroke-dasharray="${circumference}" ` +
    `stroke-dashoffset="${circumference}"/></svg>` +
    `<div class="center"><div class="val">--</div><div class="unit">%</div></div>`;

  const progress = element.querySelector(".prog");
  const valueElement = element.querySelector(".val");
  return (value) => {
    if (!finite(value)) {
      progress.style.strokeDashoffset = circumference;
      progress.style.stroke = COLORS.unavailable;
      valueElement.textContent = "--";
      valueElement.style.color = COLORS.unavailable;
      return;
    }
    const safeValue = clampPercent(value);
    progress.style.transition = "stroke-dashoffset 0.4s ease";
    progress.style.strokeDashoffset = circumference * (1 - safeValue / 100);
    progress.style.stroke = percentColor(safeValue);
    valueElement.textContent = safeValue.toFixed(1);
    valueElement.style.color = percentColor(safeValue);
  };
}

const setCpu = makeGauge("g-cpu");
const setMemory = makeGauge("g-mem");
const setDisk = makeGauge("g-disk");

function drawChart(samples, windowSeconds, sampleIntervalSeconds) {
  const svg = byId("chart");
  const width = 600;
  const height = 200;
  const padding = 10;
  const nowMs = Date.now();
  const latestSampleMs = samples.length ? samples[samples.length - 1].epochMs : nowMs;
  const endMs = Math.max(nowMs, finite(latestSampleMs) ? latestSampleMs : nowMs);
  const spanMs = Math.max(60_000, windowSeconds * 1000);
  const startMs = endMs - spanMs;
  const visible = samples.filter((sample) => (
    finite(sample.epochMs) && sample.epochMs >= startMs && sample.epochMs <= endMs
  ));

  let content = "";
  for (let index = 0; index <= 4; index += 1) {
    const y = padding + (height - 2 * padding) * index / 4;
    content +=
      `<line x1="0" y1="${y}" x2="${width}" y2="${y}" stroke="#1d2434"/>` +
      `<text x="4" y="${y - 3}" fill="#4a5165" font-size="10">` +
      `${100 - index * 25}</text>`;
  }

  function path(metric, color) {
    let definition = "";
    let previousEpoch = null;
    const gapThresholdMs = Math.max(2500, sampleIntervalSeconds * 2500);
    for (const sample of visible) {
      const value = sample[metric];
      if (!finite(value)) {
        previousEpoch = null;
        continue;
      }
      const x = padding + (width - 2 * padding) *
        ((sample.epochMs - startMs) / spanMs);
      const y = padding + (height - 2 * padding) *
        (1 - clampPercent(value) / 100);
      const startsSegment = previousEpoch === null ||
        sample.epochMs - previousEpoch > gapThresholdMs;
      definition += `${startsSegment ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)} `;
      previousEpoch = sample.epochMs;
    }
    return definition
      ? `<path d="${definition}" fill="none" stroke="${color}" ` +
        `stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>`
      : "";
  }

  content += path("cpu", COLORS.green);
  content += path("mem", COLORS.purple);
  svg.innerHTML = content;
}

function renderHealth(data) {
  const health = data.health || {};
  const sample = data.sample || {};
  const status = health.status || "error";
  const banner = byId("health-banner");
  const statusText = {
    starting: "等待首次采样",
    ok: "采集正常",
    partial: "部分指标不可用",
    error: "采集失败",
    stale: "数据已陈旧",
  }[status] || "状态未知";
  const detailParts = [];
  if (finite(health.ageSeconds)) {
    detailParts.push(`数据年龄 ${health.ageSeconds.toFixed(1)} 秒`);
  }
  if (finite(sample.intervalSeconds)) {
    detailParts.push(`周期 ${sample.intervalSeconds.toFixed(2)} 秒`);
  }
  if (finite(sample.durationMs)) {
    detailParts.push(`采集 ${sample.durationMs.toFixed(1)} 毫秒`);
  }

  banner.className = `health ${status}`;
  byId("health-title").textContent = statusText;
  byId("health-detail").textContent = detailParts.join(" · ") || "采集器正在初始化";
  byId("sample-meta").textContent =
    `序号 #${data.sequence || 0} · 抖动 ${finite(sample.jitterMs) ?
      sample.jitterMs.toFixed(1) : "--"} ms · 累计缺口 ` +
    `${sample.missedIntervalsTotal || 0}`;

  const errors = Array.isArray(health.errors) ? health.errors : [];
  const errorPanel = byId("error-panel");
  errorPanel.hidden = errors.length === 0;
  byId("error-list").innerHTML = errors.map((issue) =>
    `<li><b>${escapeHtml(issue.metric || "unknown")}</b>：` +
    `${escapeHtml(issue.message || issue.code || "未知错误")}</li>`
  ).join("");
}

function renderOverview(data) {
  const overview = data.overview || {};
  const cpu = overview.cpu || {};
  const memory = overview.memory || {};
  const disk = overview.systemDisk || {};

  setCpu(cpu.percent);
  setMemory(memory.percent);
  setDisk(disk.percent);

  byId("cpu-sub").textContent = finite(cpu.logicalCpuCount)
    ? `整机归一化 · ${cpu.logicalCpuCount} 个逻辑处理器`
    : "处理器指标不可用";
  byId("cpu-sub").title = cpu.source || "";

  byId("mem-sub").textContent = finite(memory.usedBytes) && finite(memory.totalBytes)
    ? `${formatGiB(memory.usedBytes)} / ${formatGiB(memory.totalBytes)} GiB`
    : "内存指标不可用";
  byId("mem-sub").title = memory.source || "";

  const diskName = disk.device || disk.mountpoint || "系统盘";
  byId("disk-label").textContent = diskName;
  byId("disk-sub").textContent = finite(disk.freeBytes) && finite(disk.totalBytes)
    ? `剩余 ${formatGiB(disk.freeBytes)} / ${formatGiB(disk.totalBytes)} GiB`
    : "磁盘指标不可用";
  byId("disk-sub").title = disk.source || "";

  if (finite(memory.percent)) {
    const safePercent = clampPercent(memory.percent);
    byId("mem-bar").style.width = `${safePercent}%`;
    byId("mem-bar-text").textContent = `${safePercent.toFixed(1)}%`;
  } else {
    byId("mem-bar").style.width = "0";
    byId("mem-bar-text").textContent = "不可用";
  }

  byId("uptime").textContent = formatUptime(overview.uptimeSeconds);
}

function renderProcesses(data) {
  const processes = Array.isArray(data.processes) ? data.processes : [];
  const processMeta = data.processCollection || {};
  const coverage = processMeta.status === "limited"
    ? `覆盖受限 · 枚举 ${processMeta.enumerated || 0}，无法读取 ${processMeta.skipped || 0}`
    : `完整覆盖 · 枚举 ${processMeta.enumerated || 0}`;
  byId("proc-ts").textContent =
    `${formatTimestamp(data.collectedAt)} · ${coverage}`;

  if (processes.length === 0) {
    byId("plist").innerHTML = '<div class="empty">进程指标不可用或仍在建立基线</div>';
    return;
  }

  byId("plist").innerHTML = processes.map((process, index) => {
    const normalized = finite(process.cpuNormalizedPct)
      ? `${process.cpuNormalizedPct.toFixed(1)}%`
      : "采集中";
    const coreEquivalent = finite(process.cpuCoreEquivalentPct)
      ? `${process.cpuCoreEquivalentPct.toFixed(1)}%`
      : "--";
    return (
      `<div class="process-row">` +
      `<div class="process-name" title="${escapeHtml(process.name)}">` +
      `<span class="rank">${index + 1}</span>${escapeHtml(process.name)}` +
      ` <span class="pid">PID ${process.pid}</span></div>` +
      `<div class="process-metrics">整机 CPU <b>${normalized}</b> · ` +
      `工作集 ${formatMiB(process.workingSetBytes)} · ` +
      `私有内存 ${formatMiB(process.privateBytes)}` +
      `<div class="process-detail">核心等效占用 ${coreEquivalent}</div></div>` +
      `</div>`
    );
  }).join("");
}

function render(data) {
  byId("app-version").textContent = `v${data.appVersion || "--"}`;
  renderHealth(data);
  renderOverview(data);
  renderProcesses(data);

  const historyMinutes = Math.round((data.historyWindowSeconds || 3600) / 60);
  byId("chart-title").textContent =
    `性能趋势 · 最近 ${historyMinutes} 分钟（真实时间）`;
  drawChart(
    Array.isArray(data.history) ? data.history : [],
    data.historyWindowSeconds || 3600,
    (data.sample || {}).intervalSeconds || 1
  );
  byId("ts").textContent = data.collectedAt
    ? `更新于 ${formatTimestamp(data.collectedAt)}`
    : "等待首次采样";
}

function renderDisconnected(error) {
  const banner = byId("health-banner");
  banner.className = "health disconnected";
  byId("health-title").textContent = "本地服务连接失败";
  byId("health-detail").textContent = "正在重试；当前界面数据不可视为实时数据";
  byId("sample-meta").textContent = error instanceof Error ? error.message : "网络错误";
  byId("ts").textContent = "连接失败，重试中…";
}

async function refresh() {
  try {
    const response = await fetch("/api/stats", { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
    render(await response.json());
  } catch (error) {
    renderDisconnected(error);
  } finally {
    window.setTimeout(refresh, 1000);
  }
}

refresh();
