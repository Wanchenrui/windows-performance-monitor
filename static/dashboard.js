"use strict";

const COLORS = {
  green: "#34d399",
  yellow: "#fbbf24",
  red: "#f87171",
  purple: "#818cf8",
  unavailable: "#4a5165",
};

const HISTORY_QUERY_MAX_POINTS = 2000;
const CLIENT_HISTORY_POINT_LIMIT = 5000;
let historySamples = [];
let historyDownsampled = false;
let activeInstanceId = null;
let lastSequence = 0;
let historyReady = false;

function byId(id) {
  return document.getElementById(id);
}

function finite(value) {
  return typeof value === "number" && Number.isFinite(value);
}

const ERROR_TEXT = {
  access_denied: "权限不足",
  process_exited: "进程已退出",
  not_supported: "当前系统不支持",
  timeout: "采集超时",
  invalid_data: "采集数据无效",
  resource_exhausted: "系统资源不足",
  provider_failure: "Provider 采集失败",
};

function metricValue(group, metricId) {
  const metrics = group && group.data && !Array.isArray(group.data)
    ? group.data.metrics || {}
    : {};
  const metric = metrics[metricId] || {};
  return Object.hasOwn(metric, "value") ? metric.value : null;
}

function normalizeContractSnapshot(payload) {
  if (payload.contractVersion !== "1.0") {
    return payload;
  }
  const groups = payload.groups || {};
  const cpuGroup = groups.systemCpu || {};
  const memoryGroup = groups.memory || {};
  const volumeGroup = groups.volumes || {};
  const uptimeGroup = groups.uptime || {};
  const processGroup = groups.processes || {};
  const samplerGroup = groups.sampler || {};
  const summary = payload.summary || {};
  const freshness = summary.freshness || "warming_up";
  const availability = summary.availability || "error";
  const availabilityStatus = {
    available: "ok",
    partial: "partial",
    unavailable: payload.sequence ? "error" : "starting",
    not_supported: "partial",
    permission_denied: "partial",
    access_denied: "partial",
    timeout: "partial",
    error: "error",
  }[availability] || "error";
  const completedEpochMs = payload.completedAtUtc
    ? Date.parse(payload.completedAtUtc)
    : null;

  const volumes = Array.isArray(volumeGroup.data)
    ? volumeGroup.data.map((volume) => ({
      device: volume.volumeId,
      mountpoint: volume.mountpoint,
      fileSystem: volume.fileSystem,
      isSystem: volume.isSystem === true,
      percent: metricValue({ data: volume }, "system.volume.utilization.percent"),
      usedBytes: metricValue({ data: volume }, "system.volume.used.bytes"),
      freeBytes: metricValue({ data: volume }, "system.volume.free.bytes"),
      totalBytes: metricValue({ data: volume }, "system.volume.total.bytes"),
      source: (volume.metrics &&
        volume.metrics["system.volume.utilization.percent"] || {}).sourceId || "",
    }))
    : [];
  const processes = Array.isArray(processGroup.data)
    ? processGroup.data.map((process) => ({
      pid: (process.identity || {}).pid,
      name: process.name,
      cpuReady: process.cpuReady === true,
      cpuNormalizedPct: metricValue(
        { data: process },
        "process.cpu.normalized.percent"
      ),
      cpuCoreEquivalentPct: metricValue(
        { data: process },
        "process.cpu.core_equivalent.percent"
      ),
      workingSetBytes: metricValue(
        { data: process },
        "process.memory.working_set.bytes"
      ),
      privateBytes: metricValue(
        { data: process },
        "process.memory.private.bytes"
      ),
    }))
    : [];
  const errors = Object.entries(groups).flatMap(([groupId, group]) =>
    (Array.isArray(group.errors) ? group.errors : []).map((issue) => ({
      metric: issue.metricId || groupId,
      code: issue.errorCode,
      message: ERROR_TEXT[issue.errorCode] || "未知 Provider 错误",
    }))
  );
  const coverage = processGroup.coverage || {};

  return {
    appVersion: payload.productVersion,
    apiVersion: payload.contractVersion,
    instanceId: payload.instanceId,
    sequence: payload.sequence,
    collectedAt: payload.completedAtUtc,
    collectedAtEpochMs: finite(completedEpochMs) ? completedEpochMs : null,
    historyWindowSeconds: (payload.retention || {}).historyWindowSeconds,
    overview: {
      cpu: {
        percent: metricValue(cpuGroup, "system.cpu.utilization.percent"),
        logicalCpuCount: metricValue(
          cpuGroup,
          "system.cpu.logical_processor.count"
        ),
        source: (((cpuGroup.data || {}).metrics || {})[
          "system.cpu.utilization.percent"
        ] || {}).sourceId || "",
      },
      memory: {
        percent: metricValue(memoryGroup, "system.memory.utilization.percent"),
        usedBytes: metricValue(memoryGroup, "system.memory.used.bytes"),
        availableBytes: metricValue(
          memoryGroup,
          "system.memory.available.bytes"
        ),
        totalBytes: metricValue(memoryGroup, "system.memory.total.bytes"),
        source: (((memoryGroup.data || {}).metrics || {})[
          "system.memory.utilization.percent"
        ] || {}).sourceId || "",
      },
      disks: volumes,
      systemDisk: volumes.find((volume) => volume.isSystem) || null,
      uptimeSeconds: metricValue(uptimeGroup, "system.uptime.seconds"),
    },
    processes,
    processCollection: {
      status: coverage.status,
      enumerated: coverage.enumerated,
      skipped: coverage.skipped,
    },
    sample: {
      intervalSeconds: metricValue(
        samplerGroup,
        "sampler.interval.seconds"
      ),
      durationMs: metricValue(
        samplerGroup,
        "sampler.duration.milliseconds"
      ),
      jitterMs: metricValue(
        samplerGroup,
        "sampler.jitter.milliseconds"
      ),
      missedIntervalsTotal: metricValue(
        samplerGroup,
        "sampler.missed_intervals.count"
      ),
      skippedIntervalsAfterSample: metricValue(
        samplerGroup,
        "sampler.skipped_intervals.count"
      ),
    },
    health: {
      status: freshness === "stale" ? "stale" : availabilityStatus,
      availabilityStatus,
      freshness,
      stale: freshness === "stale",
      ageSeconds: payload.dataAgeSeconds,
      errors,
    },
  };
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
  const availability = health.availabilityStatus || health.status || "error";
  const freshness = health.freshness ||
    (health.stale ? "stale" : "fresh");
  const status = freshness === "stale" ? "stale" : availability;
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
  if (freshness === "stale" && availability === "partial") {
    detailParts.push("最近一次采集为部分可用");
  } else if (freshness === "stale" && availability === "error") {
    detailParts.push("最近一次采集失败");
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
  const aggregationLabel = historyDownsampled ? " · 已降采样" : "";
  byId("chart-title").textContent =
    `性能趋势 · 最近 ${historyMinutes} 分钟（真实时间${aggregationLabel}）`;
  drawChart(
    historySamples,
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
    const response = await fetch("/api/v1/snapshot", { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
    const data = normalizeContractSnapshot(await response.json());
    selectInstance(data);
    if (!historyReady) {
      try {
        await loadHistory(data);
      } catch (error) {
        console.warn("历史数据加载失败，将在下一周期重试", error);
      }
    }
    appendSnapshot(data);
    render(data);
  } catch (error) {
    renderDisconnected(error);
  } finally {
    window.setTimeout(refresh, 1000);
  }
}

function selectInstance(data) {
  if (data.instanceId === activeInstanceId) {
    return;
  }
  activeInstanceId = data.instanceId || null;
  historySamples = [];
  historyDownsampled = false;
  lastSequence = 0;
  historyReady = false;
}

async function loadHistory(data) {
  const endMs = finite(data.collectedAtEpochMs)
    ? data.collectedAtEpochMs
    : Date.now();
  const startMs = endMs - (data.historyWindowSeconds || 3600) * 1000;
  const query = new URLSearchParams({
    metrics: "system.cpu.utilization.percent,system.memory.utilization.percent",
    from: String(Math.floor(startMs)),
    to: String(Math.ceil(endMs)),
    maxPoints: String(HISTORY_QUERY_MAX_POINTS),
  });
  const response = await fetch(`/api/v1/history?${query}`, {
    cache: "no-store",
  });
  if (!response.ok) {
    throw new Error(`历史接口 HTTP ${response.status}`);
  }
  const payload = await response.json();
  if (payload.instanceId !== activeInstanceId) {
    return;
  }
  historySamples = Array.isArray(payload.points)
    ? payload.points.map((point) => ({
      sequence: point.sequence,
      epochMs: point.endEpochMs,
      cpu: ((point.metrics || {})[
        "system.cpu.utilization.percent"
      ] || {}).last,
      mem: ((point.metrics || {})[
        "system.memory.utilization.percent"
      ] || {}).last,
    })).filter((point) => finite(point.epochMs))
    : [];
  historyDownsampled = payload.downsampled === true;
  lastSequence = historySamples.reduce(
    (highest, point) => Math.max(highest, point.sequence || 0),
    0
  );
  historyReady = true;
}

function appendSnapshot(data) {
  const sequence = data.sequence || 0;
  if (!sequence || sequence <= lastSequence || !finite(data.collectedAtEpochMs)) {
    return;
  }
  const overview = data.overview || {};
  const cpu = overview.cpu || {};
  const memory = overview.memory || {};
  historySamples.push({
    sequence,
    epochMs: data.collectedAtEpochMs,
    t: data.collectedAt,
    cpu: finite(cpu.percent) ? cpu.percent : null,
    mem: finite(memory.percent) ? memory.percent : null,
    status: (data.health || {}).status || "error",
  });
  lastSequence = sequence;

  const cutoffMs = data.collectedAtEpochMs -
    (data.historyWindowSeconds || 3600) * 1000;
  historySamples = historySamples.filter(
    (point) => point.epochMs >= cutoffMs
  );
  if (historySamples.length > CLIENT_HISTORY_POINT_LIMIT) {
    historySamples = historySamples.slice(-CLIENT_HISTORY_POINT_LIMIT);
  }
}

refresh();
