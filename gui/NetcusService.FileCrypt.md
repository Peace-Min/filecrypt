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

FileCrypt 가 NetcusService 를 부르는 곳은 `NetcusGateway.cs` 한 곳뿐이다. 그 밖의 연결부는
`NetcusHost.cs`(JS 회신을 C# 이벤트로 바꿈)에 있다.
