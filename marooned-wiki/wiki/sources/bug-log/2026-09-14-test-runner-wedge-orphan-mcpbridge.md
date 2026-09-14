---
title: bug-2026-09-14-test-runner-wedge-orphan-mcpbridge
type: bug-log
sources:
  - Marooned/Assets/Tests/Runtime/BridgeRoundTripPlayModeTests.cs
  - Library/PackageCache/com.ivanmurzak.unity.mcp/Editor/Scripts/API/Tool/Tests.cs
  - McpBridge/Program.cs
related:
  - "[[mcp-bridge]]"
  - "[[mcp-tool-table]]"
  - "[[clue-system-v2]]"
  - "[[2026-09-14-clue-system-v2-instance-pipeline]]"
folder: bug-log
created: 2026-09-14
tags:
  - bug-log
  - marooned
  - mcp
  - testing
  - playmode
  - infra
---

# 🐛 Bug: Test-Runner Wedge + Orphan McpBridge — PlayMode tests แขวน/พังเป็นชุด (2026-09-14)

> **สรุปสั้น:** รัน PlayMode tests ผ่าน MCP (`tests-run`) แล้วเจอ 3 อาการร้อยเรียงกัน:
> test แขวนเงียบ ๆ จนครบ timeout 360s, test ที่เคยผ่านกลับ fail เป็นชุด, และ
> `McpBridge` process ค้างเป็น orphan กิน TCP slot — **โค้ดเกมไม่ผิดเลย**
> ทั้งหมดเป็น infra ของการรันเทสผ่าน proxy + พฤติกรรม abort ของ test runner

**วันที่:** 2026-09-14 (พบระหว่างทำ Clue System v2 part (d)) · **ผลสุดท้าย:** ทุก test
ผ่านเมื่อรันสภาพแวดล้อมสะอาด + ใช้ filter ถูกตัว

## อาการ (Symptoms)

1. **แขวนเงียบ 360s** — `Bridge_MoveToLocation_WalkDuration_RoundTrip` (มี
   `[Timeout(360000)]` ของตัวเอง) รันแล้วไม่มี log ใหม่ ไม่มี exception จบด้วย
   `Timeout value of 360000 ms was exceeded` ทั้งที่ conversation แบบเดียวกันผ่าน
   ใน ~35 วิ
2. **fail เป็นชุดแบบข้ามคลาส** — run เดียว fail 5 test จาก 4 คลาส
   (BridgeRoundTrip / ClueGeneration / NpcSurvival / NpcZoneTransition) ที่ทุกตัว
   **เพิ่งผ่าน standalone**; message เช่น `force kill ต้องสำเร็จ (attempt สุดท้าย:
   not_attempted)` — สถานะ scene ไม่ตรงกับที่ test เตรียมไว้
3. **console มี error แปลกปลอมระหว่าง run** — `Cannot run tests: another test run is
   already in progress. Active request id: …` โผล่กลาง run ที่กำลังบิน → run นั้น
   fail ทันทีทั้งที่ assertion ยังไม่มีปัญหา
4. **`tasklist` เจอ `dotnet.exe` ค้างหลายตัว** — command line คือ
   `dotnet McpBridge.dll` (spawn โดย test) แต่ run จบ/ถูก abort ไปแล้ว

## รากเหตุ (4 ชั้นซ้อนกัน)

```
HTTP request ผ่าน proxy (timeout ≈5 นาที)
   └─► tests-run เริ่มรันใน Unity (รันต่อแม้ caller หลุด)
         └─► run เก่ายังไม่จบ + request ใหม่ทับ → ปฏิเสธ + เขียน error log
               └─► Unity LogAssert จับ error log กลาง run → run ที่กำลังบิน fail ทันที
                     └─► [Timeout] hard-abort กลาง conversation
                           └─► finally { bridge.Kill() } ไม่ได้รัน
                                 └─► McpBridge เป็น orphan ถือ TCP slot ของ Unity
                                       └─► run ถัดไป: bridge ตัวเอง connect ไม่ได้
                                             └─► retry-forever ก่อนอ่าน stdio
                                                   → writer.WriteLine แขวนเงียบ 360s
```

1. **Proxy timeout สั้นกว่า run** — HTTP gateway ตัด connection ที่ ~5 นาที แต่
   full PlayMode suite (มี bridge round-trip + walking tests) นานกว่านั้น;
   **CLI timeout ไม่ใช่ run abort** — run ยังค้างรันใน Unity ต่อไป
2. **Request ทับถูกปฏิเสธแบบเขียน log error** — plugin ปฏิเสธ request ใหม่ด้วย error
   log (`another test run is already in progress`) และ Unity Test Framework
   **fail test ที่กำลังรันทันทีเมื่อเจอ error log ที่ไม่คาดคิด** (LogAssert) —
   run เก่าจึงโดนพิษจาก request ใหม่ที่แค่ "แอบรออยู่"
3. **`[Timeout]` hard-abort ข้าม `finally`** — `BridgeRoundTripPlayModeTests`
   kill bridge ใน `finally { bridge.Kill() }` แต่ NUnit timeout abort ทำให้
   thread นั้นหยุดก่อนเข้า finally → bridge process รอดตายเป็น orphan
