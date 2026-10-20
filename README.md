# N.I.N.A. Live Polar Alignment

두 번의 기준 촬영으로 적경축을 추정하고, 이미지에 표시된 적경축과 천구극을 보며 적도의의 고도·방위각을 조정하는 N.I.N.A. 플러그인입니다.

현재 촬영·plate solve·극축 오차 계산·조정 화면을 구현한 실험용 버전입니다. 실제 장비에서의 정확도와 갱신 속도는 검증 전입니다.

## 주요 기능

- **두 위치 보정**: RA 회전 전후의 영상 자세로 적경축을 추정합니다. 자동 회전은 기본 90°이며 60–90° 범위에서 선택할 수 있습니다. 수동 회전도 지원합니다.
- **실시간 조정 안내**: 반복 촬영과 plate solve로 전체 오차, 고도·방위각 오차, 영상 위 적경축·천구극 위치와 확대 화면을 갱신합니다.
- **카메라 스트리밍**: 지원되는 네이티브 ZWO ASI·ToupTek 경로에서는 최신 프레임을 해석합니다. 미지원 시 일반 촬영으로 전환합니다.
- **정렬 결과 확인**: 새 기준 영상으로 재측정하고, 20프레임 안정성 검사와 선택적인 독립 드리프트 검사를 수행합니다.
- **현장 기록**: 선택적으로 기준 영상, 측정값, 메모를 저장하고 오프라인으로 재계산할 수 있습니다.
- **N.I.N.A. 화면 통합**: 이미징 도킹 패널, 호스트 테마, 한국어·영어를 지원합니다.

## 사용 방법

1. 카메라와 plate solver를 연결하고 관측지, 초점거리, 픽셀 크기를 설정합니다.
2. 관측 반구의 천구극에서 15° 이내를 향하게 한 뒤 `Start`로 기준 촬영을 시작합니다.
3. 자동 모드에서는 RA 회전과 두 번째 촬영을 이어서 수행합니다. 수동 모드에서는 고도·방위각과 Dec를 고정하고 RA만 60–90° 회전한 뒤 두 번째 기준을 촬영합니다. 측정 회전량은 최소 30°여야 합니다.
4. 실시간 조정을 시작하고, 고도·방위각 볼트로 적경축 표시를 천구극 표시에 맞춥니다.
5. 조정 후 중지하고 필요하면 재측정·안정성·드리프트 검사를 실행합니다. 카메라 장착이나 설정을 바꿨다면 기준을 초기화합니다.

자동 회전 전에는 선택한 방향과 회전 범위의 장비 간섭을 확인해야 합니다. 조정 중에는 RA/Dec와 카메라 장착 상태를 유지합니다. 오차 부호는 기하학적 차이이며 나사를 돌리는 방향이 아닙니다.

현재는 대기 굴절 보정을 적용하지 않습니다. 관측지 위도는 절댓값 1° 이상 89.5° 미만이어야 합니다. 실시간 갱신마다 전체 plate solve를 수행하며, 별 정합 코어는 촬영 경로에 아직 연결하지 않았습니다.

## 소스 구조

| 경로 | 내용 |
| --- | --- |
| `src/Nina.LivePolarAlignment.Core/` | 축 추정, 오차 계산, 영상 정합, RA 회전 및 드리프트 계산 |
| `src/Nina.LivePolarAlignment/` | N.I.N.A. 촬영 연동, 정렬 세션, WPF 화면과 번역 |
| `tests/` | 코어 계산과 N.I.N.A. 통합 테스트 |
| `tools/` | 합성 영상 UI 미리보기와 현장 기록 재계산 |
| `scripts/` | 설치·패키징 |
| `docs/` | 핵심 계산, 출처와 기능별 사용 안내 |

## 빌드 및 설치

Windows x64, .NET 8 SDK 및 N.I.N.A. 3.2.0.9001 이상을 대상으로 합니다.

```powershell
dotnet build src/Nina.LivePolarAlignment -c Release
dotnet test tests/Nina.LivePolarAlignment.Core.Tests -c Release
dotnet test tests/Nina.LivePolarAlignment.Integration.Tests -c Release
.\scripts\Package-Plugin.ps1
```

패키지는 `artifacts/packages/`에 생성합니다. ZIP을 풀고 N.I.N.A.를 종료한 뒤 포함된 `Install-Plugin.ps1`을 실행합니다. 재시작 후 이미징 화면에서 `Live Polar Alignment` 패널을 엽니다. [설치 안내](docs/install-package.txt)

통합 테스트와 UI 미리보기에는 설치된 N.I.N.A.의 네이티브 라이브러리가 필요합니다. [통합 테스트 설정](tests/Nina.LivePolarAlignment.Integration.Tests/README.md) · [합성 UI 미리보기](tools/Nina.LivePolarAlignment.Preview/README.md)

## 구현 및 사용 문서

- [극축 정렬 로직과 출처](docs/polar-alignment.md)
- [자동 RA 회전](docs/auto-ra-rotation.md)
- [스트리밍 동작과 제한](docs/streaming-review.md)
- [정렬 후 재측정](docs/verification.md)
- [독립 드리프트 검증](docs/independent-drift.md)
- [현장 기록과 오프라인 재계산](docs/field-validation.md)
