# N.I.N.A. Live Polar Alignment

N.I.N.A.용 실시간 극축 정렬 플러그인 개발 작업 공간입니다.
현재는 REA 분석 환경과 빌드 가능한 플러그인 진입점만 제공합니다.
카메라 연결, 정렬 계산, 조정 화면은 아직 구현되지 않았습니다.

## 개발

```powershell
.\scripts\bootstrap.ps1
.\scripts\rea.ps1 --version
dotnet build src/Nina.LivePolarAlignment -c Release
```

Node 24.22.0과 REA 6.5.0을 `.tools` 아래에 설치합니다. 전역 Node는 변경하지 않습니다.
bootstrap은 Codex 설정을 수정하지 않습니다. MCP 등록은 REA setup으로 별도 관리합니다.

## 분석

```powershell
.\scripts\rea.ps1 inspect-managed-artifact 'C:\path\SharpCap.exe' --json
.\scripts\rea.ps1 inspect-managed-members 'C:\path\SharpCap.exe' --type 'Exact.Namespace.Type' --json
```

설치 프로그램과 실제 .NET 어셈블리를 구분해야 합니다. 네이티브 구성 요소는 별도 분석 엔진이 필요합니다.
분석 계획과 근거는 [docs/research-plan.md](docs/research-plan.md)를 참고하세요.
