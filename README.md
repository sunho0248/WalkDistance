# 보행거리 계산 프로그램

건축 DXF 도면의 벽을 격자로 변환하고, 여러 출구 중 가장 가까운 곳까지의 최단
보행거리를 표시하는 C# .NET 8 WPF 데스크톱 프로그램이다. 경로 탐색과 UI는
Simulex 등 상용 제품의 코드·UI·자산을 참조하거나 복제하지 않고 독자 구현했다.

## 요구 환경과 실행

- Windows 10/11 x64
- .NET 8 SDK

```powershell
dotnet restore WalkDistance.sln
dotnet run --project src/WalkDistance.App/WalkDistance.App.csproj
```

## 사용법

1. `파일 > DXF 열기`에서 도면을 연다. `$INSUNITS`가 없거나 unitless이면 표시되는
   창에서 원본 좌표 단위(mm, cm, m, inch, ft)를 반드시 선택한다.
2. `출구 지정 모드`를 켠 뒤 시작점과 끝점을 차례로 클릭해 출구 선분을 지정한다.
   첫 점을 찍은 뒤에는 마우스를 따라 미리보기 선이 표시된다. 이때 우클릭하면 지정
   중인 선만 취소하고, 지정 중인 선이 없으면 마지막 완료 선분을 삭제한다. 우클릭은
   출구 지정 모드에서만 동작하며, 모드를 끄면 지정 중인 선이 취소된다.
3. 미터 단위 셀 크기를 입력하고 `보행거리 계산`을 누른다. 히트맵, 가장 먼 도달
   가능 지점, 최대 보행거리와 출구까지의 실제 최단경로가 점선으로 표시된다.
4. 계산 뒤 도면의 임의 지점을 클릭하면 가장 가까운 출구까지의 보행거리와 실제
   최단경로가 표시된다. 다른 지점을 클릭하면 기존 조회 점선은 새 경로로 교체된다.
   벽이거나 도달 불가능한 지점도 구분해 안내한다.
5. `프로젝트 저장`은 벽 geometry, 출구 선분, 셀 크기, 단위 배율을 v3 JSON 안에
   넣는다. 원본 DXF를 옮기거나 삭제해도 v3 프로젝트를 다시 열 수 있다.

기존 v1 경로 기반 프로젝트와 v2 자체 포함 프로젝트의 포인트 출구는 같은 위치의
길이 0인 선분으로 자동 변환된다. v1은 원본 DXF가 남아 있어야 하며, DXF에 단위가
없으면 새 DXF와 마찬가지로 사용자에게 단위를 묻는다.

## 지원 DXF와 단위

- ASCII DXF의 `LINE`, `LWPOLYLINE`, `POLYLINE`/`VERTEX`, `CIRCLE`, `ARC`
- `$INSUNITS` 1~24를 미터로 변환(0 또는 누락은 사용자 선택)
- ENTITIES 섹션이 잘못되었거나 지원되는 벽이 하나도 없으면 오류 처리
- 원과 호는 격자화를 위해 선분으로 분할

[`samples/sample-room.dxf`](samples/sample-room.dxf)는 미터 단위의 12 m × 8 m 방과
상부 통로가 있는 내부 벽을 담은 반복 검증용 도면이다.

## 알고리즘

벽 선분을 입력 셀 크기의 격자로 rasterize한 뒤 .NET의 우선순위 큐를 이용해
다중 소스 Dijkstra를 수행한다. 각 출구 선분과 한 셀 이내의 통행 가능 셀에는
벽을 통과하지 않고 도달 가능한 실제 출구 선분상의 접점을 함께 저장한다. 직교 이동은
셀 크기, 대각선 이동은 `셀 크기 × √2`로 누적하며, 대각선 양옆 중 한 셀이라도
벽이면 이동을 금지해 코너 컷을 막는다.

predecessor로 복원한 격자 경로는 실제 클릭 좌표에서 시작한다. 이후 보수적인
supercover line-of-sight 검사로 벽 셀을 통과하거나 금지된 대각 코너를 자르지 않는
범위에서 불필요한 셀 중심 꺾임을 제거하고, 가능한 경우 실제 출구 접점에서 끝나는
연속 polyline으로 표시한다. 선택 지점 거리와 최대 거리 표시는 이 polyline 선분들의
기하 길이 합계를 사용한다. 벽이 있으면 line-of-sight가 차단되어 실제 개구부를 지난다.

출구에서 도달할 수 없는 자유 셀은 무한대로 유지하며 최대값과 가장 먼 지점 선정에서
제외한다. UI는 제외된 셀 수를 알린다. 메모리 급증을 막기 위해 계산 전 격자를 최대
4,000,000셀로 제한하며, 초과 시 셀 크기를 키우라는 전용 안내를 표시한다.

## 검증

```powershell
dotnet test WalkDistance.sln
dotnet restore src/WalkDistance.App/WalkDistance.App.csproj -r win-x64
dotnet build src/WalkDistance.App/WalkDistance.App.csproj -c Release -r win-x64 --no-restore
dotnet publish src/WalkDistance.App/WalkDistance.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o dist/win-x64
```

반복 가능한 수동 스모크 절차:

1. 샘플 DXF를 열고 출구 지정 모드에서 두 번 클릭해 출구 선분을 만든다. 첫 클릭
   뒤 마우스를 움직일 때 미리보기 선이 따라오는지 확인한다.
2. 새 선분의 첫 점만 찍고 우클릭해 완료 출구는 유지한 채 pending 선만 사라지는지,
   다시 우클릭해 마지막 완료 선분이 삭제되는지 확인한다.
3. 기본 셀 크기 `0.1`로 계산해 히트맵, 빨간 최장거리 지점과 출구까지의 점선 경로가
   나타나는지 확인한다.
4. 출구 지정 모드가 꺼진 상태에서 도면의 서로 다른 지점을 차례로 클릭해 거리 문구와
   조회 점선이 중복되지 않고 새 경로로 갱신되는지 확인한다.
5. 프로젝트를 저장하고 샘플 DXF를 임시로 다른 곳에 옮긴 뒤 출구 선분까지 그대로
   열리는지 확인한다.
6. 셀 크기를 매우 작게 입력해 4,000,000셀 제한 안내가 나타나는지 확인한다.

자동 테스트는 단위 변환/단위 확인, 모든 지원 엔티티, v3 선분 round-trip, v1/v2
마이그레이션, 출구 편집 상태, 선분 source, 실제 좌표 기반 직선/우회 경로, 출구 접점,
격자 상한, unreachable 제외, 벽 충돌과 대각선 코너 컷 방지를 다룬다.

## 제약

- 바이너리 DXF, spline, block insert 확장, hatch 영역은 지원하지 않는다.
- 벽은 선분을 반 셀 간격으로 샘플링해 막힌 셀로 변환하므로 세밀한 도면은 더 작은
  셀 크기가 필요하다.
- 최단경로의 도달성과 source 선택은 격자 Dijkstra 해상도에 따르며, 표시 경로는 그
  안전한 격자 경로를 line-of-sight로 단순화한 polyline이다.
- 본 MVP는 선 중심을 벽으로 취급하며 벽 두께, 문 폭, 사람 반경은 모델링하지 않는다.

## 구조

- `src/WalkDistance.Core`: DXF, geometry, 격자, Dijkstra, 프로젝트 파일
- `src/WalkDistance.App`: 한국어 WPF UI와 히트맵 렌더링
- `tests/WalkDistance.Core.Tests`: Core 자동 테스트
- `samples`: 수동 스모크용 DXF
