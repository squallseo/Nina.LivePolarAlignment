# SharpCap 극축 정렬 조사 — 2026-10-18

## 범위와 대상

사용자가 보유한 SharpCap 4.1.14130.0 설치 번들을 정적으로 분석했다. 프로그램이나 유료 기능은 실행하지 않았다.
REA의 PE/CLI 메타데이터·CIL·호출 관계와 ILSpy 11.1.0.9782의 타입별 복원 결과를 함께 사용했다.
복원 소스는 원본 소스가 아니며 일부 의존 타입이 해석되지 않았다는 표시도 보존한다.
실제 처리 시간·정확도·장비 동작은 관측하지 않았다.

로더 구조에 따라 `SharpCap.app.dat`의 선택한 항목을 로컬로 추출했다.
각 항목은 패키지 내부 SHA-256과 추출 결과의 일치를 검사했다. 이는 배포자 인증을 의미하지 않는다.

| 어셈블리 | SHA-256 | MVID |
| --- | --- | --- |
| SharpCap.App.dll | `d2073c86b476e16ff5714edaeeaf165ea655d5161a6f6eeec4da270cf64b8430` | `1c550dcf-58f3-4d66-8db5-cc5930b13e5c` |
| SharpCap.ImageProcessing.dll | `60dd9785376485babc865f20d2ee77ef46bf037abd3350af79237b65e6967272` | `83dae6c7-c1d4-4f9f-a0a6-7d46cc8b5f7d` |

두 어셈블리는 .NET 8 CIL이다. `SharpCap.StarDetector.dll`은 CLI 디렉터리가 없는 네이티브 PE이며 내부 분석은 미수행이다.

## 확인한 구조

| 관측 | 근거 | 구현에 주는 의미 |
| --- | --- | --- |
| 첫 촬영, RA 회전 촬영, 조정 촬영이 분리된다. | `PolarAlignmentModel.HandleFrame`의 단계별 호출 | 다음 행동 하나씩 안내한다. |
| 조정 프레임도 해석한다. | `SolveAdjustmentFrame → SolveFrame → IPlateSolver.Solve` | 단순 추적만 한다는 가정은 틀리다. |
| 극 주변 전용 인덱스를 사용하는 내부 quad solver가 있다. | `CreateQuadSolver → PolarQuadIndexProvider.GetIndex → QuadSolver` | 작은 해석 범위가 빠른 응답의 유력한 원인이다. 속도 비교는 미측정이다. |
| 북·남반구 인덱스를 재사용한다. | `PolarQuadIndexProvider`의 `Lazy` 필드 및 복원 결과 | 인덱스를 매 프레임 다시 만들지 않는다. |
| 회전 전후 변환으로 회전 중심·회전량을 계산한다. | `Calculate`의 역행렬 및 회전 관련 호출 | 충분한 RA 회전과 고정된 장착 상태가 중요하다. |
| 기준 별과 목표점을 갱신한다. | `CalculateRefStarAndTarget`, `ChooseReferenceStar` | 이미지에서 옮길 지점이 수치만 보는 것보다 직관적이다. |

플러그인은 SharpCap 바이너리나 복원 소스를 포함하지 않는다. 자체 구현은
두 WCS 방향의 상대 3D 회전에서 축을 추정하고, 촬영 중간 시각의 지평 좌표계에서 오차를 계산한다.

## Evidence와 한계

로컬 `analysis/rea-polar-evidence.json`에 11개 기록을 내보냈다. 주요 parent Evidence:

- 앱 식별: `ev_96aa469a70b16fc74b3ac6b183916c3ec81770164682db932acce4ab68837089`
- 앱 극축 타입: `ev_34930e459e26d3b73704f6d0ebae2fc4cec04590ef75f815185ee617b45e88a6`
- ImageProcessing 식별: `ev_54976040915bedaa4a503615a8b48062360b028a9e58a7a54bc5490a8a48082c`
- PolarAligner 멤버: `ev_a59c4f604c4b499b202d536ac93bd8ffa19d0aaa1de6292f00fb88fe20722226`
- 호출 관계 뷰: `ev_7d158e09de03ecb71da543a79394dca5886b8ff4b76392d06df5d602c4faaa02`

선택한 타입의 멤버 coverage는 complete다. PolarAligner의 user-string coverage는 partial이며
한 문자열의 ECMA-335 terminal flag 불일치가 보고되었다. 토큰은 위 SHA-256/MVID에 종속된다.
런타임 관측 증거는 없다.

공개 설명: [SharpCap Polar Alignment](https://docs.sharpcap.co.uk/4.1/9_PolarAlignment.htm).

## 현재 자체 구현과 후속 작업

두 기준 프레임, 취소 가능한 N.I.N.A. 촬영 및 solver, 극축 추정, 오차·이미지 표시와 합성 데모를 구현했다.
빠른 별 정합 코어는 실제 촬영 경로에 아직 연결하지 않았다.
실제 카메라의 좌표/반전 규약, 처짐, 굴절 영향, 화면 밖 극 안내,
전체 갱신 시간 및 해석 실패 복구는 후속 검증 대상이다.

## 자체 구현 검증

- Release 빌드 성공. N.I.N.A. 전이 의존성의 NU1701 경고 2종은 남아 있다.
- 코어 합성 테스트 15개 통과: 회전축 추정, 반구, 반전, 투영, 별 누락·이상치 및 다른 별 필드 거부.
- Windows 통합 테스트 8개 통과: N.I.N.A.의 실제 SOFA/NOVAS로 만든 독립 역변환 입력,
  72° RA 보정, 알려진 고도·방위각 수정, 시간·타임존 변화, 픽셀 투영, 상태·설정 변화 거부.
- N.I.N.A. UT1 자료가 NaN이면 SOFA에 전달하지 않고 DUT1=0초 근사를 사용한다.
  `UsedApproximateEarthRotation`으로 이를 구분한다. 대기 굴절·현장 장비 검증은 별도다.
- 실제 WPF 화면을 별도 STA harness에서 렌더링하고, 합성 촬영 공급자로
  보정→실시간 표시→중지→오래된 안내 제거를 확인했다. 이는 호스트 내 로딩·실제 촬영 검증이 아니다.
