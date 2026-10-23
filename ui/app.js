// 화면 동작. 원래 앱의 기기 화면 → 색상 편집(ChromaView) → 고급 화면 흐름을 그대로 따른다.
(() => {
  "use strict";
  const $ = (id) => document.getElementById(id);
  const hostApi = window.chrome && window.chrome.webview;
  const send = (o) => { if (hostApi) hostApi.postMessage(o); };

  // 충전기가 지원하는 효과 (앱의 Audrey 목록: 정적·브리딩·스펙트럼·웨이브)
  const EFFECTS = [
    { id: 1, label: "정적", name: "고정", icon: "ck_ic_static", colors: 1 },
    { id: 2, label: "브리딩", name: "브리딩", icon: "ck_ic_breathing", colors: 2, duration: true },
    { id: 3, label: "스펙트럼", name: "스펙트럼", icon: "ck_ic_spectrum", colors: 0, duration: true },
    { id: 4, label: "웨이브", name: "웨이브", icon: "ck_ic_wave", colors: 0, duration: true, direction: true },
  ];
  const effectOf = (id) => EFFECTS.find((e) => e.id === id);
  const PROFILE_NAME = { 2: "충전 효과", 1: "대기 효과" };
  const TIMES = [60, 300, 600, 900, 1800, 3600];

  // 앱 기본값: 충전 = 웨이브, 대기 = 스펙트럼, 밝기 187, 초록, 타이머 15분, 상태 표시기 켬
  const state = {
    profiles: {
      2: { effect: 4, last: 4, color1: 0x00ff00, color2: 0, color2Saved: 0x0050ff, left: false, speed: 0, brightness: 187 },
      1: { effect: 3, last: 3, color1: 0x00ff00, color2: 0, color2Saved: 0x0050ff, left: false, speed: 0, brightness: 187 },
    },
    status: true,
    timer: { chargingOn: true, charging: 900, standbyOn: true, standby: 900 },
  };
  const conn = { usb: { on: false, firmware: "", serial: "" }, ble: { state: hostApi ? "scanning" : "off", address: "" } };

  // ---------- 색 ----------
  const hex6 = (c) => "#" + (c & 0xffffff).toString(16).padStart(6, "0");
  function rgb2hsv(c) {
    const r = ((c >> 16) & 255) / 255, g = ((c >> 8) & 255) / 255, b = (c & 255) / 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    let h = 0;
    if (d) { h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4; h *= 60; if (h < 0) h += 360; }
    return { h, s: max ? d / max : 0, v: max };
  }
  function hsv2rgb(h, s, v) {
    const f = (n) => { const k = (n + h / 60) % 6; return v - v * s * Math.max(0, Math.min(k, 4 - k, 1)); };
    return (Math.round(f(5) * 255) << 16) | (Math.round(f(3) * 255) << 8) | Math.round(f(1) * 255);
  }

  // ---------- 저장 ----------
  let saveTimer = 0;
  function save() { clearTimeout(saveTimer); saveTimer = setTimeout(() => send({ cmd: "save", state }), 400); }

  // ---------- 기기로 보내기 ----------
  const applyTimers = {};
  function apply(profile, delay = 110) {
    clearTimeout(applyTimers[profile]);
    applyTimers[profile] = setTimeout(() => {
      const p = state.profiles[profile];
      const e = effectOf(p.effect);
      send({
        cmd: "effect", profile, effect: p.effect,
        color1: e && e.colors ? p.color1 : 0,
        color2: e && e.colors === 2 ? p.color2 : 0,
        left: !!p.left, speed: p.speed | 0, brightness: p.brightness | 0, withBrightness: true,
      });
    }, delay);
    save();
  }
  const brightTimers = {};
  function applyBrightness(profile) {
    clearTimeout(brightTimers[profile]);
    brightTimers[profile] = setTimeout(() => send({ cmd: "brightness", profile, value: state.profiles[profile].brightness }), 90);
    save();
  }
  function applyTimersCmd() {
    const t = state.timer;
    send({ cmd: "timers", standby: t.standbyOn ? t.standby : 0, charging: t.chargingOn ? t.charging : 0 });
    save();
  }

  // ---------- 화면 전환 ----------
  function show(id) {
    for (const p of document.querySelectorAll(".page")) p.classList.add("hidden");
    const el = $(id);
    el.classList.remove("hidden");
    el.classList.remove("enter"); void el.offsetWidth; el.classList.add("enter");
    if (id === "pageEditor") requestAnimationFrame(drawWheel);
  }

  // ---------- 기기 화면 ----------
  function effectName(p) {
    if (p.effect === 0) return "꺼짐";
    const e = effectOf(p.effect);
    return e ? e.name : "효과 " + p.effect;
  }
  function renderMain() {
    const c = state.profiles[2], s = state.profiles[1];
    $("nameCharging").textContent = effectName(c);
    $("nameStandby").textContent = effectName(s);
    $("swCharging").checked = c.effect !== 0;
    $("swStandby").checked = s.effect !== 0;
    const bg = $("bgColor");
    bg.className = "";
    bg.style.background = "";
    if (c.effect === 0) bg.classList.add("off");
    else if (c.effect === 3) bg.classList.add("spectrum");
    else if (c.effect === 4) { bg.classList.add("wave"); if (c.left) bg.classList.add("left"); }
    else { bg.style.background = hex6(c.color1); if (c.effect === 2) bg.classList.add("breathing"); }
  }
  function toggleProfile(profile, on) {
    const p = state.profiles[profile];
    if (on) p.effect = p.last || 1; else { if (p.effect) p.last = p.effect; p.effect = 0; }
    renderMain();
    apply(profile, 0);
  }

  // ---------- 연결 표시 ----------
  const BLE_TEXT = { off: "연결 안 됨", scanning: "찾는 중…", connecting: "연결 중…", connected: "블루투스", lost: "다시 연결하는 중…", notfound: "찾지 못함", error: "연결 실패", needpair: "페어링 필요" };
  function renderConn() {
    const usbOn = conn.usb.on, asleep = usbOn && conn.usb.asleep, bleOn = conn.ble.state === "connected";
    const busy = conn.ble.state === "scanning" || conn.ble.state === "connecting" || conn.ble.state === "lost";
    const chip = $("connChip");
    chip.classList.toggle("on", (usbOn && !asleep) || bleOn);
    chip.classList.toggle("busy", (asleep && !bleOn) || (!usbOn && !bleOn && busy));
    $("connText").textContent = usbOn
      ? (bleOn ? "USB · 블루투스" : asleep ? "USB · 절전 중" : "USB")
      : (BLE_TEXT[conn.ble.state] || "연결 안 됨");

    const lost = !usbOn && !bleOn;
    $("lost").classList.toggle("hidden", !lost && !(asleep && !bleOn));
    if (asleep && !bleOn) {
      $("lostTitle").textContent = "충전기가 절전 중입니다";
      $("lostBody").textContent = "조명이 꺼진 동안에는 충전기가 명령을 받지 않습니다. 충전기 버튼을 누르거나 폰을 올리면 깨어나고, 그사이 바꾼 설정은 깨어나는 즉시 적용됩니다.";
      $("btnRetry").classList.add("hidden");
    } else if (lost) {
      $("lostTitle").textContent = busy ? (conn.ble.state === "lost" ? "다시 연결하는 중..." : "기기 찾는 중...")
        : conn.ble.state === "needpair" ? "블루투스 페어링이 필요합니다" : "기기가 연결되지 않았습니다";
      $("lostBody").textContent = busy
        ? "충전기를 USB 케이블로 PC에 연결하면 바로 잡힙니다. 블루투스로도 찾고 있습니다."
        : conn.ble.state === "needpair"
          ? "USB 케이블로 PC에 연결하면 바로 쓸 수 있습니다. 블루투스로 쓰려면 충전기 버튼을 5~6초 길게 눌러 페어링 모드로 만든 뒤 다시 시도하세요. (폰과의 블루투스 연결은 끊어집니다)"
          : "충전기를 USB 케이블로 PC에 연결하거나, 블루투스로 다시 찾아보세요.";
      $("btnRetry").classList.toggle("hidden", busy);
    }

    $("usbInfo").textContent = usbOn
      ? [asleep ? "연결됨 · 절전 중(버튼을 누르면 깨어남)" : "연결됨", conn.usb.firmware && "펌웨어 " + conn.usb.firmware, conn.usb.serial && "시리얼 " + conn.usb.serial].filter(Boolean).join(" · ")
      : "연결 안 됨 · USB 케이블로 PC에 꽂으면 자동으로 잡힙니다";
    const bleState = bleOn ? "연결됨" : BLE_TEXT[conn.ble.state] || "연결 안 됨";
    $("bleInfo").textContent = (conn.ble.state === "needpair" || conn.ble.state === "error" ? conn.ble.text : bleState)
      + (conn.ble.address ? " · " + conn.ble.address.replace(/(..)(?!$)/g, "$1:") : "");
    const b = $("btnBle");
    b.textContent = bleOn ? "끊기" : busy ? "연결 중…" : "연결";
    b.disabled = busy;
    $("bleCover").classList.toggle("hidden", bleOn);
  }

  // ---------- 고급 화면 ----------
  const timeText = (sec) => sec >= 3600 ? { num: sec / 3600, unit: "시간" } : { num: sec / 60, unit: "분" };
  function renderAdvanced() {
    $("swStatus").checked = !!state.status;
    for (const which of ["charging", "standby"]) {
      const on = state.timer[which + "On"], t = timeText(state.timer[which]);
      const box = $(which === "charging" ? "timerCharging" : "timerStandby");
      box.classList.toggle("off", !on);
      box.querySelector(".num").textContent = t.num;
      box.querySelector(".unit").textContent = t.unit;
      $(which === "charging" ? "swTimerCharging" : "swTimerStandby").checked = on;
    }
  }
  function openTimeSheet(which) {
    $("sheetTitle").textContent = which === "charging" ? "충전 효과 타이머" : "대기 효과 타이머";
    const list = $("sheetList");
    list.textContent = "";
    for (const sec of TIMES) {
      const b = document.createElement("button");
      const t = timeText(sec);
      b.className = "sheetItem" + (state.timer[which] === sec ? " sel" : "");
      b.textContent = t.num + t.unit;
      b.addEventListener("click", () => {
        state.timer[which] = sec; state.timer[which + "On"] = true;
        closeSheet(); renderAdvanced(); applyTimersCmd();
      });
      list.appendChild(b);
    }
    $("sheetBack").classList.remove("hidden");
  }
  function closeSheet() { $("sheetBack").classList.add("hidden"); }

  // ---------- 색상 편집 화면 ----------
  let edProfile = 2, edSwatch = 0, edTool = "color";
  const wheel = $("wheel");
  let wheelImg = null, wheelSize = 0;

  function edP() { return state.profiles[edProfile]; }
  function edEffect() { return effectOf(edP().effect) || EFFECTS[0]; }
  function edColor() { const p = edP(); return edSwatch === 1 ? (p.color2 || p.color2Saved) : p.color1; }
  function setEdColor(c) {
    const p = edP();
    if (edSwatch === 1) { p.color2 = c; p.color2Saved = c; } else p.color1 = c;
  }

  function openEditor(profile) {
    edProfile = profile;
    const p = edP();
    if (p.effect === 0) p.effect = p.last || 1;
    edSwatch = 0;
    edTool = edEffect().colors ? "color" : "bright";
    $("edTitle").textContent = PROFILE_NAME[profile];
    $("hexInput").classList.add("hidden");
    closeModeMenu();
    renderEditor();
    show("pageEditor");
    renderMain();
    apply(profile, 0);
  }

  function renderEditor() {
    const p = edP(), e = edEffect();
    $("modeIcon").src = "img/" + e.icon + "_selected.png";
    $("blocked").classList.toggle("hidden", e.colors > 0);
    const sw = document.querySelectorAll(".swatch");
    sw[0].classList.toggle("hidden", e.colors === 0);
    sw[1].classList.toggle("hidden", e.colors < 2);
    if (e.colors < 2 && edSwatch === 1) edSwatch = 0;
    sw[0].style.background = hex6(p.color1);
    sw[1].style.background = hex6(p.color2 || p.color2Saved);
    sw[1].classList.toggle("muted", !p.color2);
    sw.forEach((s, i) => s.classList.toggle("sel", edTool === "color" && i === edSwatch));
    if (!e.colors) edTool = "bright";
    $("btnBright").classList.toggle("sel", edTool === "bright");

    $("pIntensity").classList.toggle("hidden", !(edTool === "color" && e.colors));
    $("pBright").classList.toggle("hidden", edTool !== "bright");
    $("pDirection").classList.toggle("hidden", !e.direction);
    $("pDuration").classList.toggle("hidden", !e.duration);
    $("btnMute").classList.toggle("hidden", edSwatch !== 1);
    $("btnMute").classList.toggle("on", edSwatch === 1 && !p.color2);
    $("intensityTitle").textContent = e.colors === 2 ? (edSwatch === 0 ? "색상 하나의 강도" : "색상 2 강도") : "색상 강도";

    const hsv = rgb2hsv(edColor());
    const inten = $("intensity");
    inten.value = Math.round(hsv.v * 100);
    inten.style.setProperty("--track", `linear-gradient(90deg, #000, ${hex6(hsv2rgb(hsv.h, hsv.s, 1))})`);
    const br = $("brightness");
    br.value = p.brightness;
    const pct = (p.brightness / 255) * 100;
    br.style.setProperty("--track", `linear-gradient(90deg, #fff ${pct}%, rgba(255,255,255,.2) ${pct}%)`);
    for (const b of document.querySelectorAll("#pDirection .rb")) b.classList.toggle("sel", (b.dataset.dir === "left") === !!p.left);
    for (const b of document.querySelectorAll("#pDuration .rb")) b.classList.toggle("sel", Number(b.dataset.speed) === (p.speed | 0));
    if (!$("hexInput").classList.contains("hidden") && document.activeElement !== $("hexInput")) $("hexInput").value = hex6(edColor()).slice(1).toUpperCase();
    drawWheel();
  }

  // 색상환: 각도 = 색상, 중심에서 거리 = 채도 (가운데 흰색)
  function buildWheel(size) {
    const dpr = window.devicePixelRatio || 1;
    const n = Math.round(size * dpr);
    const c = document.createElement("canvas");
    c.width = c.height = n;
    const ctx = c.getContext("2d");
    const img = ctx.createImageData(n, n);
    const r = n / 2;
    for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
      const dx = x - r + .5, dy = y - r + .5, d = Math.hypot(dx, dy);
      const i = (y * n + x) * 4;
      if (d > r) { img.data[i + 3] = 0; continue; }
      let h = Math.atan2(-dy, dx) * 180 / Math.PI; if (h < 0) h += 360;
      const col = hsv2rgb(h, Math.min(1, d / (r - 1)), 1);
      img.data[i] = col >> 16 & 255; img.data[i + 1] = col >> 8 & 255; img.data[i + 2] = col & 255;
      img.data[i + 3] = d > r - 1 ? Math.round((r - d) * 255) : 255;
    }
    ctx.putImageData(img, 0, 0);
    return c;
  }
  function drawWheel() {
    if ($("pageEditor").classList.contains("hidden")) return;
    const area = wheel.parentElement.getBoundingClientRect();
    const size = Math.max(160, Math.min(area.width - 48, area.height - 24, 360));
    const dpr = window.devicePixelRatio || 1;
    if (!wheelImg || wheelSize !== size) { wheelImg = buildWheel(size); wheelSize = size; }
    wheel.width = wheel.height = Math.round(size * dpr);
    wheel.style.width = wheel.style.height = size + "px";
    const ctx = wheel.getContext("2d");
    ctx.clearRect(0, 0, wheel.width, wheel.height);
    ctx.drawImage(wheelImg, 0, 0);
    if (!edEffect().colors) return;
    const hsv = rgb2hsv(edColor()), r = wheel.width / 2;
    const a = hsv.h * Math.PI / 180, x = r + Math.cos(a) * hsv.s * (r - 1), y = r - Math.sin(a) * hsv.s * (r - 1);
    ctx.lineWidth = 2.5 * dpr;
    ctx.strokeStyle = "rgba(0,0,0,.55)";
    ctx.beginPath(); ctx.arc(x, y, 11 * dpr, 0, Math.PI * 2); ctx.stroke();
    ctx.strokeStyle = "#fff"; ctx.lineWidth = 2 * dpr;
    ctx.beginPath(); ctx.arc(x, y, 10 * dpr, 0, Math.PI * 2); ctx.stroke();
  }
  function pickFromWheel(ev) {
    const rect = wheel.getBoundingClientRect();
    const r = rect.width / 2, dx = ev.clientX - rect.left - r, dy = ev.clientY - rect.top - r;
    let h = Math.atan2(-dy, dx) * 180 / Math.PI; if (h < 0) h += 360;
    const s = Math.min(1, Math.hypot(dx, dy) / r);
    const v = rgb2hsv(edColor()).v || 1;
    const p = edP();
    if (edSwatch === 1 && !p.color2) p.color2 = p.color2Saved;
    setEdColor(hsv2rgb(h, s, v));
    renderEditor();
    if (edProfile === 2) renderMain();
    apply(edProfile);
  }
  let dragging = false;
  wheel.addEventListener("pointerdown", (ev) => { if (!edEffect().colors) return; dragging = true; wheel.setPointerCapture(ev.pointerId); pickFromWheel(ev); });
  wheel.addEventListener("pointermove", (ev) => { if (dragging) pickFromWheel(ev); });
  wheel.addEventListener("pointerup", () => { dragging = false; });
  window.addEventListener("resize", () => { wheelImg = null; drawWheel(); });

  // 효과 메뉴 (원래 앱처럼 오른쪽 아래에서 위로 펼쳐짐)
  function openModeMenu() {
    const menu = $("modeMenu");
    menu.textContent = "";
    for (const e of EFFECTS.slice().reverse()) {
      const item = document.createElement("button");
      item.className = "modeItem" + (e.id === edP().effect ? " sel" : "");
      item.innerHTML = `<span class="ml"></span><span class="mi"><img alt=""></span>`;
      item.querySelector(".ml").textContent = e.label;
      item.querySelector("img").src = "img/" + e.icon + (e.id === edP().effect ? "_selected" : "") + ".png";
      item.addEventListener("click", () => {
        const p = edP();
        p.effect = e.id; p.last = e.id;
        edSwatch = 0;
        edTool = e.colors ? "color" : "bright";
        closeModeMenu();
        renderEditor(); renderMain(); apply(edProfile, 0);
      });
      menu.appendChild(item);
    }
    menu.classList.remove("hidden");
    let shade = document.querySelector(".modeShade");
    if (!shade) { shade = document.createElement("div"); shade.className = "modeShade"; shade.addEventListener("click", closeModeMenu); $("pageEditor").appendChild(shade); }
  }
  function closeModeMenu() {
    $("modeMenu").classList.add("hidden");
    const shade = document.querySelector(".modeShade");
    if (shade) shade.remove();
  }

  // ---------- 알림·기록·대화상자 ----------
  let toastTimer = 0;
  function toast(text) {
    const t = $("toast");
    t.textContent = text; t.classList.remove("hidden");
    clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add("hidden"), 2600);
  }
  function log(text) {
    const el = $("log");
    const time = new Date().toLocaleTimeString("ko-KR", { hour12: false });
    el.textContent += `[${time}] ${text}\n`;
    const lines = el.textContent.split("\n");
    if (lines.length > 400) el.textContent = lines.slice(-400).join("\n");
    el.scrollTop = el.scrollHeight;
  }
  function infoDialog() {
    const rows = [
      ["모델", "Razer Wireless Charger"],
      ["연결", conn.usb.on ? (conn.ble.state === "connected" ? "USB · 블루투스" : "USB") : conn.ble.state === "connected" ? "블루투스" : "연결 안 됨"],
      ["펌웨어", conn.usb.firmware || "-"],
      ["시리얼", conn.usb.serial || "-"],
      ["블루투스 주소", conn.ble.address ? conn.ble.address.replace(/(..)(?!$)/g, "$1:") : "-"],
    ];
    const back = document.createElement("div");
    back.className = "dialogBack";
    back.innerHTML = `<div class="dialog"><div class="dTitle">정보</div><div class="dBody"></div><div class="dBtns"><button class="green">확인</button></div></div>`;
    back.querySelector(".dBody").innerHTML = rows.map((r) => `<div style="display:flex;justify-content:space-between;gap:12px;padding:3px 0"><span style="color:#999">${r[0]}</span><span>${r[1]}</span></div>`).join("");
    const close = () => back.remove();
    back.addEventListener("click", (e) => { if (e.target === back) close(); });
    back.querySelector("button").addEventListener("click", close);
    document.body.appendChild(back);
  }

  // ---------- 이벤트 연결 ----------
  $("rowCharging").addEventListener("click", (e) => { if (!e.target.closest(".switch")) openEditor(2); });
  $("rowStandby").addEventListener("click", (e) => { if (!e.target.closest(".switch")) openEditor(1); });
  $("swCharging").addEventListener("change", (e) => toggleProfile(2, e.target.checked));
  $("swStandby").addEventListener("change", (e) => toggleProfile(1, e.target.checked));
  $("btnAdvanced").addEventListener("click", () => { renderAdvanced(); show("pageAdvanced"); });
  $("btnAdvBack").addEventListener("click", () => show("pageMain"));
  $("btnInfo").addEventListener("click", infoDialog);
  $("btnRetry").addEventListener("click", () => send({ cmd: "connectBle" }));
  $("btnBack").addEventListener("click", () => { closeModeMenu(); show(edProfile === 1 ? "pageAdvanced" : "pageMain"); });

  $("btnHex").addEventListener("click", () => {
    const inp = $("hexInput");
    if (inp.classList.contains("hidden")) {
      if (!edEffect().colors) return;
      inp.classList.remove("hidden"); inp.value = hex6(edColor()).slice(1).toUpperCase(); inp.focus(); inp.select();
    } else commitHex();
  });
  function commitHex() {
    const inp = $("hexInput"), v = inp.value.trim().replace(/^#/, "");
    if (/^[0-9a-fA-F]{6}$/.test(v)) {
      const p = edP();
      if (edSwatch === 1 && !p.color2) p.color2 = p.color2Saved;
      setEdColor(parseInt(v, 16));
      renderEditor(); if (edProfile === 2) renderMain(); apply(edProfile, 0);
    }
    inp.classList.add("hidden");
  }
  $("hexInput").addEventListener("keydown", (e) => { if (e.key === "Enter") commitHex(); if (e.key === "Escape") $("hexInput").classList.add("hidden"); });

  $("btnBright").addEventListener("click", () => { edTool = "bright"; renderEditor(); });
  for (const s of document.querySelectorAll(".swatch")) s.addEventListener("click", () => { edSwatch = Number(s.dataset.i); edTool = "color"; renderEditor(); });
  $("btnMode").addEventListener("click", () => { if ($("modeMenu").classList.contains("hidden")) openModeMenu(); else closeModeMenu(); });
  $("btnMute").addEventListener("click", () => {
    const p = edP();
    if (p.color2) { p.color2Saved = p.color2; p.color2 = 0; } else p.color2 = p.color2Saved || 0x0050ff;
    renderEditor(); apply(edProfile, 0);
  });
  $("intensity").addEventListener("input", (e) => {
    const p = edP();
    if (edSwatch === 1 && !p.color2) p.color2 = p.color2Saved;
    const hsv = rgb2hsv(edColor());
    const hueSrc = hsv.v === 0 ? rgb2hsv(edSwatch === 1 ? p.color2Saved : p.color1) : hsv;
    setEdColor(hsv2rgb(hueSrc.h, hueSrc.s, Number(e.target.value) / 100));
    renderEditor(); if (edProfile === 2) renderMain(); apply(edProfile);
  });
  $("brightness").addEventListener("input", (e) => {
    edP().brightness = Number(e.target.value);
    renderEditor(); applyBrightness(edProfile);
  });
  for (const b of document.querySelectorAll("#pDirection .rb")) b.addEventListener("click", () => {
    edP().left = b.dataset.dir === "left"; renderEditor(); renderMain(); apply(edProfile, 0);
  });
  for (const b of document.querySelectorAll("#pDuration .rb")) b.addEventListener("click", () => {
    edP().speed = Number(b.dataset.speed); renderEditor(); apply(edProfile, 0);
  });

  $("swStatus").addEventListener("change", (e) => { state.status = e.target.checked; send({ cmd: "status", on: state.status }); save(); });
  $("swTimerCharging").addEventListener("change", (e) => { state.timer.chargingOn = e.target.checked; renderAdvanced(); applyTimersCmd(); });
  $("swTimerStandby").addEventListener("change", (e) => { state.timer.standbyOn = e.target.checked; renderAdvanced(); applyTimersCmd(); });
  for (const b of document.querySelectorAll(".timeVal")) b.addEventListener("click", () => openTimeSheet(b.dataset.which));
  $("sheetBack").addEventListener("click", (e) => { if (e.target === $("sheetBack")) closeSheet(); });
  $("btnBle").addEventListener("click", () => send({ cmd: conn.ble.state === "connected" ? "disconnectBle" : "connectBle" }));
  $("btnBleCover").addEventListener("click", () => send({ cmd: "connectBle" }));
  $("btnReset").addEventListener("click", () => $("confirmBack").classList.remove("hidden"));
  $("confirmNo").addEventListener("click", () => $("confirmBack").classList.add("hidden"));
  $("confirmYes").addEventListener("click", () => { $("confirmBack").classList.add("hidden"); send({ cmd: "reset" }); });
  $("btnLog").addEventListener("click", () => { $("log").classList.toggle("hidden"); $("log").scrollTop = 1e9; });
  document.addEventListener("keydown", (e) => {
    if (e.key !== "Escape") return;
    if (!$("sheetBack").classList.contains("hidden")) closeSheet();
    else if (!$("modeMenu").classList.contains("hidden")) closeModeMenu();
    else if (!$("pageEditor").classList.contains("hidden")) $("btnBack").click();
    else if (!$("pageAdvanced").classList.contains("hidden")) show("pageMain");
  });

  // ---------- 프로그램에서 오는 소식 ----------
  function merge(dst, src) {
    for (const k of Object.keys(src || {})) {
      if (src[k] && typeof src[k] === "object" && !Array.isArray(src[k]) && dst[k] && typeof dst[k] === "object") merge(dst[k], src[k]);
      else if (k in dst || dst === state.profiles) dst[k] = src[k];
    }
  }
  function onDevice(profiles) {
    for (const key of Object.keys(profiles)) {
      const d = profiles[key], p = state.profiles[key];
      if (!p) continue;
      p.effect = d.effect;
      if (d.effect) p.last = d.effect;
      if (d.effect && effectOf(d.effect) && effectOf(d.effect).colors) {
        p.color1 = d.color1;
        if (d.colors === 2 && d.color2) { p.color2 = d.color2; p.color2Saved = d.color2; } else p.color2 = 0;
      }
      if (d.left === 2 || d.left === 1) p.left = d.left === 2;
      if (d.speed >= 1 && d.speed <= 3) p.speed = d.speed - 1;
      if (d.brightness >= 0) p.brightness = d.brightness;
    }
    renderMain();
    if (!$("pageEditor").classList.contains("hidden")) renderEditor();
    save();
  }
  if (hostApi) hostApi.addEventListener("message", (ev) => {
    const m = ev.data;
    if (!m || !m.type) return;
    if (m.type === "init") { if (m.settings) merge(state, m.settings); renderMain(); renderAdvanced(); }
    else if (m.type === "conn") { conn.usb = m.usb; conn.ble = m.ble; renderConn(); }
    else if (m.type === "device") onDevice(m.profiles);
    else if (m.type === "toast") toast(m.text);
    else if (m.type === "log") log(m.text);
  });

  renderMain(); renderAdvanced(); renderConn();
  send({ cmd: "ready" });
})();
