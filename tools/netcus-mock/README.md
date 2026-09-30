# 목업 근태관리 사이트

`www.netcus.com/pjm` 의 흉내. FileCrypt 의 근태관리 올리기·가져오기를 **실제 사이트 없이** 끝까지 돌려 보려고 만들었다.

## 쓰는 법

| 하려는 것 | 명령 |
|---|---|
| 자동 테스트 (전체 흐름 32건) | `powershell -ExecutionPolicy Bypass -File .\engine\tests\test-netcus-mock.ps1` |
| 화면에서 직접 눌러 보기 | `powershell -ExecutionPolicy Bypass -File .\tools\netcus-mock\start-mock.ps1` (계정 `mock` / `mock1234`) |
| 저장 잘림·느린 응답을 켜고 눌러 보기 | `start-mock.ps1 -Truncate 500 -Delay 300` |

앱은 환경변수 `FILECRYPT_NETCUS_MOCK=<포트>` 가 있을 때만 `www.netcus.com` 을 `127.0.0.1:<포트>` 로 돌린다
(WebView2 `--host-resolver-rules`, 목업일 때만 인증서 검사 끔, WebView2 프로필도 `wv2-mock` 으로 따로).
그때 메인 창 제목에 `[목업 근태관리 127.0.0.1:포트]`, 근태관리 창에 `[목업]` 이 붙는다.
설정·계정은 `FILECRYPT_DATA_DIR` 로 따로 둔다 — `start-mock.ps1` 과 테스트가 알아서 정한다.

자가 테스트 모드 `FileCrypt.exe --netcus-selftest 시나리오.json` 은 두 환경변수가 **둘 다** 있을 때만 돈다.

## 무엇을 흉내 내나

2026-09-30 에 실제 사이트에서 떠 온 구조(`FileCrypt.exe --netcus-capture`, 읽기 전용)를 따른다.
사이트의 스크립트·이미지·내용은 가져오지 않고 새로 썼다.

| 실제 | 목업 |
|---|---|
| `login.htm`: form `name=form` `action=loginRSA.jsp`, `UserName_Enc`/`Password_Enc`/`id`/`pass`, `goLogin()` → `Encrypt('SST/PublicKey.xml')` → submit, 끝에 `pass` 를 `xxxxxxxxx` 로 덮음 | 같음. 암호화만 RSA 대신 `MOCK:` + base64 |
| `loginRSA.jsp` POST → `JSESSIONID`(Path=/pjm; Secure) → `pjm.jsp` | 같음 |
| 로그인 안 된 `pjm_work_view.jsp` → 빈 줄 + `<meta http-equiv='Refresh' content='0; URL=login.htm'>` (64바이트) | 같음 |
| `pjm_work_view.jsp`: euc-kr, form 이 table 안(옛 마크업), multipart, `dbstatus`/`status`(선택지 12개)/`overtime`(0~11)/`content`, `Bmodify()` → `go=write&table=report_tbl&y&m&d&id` | 같음 |
| 기록(`go=write`) 응답 | **캡처하지 않음**(실제 일간보고에 쓰게 되므로). 그 날짜로 되돌아가는 것으로 가정 |
| 로그인 실패 응답 | **캡처하지 않음**. `login.htm` 으로 되돌아가는 것으로 가정 |

## 일부러 만들 수 있는 상황 (`FileCryptMock.NetcusMock` 속성)

| 속성 | 상황 |
|---|---|
| `TruncateContentAt` | 사이트가 긴 글을 잘라 저장 |
| `DropWritesOn` / `DropWritesOnceOn` | 기록을 받는 척하고 저장하지 않음 (항상 / 한 번만) |
| `ExpireSessionAfter` | 세션 하나로 N번 요청하면 로그인이 풀림 |
| `BlockLoginsAfter` | 로그인이 N번을 넘으면 전부 거절 (몰린 로그인 차단) |
| `HangOnPath` | 그 경로로 오는 요청에 응답하지 않음 |
| `DelayMs` | 모든 응답을 늦춤 |
| `Enforce52` (기본 켬) | 기록 후 그 주(월~일) 합계가 52시간을 넘으면 저장 거부. 페이지의 `Bmodify()` 에도 실제처럼 "그 주 나머지 합계" 를 박아 둔다. 실제 서버가 막는지는 모른다 |

`LoginPosts`, `WriteLog`, `RequestLog`, `GetDay()` 로 사이트 쪽에 실제로 무엇이 남았는지 확인할 수 있다.

## 한계

목업은 **우리가 아는 사이트 동작**만 재현한다. 지금까지 실제로 문제가 된 것(몰린 로그인 차단, 로그인 실패가
잠깐 성공처럼 보이는 것, euc-kr 재인코딩)은 모르는 동작이었다. 사이트와 맞닿는 코드(`NetcusService.cs`,
`NetcusGateway.cs`)를 바꿨을 때는 실제 사이트에서 지난 날짜 1~2일로 짧게 한 번 확인한다.
사이트 구조가 바뀐 것 같으면 `FileCrypt.exe --netcus-capture` 로 다시 떠서 비교한다
(결과는 `%LOCALAPPDATA%\FileCrypt\netcus-capture\` — 실제 보고 내용이 들어 있으니 저장소에 넣지 않는다).
