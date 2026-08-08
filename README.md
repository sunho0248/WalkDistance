# WalkDistance — 보행거리 계산

건축 DXF 도면의 벽을 보행 가능 격자로 변환하고, 각 지점에서 가장 가까운 출구까지의 보행거리를 보여 주는 Windows 데스크톱 프로그램입니다.

- GitHub Release: https://github.com/sunho0248/WalkDistance/releases/tag/v1.0.0
- 지원 환경: Windows 10/11 x64
- 라이선스·코드: 상용 제품의 코드·UI·자산을 복제하지 않은 독자 구현

## 바로 실행하기

가장 쉬운 방법은 [v1.0.0 Release](https://github.com/sunho0248/WalkDistance/releases/tag/v1.0.0)에서 `WalkDistance-v1.0.0-win-x64.zip`를 내려받는 것입니다.

1. ZIP 파일의 압축을 풉니다.
2. `WalkDistance.App.exe`를 실행합니다.

이 배포 파일은 **자체 포함(Self-contained)** 패키지이므로 .NET Desktop Runtime을 별도로 설치할 필요가 없습니다.

## 주요 기능

- ASCII DXF의 `LINE`, `LWPOLYLINE`, `POLYLINE`/`VERTEX`, `CIRCLE`, `ARC` 벽 geometry 불러오기
- 여러 출구 작성·선택·이동·삭제
  - 출구는 `EXIT 1`, `EXIT 2`처럼 자동 이름이 붙고 화면에 표시됩니다.
  - 출구 작성 모드에서 벽 위를 한 번 클릭하면 해당 위치를 중심으로 고정 길이 출구가 만들어집니다.
  - 출구 선택·이동·작성 상태는 `Esc`로 언제든 취소할 수 있으며, 이미 저장된 출구와 계산 결과는 유지됩니다.
- 거리 맵과 보행경로를 독립적으로 표시·숨기기
- 5m 단위의 명확한 단색 거리 밴드 및 등거리선 표시
- 임의 지점 클릭 시 가장 가까운 출구까지의 안전한 경로와 거리 조회
- 계산 진행 단계 표시 및 실행 중 중복 계산 방지
- 프로젝트 저장·열기: 벽 geometry, 출구 경로, 셀 크기, 단위 배율 보존
- 최대 **10,000,000개 셀** 분석 지원

## 사용 방법

1. **파일 > DXF 열기**에서 도면을 엽니다. DXF에 단위 정보가 없으면 원본 단위(mm, cm, m, inch, ft)를 선택합니다.
2. 기본 셀 크기 `0.2m`를 필요에 맞게 조정합니다. 작은 셀일수록 정밀하지만 계산 시간과 메모리 사용량이 늘어납니다.
3. **출구 지정 모드**를 켭니다.
   - 벽 또는 연결된 도형 위를 한 번 클릭하면 마우스 위치를 출구 중앙으로 하는 출구가 작성됩니다.
   - 기존 출구를 선택하면 새 벽 위치로 이동할 수 있습니다.
   - `Esc`는 현재 작성·선택·이동만 취소합니다.
4. **보행거리 계산**을 누릅니다. 상태 표시줄과 진행 표시에서 격자 생성, 출구 source 생성, 거리 계산, 결과 렌더링 단계를 확인할 수 있습니다.
5. 계산 후 도면을 클릭하면 해당 지점에서 가장 가까운 출구까지의 거리와 경로를 조회합니다.

`디스턴스 맵`과 `보행경로` 표시 옵션은 독립적입니다. 디스턴스 맵만 표시하면 거리 밴드만 보이고, 경로 시작점·경로·거리 라벨은 보행경로 옵션을 켰을 때만 표시됩니다.

일부 내부 셀이 기하학적으로 출구에 연결되지 않는 경우는 정상적인 분석 결과이며 팝업 오류를 띄우지 않습니다. 실제 계산 오류만 안내합니다.

## 지원 DXF와 제약

### 지원

- ASCII DXF의 `LINE`, `LWPOLYLINE`, `POLYLINE`/`VERTEX`, `CIRCLE`, `ARC`
- `$INSUNITS` 1~24의 미터 단위 변환. 값이 없거나 0이면 사용자 선택
- 원과 호의 선분 근사 및 격자화

### 제약

- Binary DXF, spline, block insert, hatch 영역은 지원하지 않습니다.
- 벽 두께, 문 폭, 사람 반경은 모델링하지 않습니다.
- 경로는 raster 격자와 보수적 line-of-sight 기반 Theta* 근사입니다. 원본 벡터 geometry의 전역 최단경로를 보장하지는 않습니다.
- 10,000,000 셀은 메모리 폭주 방지 상한입니다. 큰 도면에서 너무 작은 셀 크기를 선택하면 계산 시간이 길어질 수 있습니다.

## 개발 환경과 검증

소스에서 실행하거나 개발하려면 .NET 8 SDK가 필요합니다.

```powershell
dotnet restore WalkDistance.sln
dotnet run --project src/WalkDistance.App/WalkDistance.App.csproj
```

테스트와 Release 빌드:

```powershell
dotnet test WalkDistance.sln -c Release --no-restore
dotnet build src/WalkDistance.App/WalkDistance.App.csproj -c Release -r win-x64 --no-restore
dotnet publish src/WalkDistance.App/WalkDistance.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o dist/win-x64
```

## 프로젝트 구조

- `src/WalkDistance.Core`: DXF, geometry, 격자, 경로 탐색, 프로젝트 파일
- `src/WalkDistance.App`: 한국어 WPF UI와 거리 맵 렌더링
- `tests/WalkDistance.Core.Tests`: 자동 테스트
- `samples`: 수동 확인용 DXF
