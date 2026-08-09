# WalkDistance — 보행거리 계산

건축 DXF 도면의 벽을 보행 가능 격자로 변환하고, 각 지점에서 가장 가까운 출구까지의 보행거리를 보여 주는 Windows 데스크톱 프로그램입니다.

- GitHub Release: https://github.com/sunho0248/WalkDistance/releases/tag/v1.3.6
- 지원 환경: Windows 10/11 x64


- 라이선스·코드: 상용 제품의 코드·UI·자산을 복제하지 않은 독자 구현

## 바로 실행하기

가장 쉬운 방법은 [v1.3.6 Release](https://github.com/sunho0248/WalkDistance/releases/tag/v1.3.6)에서 `WalkDistance-win-x64.zip`를 내려받는 것입니다.

1. ZIP 파일의 압축을 풉니다.
2. `WalkDistance.exe`를 실행합니다.

이 배포 파일은 **자체 포함(Self-contained)** 패키지이므로 .NET Desktop Runtime을 별도로 설치할 필요가 없습니다. ZIP에는 실행에 필요한 모든 파일이 포함되어 있습니다.

## 업데이트

시작할 때 한 세션에 한 번만 공개 GitHub Release를 확인합니다. 새 버전이 있으면 설치 여부를 묻고, **예**를 선택한 경우에만 `WalkDistance-win-x64.zip`를 내려받습니다. 다운로드한 파일은 GitHub Release API의 SHA-256 digest와 일치할 때만 설치하며, 네트워크 오류는 작업을 방해하지 않도록 알림 없이 건너뜁니다.

업데이트 도우미와 자체 포함 런타임 파일은 ZIP 루트에 포함됩니다. 설치 중에는 앱이 종료된 뒤 도우미가 안전한 임시 폴더에 압축을 풀고 기존 파일을 백업한 뒤 교체합니다. 새 앱을 시작하지 못하면 백업을 복원합니다. `.walkdistance` 프로젝트는 설치 폴더 밖에 저장하면 업데이트의 영향을 받지 않습니다.

## 주요 기능

- ASCII DXF의 `LINE`, `LWPOLYLINE`, `POLYLINE`/`VERTEX`, `CIRCLE`, `ARC` 벽 geometry 불러오기
- 여러 출구 작성·선택·이동·삭제
  - 출구는 `EXIT 1`, `EXIT 2`처럼 자동 이름이 붙고 화면에 표시됩니다.
  - 출구 작성 모드에서 벽 위를 한 번 클릭하면 해당 위치를 중심으로 고정 길이 출구가 만들어집니다.
  - 출구 선택·이동·작성 상태는 `Esc`로 언제든 취소할 수 있으며, 이미 저장된 출구와 계산 결과는 유지됩니다.
- 거리 맵과 보행경로를 독립적으로 표시·숨기기
- 5m 단위의 명확한 단색 거리 밴드 및 등거리선 표시
- 임의 지점 클릭 시 가장 가까운 출구까지의 안전한 경로와 거리 조회
- 인체 치수 적용 시 어깨 너비의 절반을 벽·출구 가장자리 여유로 사용하고, 경로는 사람이 실제로 설 수 있는 중심점까지 표시
- 계산 진행 단계 표시 및 실행 중 중복 계산 방지
- `.walkdistance` 프로젝트 저장·열기
  - `Ctrl+S`로 저장하고 `Ctrl+Shift+S`로 다른 이름으로 저장합니다.
  - 벽 geometry, 출구 경로, 셀 크기, 단위 배율과 계산된 거리 맵·경로·조회 결과를 함께 보존합니다.
  - 창 제목에서 현재 파일명과 저장되지 않은 변경사항을 확인할 수 있습니다.
- 출구 미리보기 양 끝의 빨간 기준점 표시 및 출구 작성 중 가운데 버튼 드래그 화면 이동
- 출구 미리보기는 마우스 클릭 또는 `Space` 키로 확정
- `Zoom Window`: 점선 십자선으로 경계를 확인하며 사각 영역을 드래그해 원하는 범위만 CAD 방식으로 한 번 확대
- 최대 **10,000,000개 셀** 분석 지원

## 사용 방법

1. **파일 > DXF 열기**에서 도면을 엽니다. DXF에 단위 정보가 없으면 원본 단위(mm, cm, m, inch, ft)를 선택합니다.
2. 기본 셀 크기 `0.2m`를 필요에 맞게 조정합니다. 작은 셀일수록 정밀하지만 계산 시간과 메모리 사용량이 늘어납니다.
3. **출구 지정 모드**를 켭니다.
   - 벽 또는 연결된 도형 위를 한 번 클릭하면 마우스 위치를 출구 중앙으로 하는 출구가 작성됩니다.
   - 미리보기 중 `Space` 키를 눌러도 같은 출구를 확정할 수 있습니다.
   - 기존 출구를 선택하면 새 벽 위치로 이동할 수 있습니다.
   - `Esc`는 현재 작성·선택·이동만 취소합니다.
4. **보행거리 계산**을 누릅니다. 상태 표시줄과 진행 표시에서 격자 생성, 출구 source 생성, 거리 계산, 결과 렌더링 단계를 확인할 수 있습니다.
5. 계산 후 도면을 클릭하면 해당 지점에서 가장 가까운 출구까지의 거리와 경로를 조회합니다.
6. **파일 > 프로젝트 저장** 또는 `Ctrl+S`로 `.walkdistance` 파일을 저장합니다. 계산된 거리 맵도 함께 저장되므로 다시 열 때 결과를 복원합니다.
7. 원하는 영역만 확대하려면 도구 모음의 **Zoom Window**를 켠 뒤 점선 십자선 기준으로 도면에서 사각형을 드래그합니다. 확대가 적용되면 모드는 자동으로 꺼지며, `Esc`로도 취소할 수 있습니다.

`디스턴스 맵`과 `보행경로` 표시 옵션은 독립적입니다. 디스턴스 맵만 표시하면 거리 밴드만 보이고, 경로 시작점·경로·거리 라벨은 보행경로 옵션을 켰을 때만 표시됩니다.

인체 치수 적용 상태에서 보행경로의 점선 원은 출발 지점의 몸통 여유이고, 실선 원은 출구 가까이 실제로 도달한 사람 중심입니다. 따라서 경로와 실선 원은 벽/출구 선에서 끝나지 않으며, 출구가 너무 좁거나 도달할 수 없으면 도착 원을 표시하지 않습니다. 인체 치수를 끄면 기존처럼 기하학적 출구 선까지의 경로를 사용하고 몸통 여유 원은 표시하지 않습니다.

일부 내부 셀이 기하학적으로 출구에 연결되지 않는 경우는 정상적인 분석 결과이며 팝업 오류를 띄우지 않습니다. 실제 계산 오류만 안내합니다.

## 지원 DXF와 제약

### 지원

- ASCII DXF의 `LINE`, `LWPOLYLINE`, `POLYLINE`/`VERTEX`, `CIRCLE`, `ARC`
- `$INSUNITS` 1~24의 미터 단위 변환. 값이 없거나 0이면 사용자 선택
- 원과 호의 선분 근사 및 격자화

### 제약

- Binary DXF, spline, block insert, hatch 영역은 지원하지 않습니다.
- 벽 두께와 문 폭은 모델링하지 않습니다.
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
Compress-Archive -Path dist\win-x64\* -DestinationPath dist\WalkDistance-win-x64.zip -Force
```

Release에는 `dist\WalkDistance-win-x64.zip`를 `WalkDistance-win-x64.zip` 이름으로 올립니다. publish 검증은 ZIP에 들어갈 루트에 `WalkDistance.Updater.exe`와 도우미 런타임 파일이 없으면 실패합니다.

## 프로젝트 구조

- `src/WalkDistance.Core`: DXF, geometry, 격자, 경로 탐색, 프로젝트 파일
- `src/WalkDistance.App`: 한국어 WPF UI와 거리 맵 렌더링
- `src/WalkDistance.Update`: GitHub Release, SHA-256, 안전한 ZIP 처리
- `src/WalkDistance.Updater`: 종료된 앱 파일을 교체하는 별도 업데이트 도우미
- `tests/WalkDistance.Core.Tests`: 자동 테스트
- `samples`: 수동 확인용 DXF
