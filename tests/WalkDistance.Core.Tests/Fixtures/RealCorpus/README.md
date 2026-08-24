# Real DXF benchmark corpus

이 디렉터리는 익명화가 끝난 실제 운영 도면의 로컬 벤치마크 자리입니다. 원본 `.dxf`는
`.gitignore`로 보호되며 저장소에 커밋하지 않습니다. 현재 manifest는 비어 있고
`B-REAL-S/M/L` 데이터가 제공되기 전까지 실제 도면 수용 기준은 충족되지 않습니다.

`manifest.json`의 각 `fixtures` 항목은 다음 필드를 사용합니다.

- `id`: 결과에 표시할 안정적인 식별자
- `file`: 이 디렉터리 기준 DXF 파일명
- `sha256`: 대문자/소문자 무관 SHA-256
- `provenance`: 출처와 익명화 확인을 담은 한 줄 설명
- `sizeClass`: `S`, `M`, `L` 중 하나
- `sidecar`: 선택 사항인 JSON 파일명; DXF에 `WD_Exit`가 없거나 인체/셀 설정을 덮어쓸 때 사용

sidecar는 `id`, `exits`(`[[[x,y],...], ...]`), `bodyRadius`, `cellSize`를 지원합니다.
좌표와 길이는 `DxfLoader`가 변환한 미터 단위입니다. SHA 불일치, 출구 누락, DXF 로드
오류는 해당 fixture 오류로 기록되며 나머지 fixture 실행은 계속됩니다.
