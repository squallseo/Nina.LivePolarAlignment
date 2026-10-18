# 초기 조사 — 2026-10-18

## 준비 및 검증

- 프로젝트 전용 Node 24.22.0 / REA 6.5.0 설치.
- Codex MCP와 번들 스킬 설치 완료. `doctor --client codex --json`의 `healthy: true` 확인.
- 전체 환경의 `environment_healthy: false`는 Ghidra/IDA/JADX 등 별도 엔진 미설정을 포함한다. .NET 정적 분석은 실제 실행으로 확인했다.
- MCP는 현재 대화에 동적으로 추가되지 않는다. Codex 재시작 후 도구 목록을 확인한다. 현재 세션은 CLI 사용 가능.
- Release 빌드 성공. 전이 의존성 ToastNotifications, VVVV.FreeImage에서 NU1701 경고가 있다. 실제 N.I.N.A. 로딩 시험은 아직 하지 않았다.

## SharpCap 대상 식별

사용자가 보유한 다운로드 `SharpCapInstall-4.1.14130.0-64bit.exe`에서 CAB를 추출한 뒤,
MSI administrative extraction(`/a`, TARGETDIR)으로 `.tools/sharpcap-extracted`에 파일을 풀었다.
SharpCap 프로그램은 실행하지 않았다.

| 파일 | SHA-256 |
| --- | --- |
| 설치 번들 | `C3F044C735B404FB606631C236B6310CF0BE7240E92C62E6968C4B68287EB016` |
| SharpCap.dll | `73FE0278F884EF547BA39A28C9037194D55FF2477D18D14BD6BE8814D59E2C79` |

REA `inspect-managed-artifact` 결과:

- `SharpCap.exe`: CLI 디렉터리 없음. 이 파일에 .NET 메타데이터가 없다는 관측이며, 제품 전체가 네이티브라는 뜻은 아니다.
- `SharpCap.dll`: managed / modern-dotnet / x86_64, assembly version `4.1.14130.0`, metadata complete.
- `inspect-managed-members` coverage complete, 14개 타입. 주요 타입은 `Packer.QuickArchiveReader`, `SharpCap.Launcher.AssemblyLoader`, `SharpCap.Launcher.Program`이다.
- `Decrypt`, `TryLoadViaPackOrFile`, `Main` 등 로더 메서드가 관측된다. 이 이름만으로 암호 형식이나 극축 정렬 동작을 추론하지 않는다.
- 설치 파일 목록에 `SharpCap.app.dat`, `SharpCap.StarDetector.dll`, `npolarstars.txt`, `spolarstars.txt`가 있다. 파일 이름은 역할의 후보 근거이며 알고리즘의 증거는 아니다.

로컬 원본 Evidence는 `analysis/sharpcap-artifact.json`, `analysis/sharpcap-dll-artifact.json`,
`analysis/sharpcap-members.json`에 보관한다. 대상 바이너리와 전체 분석 출력은 Git에 올리지 않는다.

## 다음 조사

실제 기능 어셈블리의 위치/컨테이너를 확인하고 polar alignment 상태 전환과
별 검출·추적·회전축 계산을 좁혀 조사한다. 현재 결과에는 극축 정렬 알고리즘이 포함되어 있지 않다.
플러그인 진입점만 구현되었으며 촬영 및 정렬 기능은 후속 구현 대상이다.
