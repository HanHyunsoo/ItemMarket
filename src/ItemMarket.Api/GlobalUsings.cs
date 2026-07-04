// Orleans의 암시적 global using(Orleans 네임스페이스)이 도입하는 Orleans.ErrorCode와
// 계약 ErrorCode의 충돌을 프로젝트 전역에서 계약 쪽으로 고정한다.
global using ErrorCode = ItemMarket.Contracts.Common.ErrorCode;
