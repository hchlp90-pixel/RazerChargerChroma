// Razer Wireless Charger (코드명 Audrey) BLE 크로마 프로토콜
// Razer Chroma Configurator 2.5.199 (com.razer.chromaconfigurator) 의
// com.razer.chromaconfigurator.devices.audrey.f / a / g 를 디컴파일해 옮긴 것.
(function (root) {
  "use strict";

  const UUID = {
    service: "52401523-f97c-7f90-0e7f-6c6f4e36db1c", // 크로마 서비스
    write: "52401524-f97c-7f90-0e7f-6c6f4e36db1c",   // 명령 쓰기
    notify: "52401525-f97c-7f90-0e7f-6c6f4e36db1c",  // 응답 알림
    deviceInfo: 0x180a,
    manufacturer: 0x2a29,
    pnpId: 0x2a50,
  };

  const DEVICE_NAME = "Razer Wireless Charger";

  // 앱의 프로필 번호: 충전 중 효과 = 2, 대기 효과 = 1 (audrey/g.a, audrey/b.a)
  const PROFILE = { charging: 2, standby: 1 };

  // 장치 효과 번호 (audrey/g.c). 앱이 충전기에 보여 주는 것은 1~4.
  const EFFECTS = [
    { id: 0, key: "OFF", name: "끄기", colors: 0 },
    { id: 1, key: "STATIC", name: "고정", colors: 1 },
    { id: 2, key: "BREATHING", name: "숨쉬기", colors: 2 },
    { id: 3, key: "SPECTRUM", name: "스펙트럼", colors: 0 },
    { id: 4, key: "WAVE", name: "물결", colors: 0, direction: true },
    // 아래는 앱의 번호표에만 있고 충전기 화면에는 안 나오는 것들(실험)
    { id: 5, key: "DECAY", name: "감쇠", colors: 1, extra: true },
    { id: 7, key: "STARLIGHT", name: "별빛", colors: 2, extra: true },
    { id: 8, key: "CUSTOMIZE", name: "사용자 지정", colors: 0, extra: true },
    { id: 130, key: "PRISM", name: "프리즘", colors: 0, extra: true },
    { id: 131, key: "TROUGH", name: "골짜기", colors: 1, direction: true, extra: true },
    { id: 134, key: "PEAK", name: "봉우리", colors: 1, extra: true },
    { id: 137, key: "FACTORY", name: "공장 시연", colors: 0, direction: true, extra: true },
    { id: 143, key: "CONFETTI", name: "색종이", colors: 2, extra: true },
    { id: 144, key: "CYLON", name: "사일런", colors: 1, extra: true },
    { id: 153, key: "MARQUEE", name: "마키", colors: 1, direction: true, extra: true },
    { id: 157, key: "ATOM", name: "원자", colors: 1, direction: true, extra: true },
    { id: 158, key: "SEARCH_LIGHT", name: "탐조등", colors: 1, direction: true, extra: true },
  ];

  // 10초 넘게 조용하면 앱은 명령 앞에 이 '깨우기' 패킷을 먼저 보낸다 (audrey/a.g)
  const WAKEUP = Uint8Array.from([0x44, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00]);
  const WAKEUP_AFTER_MS = 10000;

  class Encoder {
    constructor() { this.seq = 0; }

    // 8바이트 머리: [0]=순번, [1..3]=데이터 길이(리틀 엔디언 24비트), [4]=명령 묶음, [5]=명령, [6], [7]
    header(group, cmd, b6, b7, length) {
      const h = new Uint8Array(8);
      h[0] = this.seq & 0xff;
      this.seq = (this.seq + 1) & 0xff;
      if (this.seq === 0) this.seq = 1;
      h[1] = length & 0xff;
      h[2] = (length >> 8) & 0xff;
      h[3] = (length >> 16) & 0xff;
      h[4] = group; h[5] = cmd; h[6] = b6; h[7] = b7;
      return h;
    }

    packet(group, cmd, b6, b7, payload) {
      const data = payload || new Uint8Array(0);
      const h = this.header(group, cmd, b6, b7, data.length);
      const out = new Uint8Array(8 + data.length);
      out.set(h, 0);
      out.set(data, 8);
      return out;
    }

    // 효과: 명령 0x10/0x03, 데이터 10바이트
    effect({ profile, effect, color1 = 0, color2 = 0, left = false, speed = 0, random = false }) {
      const on = effect !== 0;
      const c1 = on ? color1 : 0;
      const c2 = on ? color2 : 0;
      const p = new Uint8Array(10);
      p[0] = effect & 0xff;
      p[1] = left ? 2 : 1;
      p[2] = (speed + 1) & 0xff;
      p[3] = random ? 0 : (c2 !== 0 ? 2 : 1);
      p[4] = (c1 >> 16) & 0xff; p[5] = (c1 >> 8) & 0xff; p[6] = c1 & 0xff;
      p[7] = (c2 >> 16) & 0xff; p[8] = (c2 >> 8) & 0xff; p[9] = c2 & 0xff;
      return this.packet(0x10, 0x03, profile, 0x0a, p);
    }

    // 밝기: 명령 0x10/0x05, 앱 값 0~255 → 장치 값 56~255
    brightness({ profile, value }) {
      const v = Math.max(0, Math.min(255, value | 0));
      const dev = Math.trunc((v / 255) * 199) + 56;
      return this.packet(0x10, 0x05, profile, 0x0a, Uint8Array.from([dev]));
    }

    // 상태 표시등(충전 표시): 명령 0x11/0x04, 켜기 2 / 끄기 0
    statusLight(on) {
      return this.packet(0x11, 0x04, 0, 0, Uint8Array.from([on ? 2 : 0]));
    }

    // 조명 꺼짐 타이머(초, 0 = 끄지 않음): 명령 0x01/0x0b
    timers({ standby = 0, charging = 0 }) {
      const s = Math.max(0, Math.min(0xffff, standby | 0));
      const c = Math.max(0, Math.min(0xffff, charging | 0));
      const p = Uint8Array.from([s & 0xff, s >> 8, c & 0xff, c >> 8, s & 0xff, s >> 8]);
      return this.packet(0x01, 0x0b, 0, 0, p);
    }

    // 설정 초기화: 명령 0x01/0x01, [7]=1, 데이터 없음
    reset() {
      return this.packet(0x01, 0x01, 0, 0x01, null);
    }
  }

  function hex(bytes) {
    return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join(" ");
  }

  const api = { UUID, DEVICE_NAME, PROFILE, EFFECTS, WAKEUP, WAKEUP_AFTER_MS, Encoder, hex };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.RazerCharger = api;
})(typeof window !== "undefined" ? window : globalThis);
