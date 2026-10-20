# 극축 정렬 로직과 출처

현재 구현의 계산 경로를 정리합니다. 수학 코어는 `src/Nina.LivePolarAlignment.Core/`, 촬영·좌표 변환·세션은 `src/Nina.LivePolarAlignment/Capture/`에 있습니다.

## 촬영과 좌표 변환

`NinaPolarAlignmentCaptureService.CaptureAndSolveAsync`는 N.I.N.A. 카메라·이미징 mediator와 설정된 plate solver를 사용합니다. 중심 적경·적위, 영상 회전각, 반전, 픽셀 스케일, 영상 크기와 노출 중간 시각을 하나의 프레임으로 전달합니다. 촬영 점유는 종료·취소 시 해제합니다.

`PolarAlignmentSession.ToHorizon`은 J2000 영상의 중심과 접선 방향을 노출 중간 UTC, 관측지 위도·경도와 N.I.N.A.의 SOFA 변환으로 수평 좌표계에 옮깁니다. 방향 벡터는 동·북·천정(ENU) 순서입니다. 영상 자세는 `FromEquatorial`에서 구성하고, 반전은 영상 오른쪽 축에 반영합니다. DUT1이 없거나 유효하지 않으면 0초를 사용하며 `UsedApproximateEarthRotation`에 표시합니다. 현재 대기 굴절과 관측지 고도는 적용하지 않습니다.

## 두 위치에서 적경축 추정

`PolarAlignmentSession.AddCalibration`은 RA만 회전한 두 영상의 자세를 비교합니다. 고도·방위각, Dec와 카메라 장착은 고정해야 합니다.

`PolarAlignmentGeometry.EstimateAxis(SkyOrientation, SkyOrientation)`는 두 정규 직교 기저의 대응 축 외적을 합산해 상대 회전축을 구합니다. 회전각은 기저 내적 합으로 계산하며, 영상 반전 변경과 불안정한 회전을 거부합니다. 축의 부호는 관측 반구의 극을 향하도록 선택합니다.

세션은 실제 회전량 30° 이상과 천구극 15° 이내를 요구합니다. 권장 회전량은 60–90°입니다. 중심 방향 두 개만으로 축을 유일하게 결정하지 않으며 영상 회전각을 함께 사용합니다. 세 방향 이상을 평면 적합하는 별도 코어 오버로드도 있지만 현재 두 위치 촬영 흐름에는 사용하지 않습니다.

## 극축 오차와 이미지 표시

`CalculateError`는 단위 적경축 `a = (E, N, U)`를 관측 반구의 극 벡터 `p`와 비교합니다. 위도 `φ`에서 북반구의 극은 `(0, cos(φ), sin(φ))`이며 남반구에서는 그 반대 방향을 사용합니다. 내부 각도는 라디안, 반환값은 각분입니다.

```text
전체 오차 = atan2(length(cross(a, p)), dot(a, p))
고도 오차 = asin(U) - abs(φ)
방위각 오차 = wrapToPi(atan2(E, N) - atan2(p.E, p.N))
```

고도 오차 양수는 축이 너무 높은 상태입니다. 방위각은 북쪽에서 동쪽으로 증가하며 목표 극에 대한 부호 있는 차이입니다. 두 값은 현재 축의 오차이며 볼트 회전 명령이 아닙니다. 방위각 차이를 고도 오차와 단순 제곱합해 전체 오차로 사용하지 않습니다.

`ProjectDirection`은 gnomonic 투영으로 축과 극을 영상에 표시합니다. 초점거리 픽셀값은 `206264.806 / 픽셀 스케일(각초/픽셀)`이며, 영상 Y축은 아래쪽으로 증가합니다. 전방 반구 밖의 방향은 투영하지 않습니다.

## 반복 측정과 검증

`GetGuidance`는 두 번째 기준 자세에서 현재 촬영 자세로 `TransformDirection`을 적용해 축을 갱신합니다. 각 프레임의 좌표 변환에 촬영 시각을 사용합니다. RA/Dec 이동이나 카메라 재장착은 기준 모델을 무효화하므로 재보정해야 합니다. 영상 크기·반전 변경, 2%를 넘는 스케일 변경과 역순 시각을 거부합니다.

현재 조정 프레임은 매번 전체 plate solve를 수행합니다. 스트리밍은 용량 1의 `LatestFrameBuffer`로 오래된 프레임을 버리고 촬영·해석을 겹칩니다. `FrameRegistration`의 빠른 별 정합은 코어와 테스트만 있으며 실시간 촬영 경로에는 연결하지 않았습니다.

`DriftValidation`은 두 위치의 Dec 변화율과 시간각으로 작은 극축 오차를 추정합니다. 재측정은 새 RA 구간에서 기준 두 장을 다시 구하고, Stability는 정지 상태 20프레임의 표본 표준편차를 계산합니다. 반복성과 절대 정확도를 구분합니다.

- `tests/Nina.LivePolarAlignment.Core.Tests/`: 축 추정, 반구·반전·투영, 영상 정합, RA 회전, 최신 프레임 버퍼와 드리프트 계산.
- `tests/Nina.LivePolarAlignment.Integration.Tests/`: 실제 N.I.N.A. SOFA/NOVAS로 만든 독립 좌표 입력, 시간·기준 변경과 촬영 상태 경계.
- `tools/Nina.LivePolarAlignment.Preview/`: 합성 입력으로 실제 WPF 화면과 워크플로 검증.
- 실제 장비의 축 정확도·처짐·갱신 시간·회전 정지 동작은 별도 현장 검증 대상입니다.

## 참고 출처

- [IAU SOFA 공식 라이브러리와 문서](https://www.iausofa.org/current-software): 천문 좌표 및 관측 시각 변환의 기준. 런타임에서는 N.I.N.A.의 SOFA 래퍼를 사용합니다.
- [SharpCap 공식 Polar Alignment 설명](https://docs.sharpcap.co.uk/4.1/9_PolarAlignment.htm): 두 기준 촬영, RA 회전량과 영상 기반 조정 흐름 참고.
- [PHD2 Drift Alignment 안내](https://openphdguiding.org/man/Tutorials.htm): 자오선·동서쪽 두 위치에서 Dec 드리프트를 관측하는 절차 참고.
- [N.I.N.A. 공식 이미징 문서](https://nighttime-imaging.eu/docs/master/site/tabs/imaging/): 촬영과 카메라별 LiveView 동작 참고.
- [N.I.N.A. 플러그인 템플릿](https://github.com/isbeorn/nina.plugin.template): 플러그인 확장 구조 참고. 실제 대상 API 버전은 프로젝트의 `.csproj`를 기준으로 합니다.
