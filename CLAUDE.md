# 프로젝트 규칙 (Wasteland Exchange)

## 커밋 규칙
- **커밋 메시지는 한글로 작성한다.** 제목(요약)과 본문 모두 한글. Conventional Commits 접두사
  (`feat`, `fix`, `docs`, `refactor`, `test`, `chore` 등)와 스코프는 영문 그대로 사용하되, 그 뒤 설명은 한글로 쓴다.
  - 예: `feat(raid): 출격 시 스태시 밖 자산을 at-risk로 잠그는 정산 추가`
- 논리 단위로 **중간중간 커밋**한다(한 커밋 = 한 가지 변경).
- 커밋 메시지 끝에는 아래 트레일러를 붙인다:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

## 저장소 중립성
- 이 저장소는 여러 C# 게임 서버 JD 지원에 재사용하므로 **특정 제품/회사명을 넣지 않는다**
  (예: 낙원, 넥슨/Nexon, 파라다이스/Paradise, 슈퍼진/Supergene, LastParadise). 좀비/zombie 표현도 배제.
- 장르는 **"아포칼립스/익스트랙션 슈터"** 같은 일반 표현만 사용한다.