4. **Plugin lock ผูกกับ SessionState (อยู่ข้าม domain reload)** — `SessionState`
   ของ request id ที่ active ไม่หายตอน recompile (forced recompile จึงไม่เคยแก้ wedge);
   ต้องรอ lease หมดอายุ (นาทีขั้นต่ำ ~10 นาที) หรือรอ run จบจริง

> หมายเหตุ: การใช้ `testNames` (ผิด) แทน `testClass` (ถูก) ทำให้ "filtered" request
> กลายเป็น full-suite run ทุกครั้ง — เป็นตัวจุดประกายให้ run ทับซ้อนบ่อยที่สุด

## วิธีแก้ / Protocol ที่ใช้ได้จริง

**เช็คก่อนรันทุกครั้ง:**

```bash
# 1. ไม่มี orphan bridge (dotnet.exe ที่เป็น McpBridge = ต้อง kill)
tasklist | grep -i dotnet || echo clean
# ดู command line ก่อน kill — dotnet ตัวอื่น (เช่น config server) ห้ามแตะ
powershell -NoProfile -Command "(Get-CimInstance Win32_Process -Filter \"name='dotnet.exe'\").CommandLine"
taskkill //PID <orphan_pid> //F

# 2. editor idle + ไม่มี run ค้าง
npx -y unity-mcp-cli run-tool editor-application-get-state --input '{}'
#   → IsPlaying:false, IsCompiling:false

# 3. filter ที่ถูกคือ "testClass" (ไม่ใช่ testNames — ถูกเมียงเงียบ ๆ = full suite!)
npx -y unity-mcp-cli run-tool tests-run \
  --input '{"testMode":"PlayMode","testClass":"ClueGenerationPlayModeTests"}' \
  --timeout 120000   # หน่วย = ms

# 4. ถ้า CLI timeout: อย่ายิง request ใหม่ — poll แทน
#    (TestResults.xml ที่ %USERPROFILE%/AppData/LocalLow/DefaultCompany/Marooned/
#     คือหลักฐาน final + editor-application-get-state รอ IsPlaying:false)
```

**ถ้า wedge แล้ว:** kill orphan → รอ SessionState lease หมดอายุ (≥10 นาที) →
ยืนยัน editor idle → รันใหม่ทีเดียว (solitary) เท่านั้น

**ผลลัพธ์:** bridge round-trip ผ่านใน 35.4s (เทียบกับ 360s-timeout ก่อนหน้า),
ClueGeneration A+F ผ่านใน ~1 วิ, evidence ใหม่ทั้งหมดที่
`TestEvidence/clue-system-v2-b/` + `TestEvidence/movetolocation-fix/`

## บทเรียน

- **CLI/HTTP timeout ≠ run abort** — ทุก request ที่ timeout ทิ้ง run มีชีวิตอยู่
  ใน Unity; วิธีเดียวที่ปลอดภัยคือ poll (`editor-application-get-state` +
  `TestResults.xml`) ไม่ใช่ยิง request ซ้ำ
- **Error log = พิษของ Unity Test Framework** — log ใด ๆ ที่โผล่กลาง run (แม้จาก
  request ถูกปฏิเสธ) ทำให้ test fail ทันที; อย่าส่ง request อื่นระหว่างมี run active
- **NUnit `[Timeout]` abort ไม่รับประกัน `finally`** — ทุก test ที่ spawn external
  process ต้องมีวิธี cleanup นอก finally (ดู "ของที่ยังค้าง")
- **`SessionState` ข้าม domain reload** — forced recompile ไม่เคยเคลียร์ lock ของ
  plugin; อย่าเสียเวลา recompile กับ wedge ชนิดนี้
- **unity-mcp-cli `--timeout` หน่วย ms** — `--timeout 150` = 0.15 วิ (ได้ 504 หน้าตัง);
  และ filter ถูกต้องคือ `testClass` ตาม schema ของ tool
- **หลักฐาน final อยู่ที่ TestResults.xml + evidence file** — console log cache มี
  stale entries จาก run เก่าปนเสมอ อย่าตัดสินจาก console อย่างเดียว

## ของที่ยังค้าง (ไม่บล็อก แต่ควรรู้)

- **ควรเพิ่ม orphan cleanup ใน test `SetUp`** — kill `McpBridge` dotnet process
  ที่ตกค้างก่อน spawn bridge ใหม่ (ตอนนี้ kill มือ: 24472 / 5312 / 18428 ในรอบเดียว)
- พิจารณาย้าย bridge conversation tests ออกจาก full suite run ผ่าน proxy หรือ
  ยกระดับเป็น standalone script — proxy window สั้นกว่า test ยาวเสมอ
- plugin ฝั่ง unity.mcp อาจควรคืน HTTP ทันทีแล้วให้ poll ผล (job pattern) แทน
  request ยาวที่โดน proxy ตัด — ข้อเสนอแนะสำหรับ upstream
