# NetcusService.cs — FileCrypt 가 덧붙인 부분

`NetcusService.cs` 와 `NetcusText.cs` 는 task-calendar-db(수행과제 캘린더)의 파일을 그대로 가져와 쓴다
(커밋 `b95e5fa`). 캘린더 쪽에서 고친 내용을 받으려면 파일을 다시 복사하면 된다.

**단, `NetcusService.cs` 는 이제 원본 그대로가 아니다.** 아래 변경이 들어 있고, 원본을 다시 복사하면
이 변경이 사라져 예전 문제가 되살아난다. 원본을 복사한 뒤에는 반드시 다시 적용할 것.

```powershell
# 캘린더 원본을 복사한 뒤, 저장소 루트에서
git apply --3way gui/NetcusService.FileCrypt.patch
```

`NetcusService.FileCrypt.patch` 는 `git diff b95e5fa -- gui/NetcusService.cs` 로 만든 것이다.
여기 적힌 변경을 캘린더 원본에도 넣으면 이 파일과 패치는 필요 없어진다.

코드 안에서는 모두 `FileCrypt 추가분` 이라는 주석으로 표시돼 있다.

| 무엇 | 왜 | 없으면 |
|---|---|---|
| `NetcusSessionAlive` + `NetcusLoginVerify(..., allowSessionReuse)` | 이미 인증된 세션이면 로그인 POST 를 건너뛴다 | 날짜마다 로그인해 15일치쯤에서 사이트가 인증을 막는다 (`a0adce3`) |
| `LoginVerify` 와 명시적 검증 경로는 `allowSessionReuse:false` | 자격증명 자체를 확인하는 자리라 세션으로 통과시키면 안 된다 | 비밀번호가 바뀐 것을 못 잡는다 |
| `QuietWindows` 속성 + `EnsureW2(background: QuietWindows)` | 전송 창을 최소화·비활성으로 띄운다 | 날짜마다 창이 떠서 포커스를 빼앗는다 (`9505eca`) |
| `CloseWindow()` | 작업 묶음이 끝나면 숨긴 보조 창을 닫는다 (`NetcusGateway.Dispose`) | 화면 밖(-32000)에 WebView2 창이 앱을 끌 때까지 남는다 |
| `KeepOvertime` 속성 + `NetcusSubmit` 의 채우기·제출에서 초과시간을 페이지 값 그대로 | FileCrypt 는 초과시간을 모른다(자료 옮기기) | 늘 0 을 보내 기존 초과근무 기록이 지워진다 — 목업에서 야근 +3시간이 0 으로 바뀌는 것으로 확인 |
| `NetcusWeekMerge` 가 날짜마다 `status`·`overtime`·`weekOthers` 도 회신 | FileCrypt 는 페이지의 `Bmodify()` 를 거치지 않고 제출하므로 주 52시간 검사를 스스로 해야 한다. `weekOthers` 는 페이지에 박힌 "그 주 나머지 날 합계"(`totalWorkingTime = todayWorkingTime + N` 의 N) | 빈 날을 정근으로 채우다 주 52시간을 넘긴다(일요일 등) — 사이트가 막으면 올리기가 멈추고, 안 막으면 법정 한도를 넘는 근태가 남는다 |
| `NetcusSubmit` 저장 확인: 빈 내용을 보냈으면 "비어 있음(0)" 에서 바로 끝냄 | 비우기는 비어 있는 것이 정답이다 | 비우는 날짜마다 14×300ms(4.2초)를 헛기다린다 — 목업에서 10일 비우기가 48초로 드러남 |

FileCrypt 가 NetcusService 를 부르는 곳은 `NetcusGateway.cs` 한 곳뿐이다. 그 밖의 연결부는
`NetcusHost.cs`(JS 회신을 C# 이벤트로 바꿈)에 있다.
