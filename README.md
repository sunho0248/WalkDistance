# 보행거리 계산 프로그램 (MVP)

C# .NET 8 WPF 데스크톱 애플리케이션. DXF 도면에서 출구를 지정하고, 임의 지점에서
가장 가까운 출구까지의 최단 보행거리를 계산한다.

## 요구사항

- .NET 8 SDK
- Windows

## 빌드

```
dotnet build WalkDistance.sln
```

## 실행

```
dotnet run --project src/WalkDistance.App/WalkDistance.App.csproj
```

## 테스트

```
dotnet test tests/WalkDistance.Core.Tests/WalkDistance.Core.Tests.csproj
```

## 사용법

1. 메뉴 `파일 > DXF 열기`로 도면을 불러온다.
2. 툴바의 `출구 지정 모드`를 켜고 캔버스를 클릭해 출구를 지정한다 (여러 개 가능).
3. `보행거리 계산`을 클릭하면 Distance Map(히트맵)과 가장 먼 지점(빨간 점)이
   표시되고, 하단 상태 표시줄에 최대 보행거리가 출력된다.
4. 메뉴 `파일 > 프로젝트 저장 / 프로젝트 열기`로 DXF 경로, 셀 크기, 출구 위치를
   저장하고 다시 불러올 수 있다.

## 구조

- `src/WalkDistance.Core`: DXF 파싱, 격자화, 최단 보행경로(다중 소스 Dijkstra)
  계산, 프로젝트 저장/불러오기 로직 (UI 비의존, 단위 테스트 대상)
- `src/WalkDistance.App`: WPF UI
- `tests/WalkDistance.Core.Tests`: Core 로직 단위 테스트

경로탐색은 상용 도구의 코드/UI를 참고하지 않고, 공개된 격자 기반 다중 소스
Dijkstra(8방향, 대각선 코너 컷 방지)로 독자 구현했다.
