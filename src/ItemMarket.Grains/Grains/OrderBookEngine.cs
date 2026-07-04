using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Trades;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;
using Microsoft.Extensions.Logging;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 매칭 엔진 코어(grain이 아님). ===== 동시성 스토리의 핵심 =====
///
/// 기존 <c>OrderBookGrain</c>이 인라인으로 갖고 있던 매칭·에스크로·정산·재수화·스냅샷
/// 로직을 <b>그대로</b> 추출한 것. 소유 grain이 <b>단일 활성화 + 논-리엔트런트</b>인 한,
/// 이 엔진이 다루는 호가창(전체 템플릿이든 특정 가격 밴드든)의 매칭은 사실상 단일 스레드로
/// 직렬화되어 매칭 레이스(중복 체결·이중 판매)가 원천 차단된다.
///
/// 이 엔진은 자신이 다루는 주문 <b>범위(scope)</b>를 알지 못한다. 활성화 시 소유 grain이
/// 넘겨준 미체결 주문으로 인메모리 호가창을 재수화하고, 이후 그 안에서만 매칭한다:
///  - 밴딩 OFF → 소유 grain(<see cref="OrderBookGrain"/>)이 템플릿 전체 주문을 넘김(기존 동작).
///  - 밴딩 ON  → 소유 grain(<see cref="OrderBandGrain"/>)이 자기 밴드 주문만 넘김(밴드-격리 매칭).
///
/// 상태(호가창)는 활성화 시 Postgres에서 재수화하고 이후 인메모리로 유지하되, 모든 실제
/// 자산 이동은 Postgres 트랜잭션으로 커밋한다(소스오브트루스=DB).
/// </summary>
internal sealed class OrderBookEngine(
    MarketRepository repo,
    IGrainFactory grains,
    ILogger log,
    int templateId,
    Action deactivateOnIdle)
{
    // ---- 가격/수량 상한 -----------------------------------------------------
    // long 곱셈 오버플로 방어. 상한 없이 UnitPrice×Quantity가 long을 넘으면
    // 음수 에스크로가 되어 지갑에 병뚜껑이 "찍히는" 치명적 익스플로잇이 된다.
    // (검증은 Int128로 수행하므로 상한 이내에서는 오버플로 자체가 불가능.)
    internal const long MaxUnitPrice = 1_000_000_000_000;      // 1조 CAP
    internal const int MaxQuantity = 1_000_000;                // 100만 개
    internal const long MaxNotional = 1_000_000_000_000_000;   // 주문 총액 상한(1000조 CAP)

    /// <summary>호가창에 잔존하는 주문(인메모리 뷰).</summary>
    private sealed class Resting
    {
        public required Guid Id;
        public required Guid PlayerId;
        public required OrderSide Side;
        public required long UnitPrice;
        public required int Remaining;
        public required Guid? InstanceId;
        public required DateTimeOffset CreatedAt;
    }

    private readonly List<Resting> _book = [];
    private int _feeBps = 500;
    private bool _stackable;

    // 활성화 시 DB에서 미체결 주문과 설정을 로드해 인메모리 호가창을 재수화.
    // liveOrders는 소유 grain이 자신의 범위(템플릿 전체 / 특정 밴드)에 맞게 조회해 넘긴다.
    public async Task RehydrateAsync(IReadOnlyList<OrderRow> liveOrders)
    {
        _feeBps = await repo.GetFeeBpsAsync();
        var tmpl = await repo.GetTemplateAsync(templateId);
        _stackable = tmpl?.Stackable ?? true;

        foreach (var o in liveOrders)
        {
            _book.Add(new Resting
            {
                Id = o.Id,
                PlayerId = o.PlayerId,
                Side = o.Side,
                UnitPrice = o.UnitPrice,
                Remaining = o.RemainingQuantity,
                InstanceId = o.InstanceId,
                CreatedAt = o.CreatedAt
            });
        }
    }

    // ======================================================================
    //  주문 등록 + 즉시 매칭
    // ======================================================================
    public async Task<PlaceOrderResult> PlaceOrderAsync(Guid playerId, PlaceOrderRequest req)
    {
        // ---- 검증 ---------------------------------------------------------
        if (req.ItemTemplateId != templateId)
            throw new DomainException(ErrorCode.ValidationError, "템플릿 ID가 주문서와 일치하지 않습니다.");
        var tmpl = await repo.GetTemplateAsync(templateId)
            ?? throw new DomainException(ErrorCode.TemplateNotFound, "아이템 템플릿을 찾을 수 없습니다.");
        _stackable = tmpl.Stackable;

        if (req.Quantity < 1 || req.Quantity > MaxQuantity)
            throw new DomainException(ErrorCode.ValidationError, $"수량은 1 이상 {MaxQuantity:N0} 이하이어야 합니다.");
        if (req.UnitPrice <= 0 || req.UnitPrice > MaxUnitPrice)
            throw new DomainException(ErrorCode.ValidationError, $"단가는 1 이상 {MaxUnitPrice:N0} 이하이어야 합니다.");
        // 총액(단가×수량)은 Int128로 계산해 long 오버플로를 원천 차단.
        var notional = (Int128)req.UnitPrice * req.Quantity;
        if (notional > MaxNotional)
            throw new DomainException(ErrorCode.ValidationError, $"주문 총액(단가×수량)은 {MaxNotional:N0} CAP을 넘을 수 없습니다.");

        if (req.Side == OrderSide.Sell)
        {
            if (_stackable && req.InstanceId is not null)
                throw new DomainException(ErrorCode.StackableMismatch, "스택형 매도에는 InstanceId를 지정할 수 없습니다.");
            if (!_stackable)
            {
                if (req.InstanceId is null)
                    throw new DomainException(ErrorCode.StackableMismatch, "유니크 매도에는 InstanceId가 필요합니다.");
                if (req.Quantity != 1)
                    throw new DomainException(ErrorCode.StackableMismatch, "유니크 매도 수량은 1이어야 합니다.");
            }
        }
        else // Buy
        {
            if (req.InstanceId is not null)
                throw new DomainException(ErrorCode.StackableMismatch, "매수 주문에는 InstanceId를 지정할 수 없습니다.");
        }

        var orderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        // ---- 에스크로(자산 잠금) -----------------------------------------
        long escrowCaps = 0;
        if (req.Side == OrderSide.Buy)
        {
            escrowCaps = (long)notional; // 위에서 MaxNotional 검증됨 — 오버플로 불가
            var ok = await grains.GetGrain<IWalletGrain>(playerId).TryEscrow(escrowCaps, orderId);
            if (!ok) throw new DomainException(ErrorCode.InsufficientFunds, "병뚜껑 잔액이 부족합니다.");
        }
        else if (_stackable)
        {
            var ok = await grains.GetGrain<IPlayerInventoryGrain>(playerId).TryEscrowStack(templateId, req.Quantity);
            if (!ok) throw new DomainException(ErrorCode.InsufficientQuantity, "인벤토리 수량이 부족합니다.");
        }
        else
        {
            var outcome = await grains.GetGrain<IPlayerInventoryGrain>(playerId)
                .TryEscrowInstance(req.InstanceId!.Value, templateId);
            switch (outcome)
            {
                case EscrowInstanceOutcome.NotFound: throw new DomainException(ErrorCode.InstanceNotFound, "인스턴스를 찾을 수 없습니다.");
                case EscrowInstanceOutcome.NotOwned: throw new DomainException(ErrorCode.InstanceNotOwned, "소유하지 않은 인스턴스입니다.");
                case EscrowInstanceOutcome.TemplateMismatch: throw new DomainException(ErrorCode.StackableMismatch, "인스턴스의 템플릿이 일치하지 않습니다.");
            }
        }

        // ---- 주문 영속화(OPEN, 전량 잔존 상태로) --------------------------
        // 에스크로 커밋 후 주문 INSERT가 실패하면 잠긴 자산을 되돌릴 주문이
        // 존재하지 않게 되므로(취소 불가) 반드시 보상(compensation)으로 원복한다.
        try
        {
            await repo.InsertOrderAsync(new OrderRow(
                orderId, playerId, req.Side, templateId, req.UnitPrice, req.Quantity,
                req.Quantity, req.Side == OrderSide.Sell && !_stackable ? req.InstanceId : null,
                OrderStatus.Open, escrowCaps, createdAt));
        }
        catch (Exception insertEx)
        {
            try
            {
                if (req.Side == OrderSide.Buy)
                    await grains.GetGrain<IWalletGrain>(playerId).Refund(escrowCaps, orderId);
                else if (_stackable)
                    await grains.GetGrain<IPlayerInventoryGrain>(playerId).ReturnStack(templateId, req.Quantity);
                else
                    await grains.GetGrain<IPlayerInventoryGrain>(playerId).ReturnInstance(req.InstanceId!.Value);
            }
            catch (Exception compEx)
            {
                // 보상까지 실패: 원장의 ORDER_ESCROW 기록(ref=orderId)으로 수동 복구 가능.
                log.LogCritical(compEx,
                    "주문 {OrderId} INSERT 실패 후 에스크로 보상도 실패. 수동 복구 필요 (player={PlayerId}, template={TemplateId})",
                    orderId, playerId, templateId);
            }
            log.LogError(insertEx, "주문 {OrderId} 영속화 실패 — 에스크로 원복 시도 완료", orderId);
            throw;
        }

        var incoming = new Resting
        {
            Id = orderId,
            PlayerId = playerId,
            Side = req.Side,
            UnitPrice = req.UnitPrice,
            Remaining = req.Quantity,
            InstanceId = req.Side == OrderSide.Sell && !_stackable ? req.InstanceId : null,
            CreatedAt = createdAt
        };

        // ---- 매칭(가격-시간 우선) ----------------------------------------
        var fills = await MatchAsync(incoming);

        // ---- 잔량 처리: 남으면 호가창에 잔존 -----------------------------
        OrderStatus finalStatus;
        if (incoming.Remaining == 0)
        {
            finalStatus = OrderStatus.Filled; // 마지막 SettleFill이 DB status=Filled로 갱신함
        }
        else
        {
            _book.Add(incoming);
            finalStatus = fills.Count > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Open;
        }

        var orderDto = new OrderDto(
            orderId, playerId, req.Side, templateId, req.UnitPrice, req.Quantity,
            incoming.Remaining, finalStatus, incoming.InstanceId, createdAt);

        return new PlaceOrderResult(orderDto, fills);
    }

    /// <summary>
    /// 매칭 루프. 반대편 호가를 가격-시간 우선으로 훑으며 교차하는 만큼 체결한다.
    /// 체결가 = 메이커(호가창 잔존 주문)의 가격 → 테이커에게 가격 개선(차익)이 돌아간다.
    /// 각 체결은 SettleFill로 단일 Postgres 트랜잭션 정산.
    /// 자기 주문과는 체결하지 않는다(자전거래 방지 — 본인 주문은 건너뛰고 잔존).
    /// </summary>
    private async Task<List<TradeDto>> MatchAsync(Resting incoming)
    {
        var fills = new List<TradeDto>();

        // 반대편 후보를 가격-시간 우선으로 정렬.
        //  - 매수 테이커: 매도(ask) 중 price <= 상한가, 가격 오름차순 → 가장 싼 것부터.
        //  - 매도 테이커: 매수(bid) 중 price >= 하한가, 가격 내림차순 → 가장 비싼 것부터.
        List<Resting> Candidates() => incoming.Side == OrderSide.Buy
            ? _book.Where(o => o.Side == OrderSide.Sell && o.UnitPrice <= incoming.UnitPrice)
                   .OrderBy(o => o.UnitPrice).ThenBy(o => o.CreatedAt).ToList()
            : _book.Where(o => o.Side == OrderSide.Buy && o.UnitPrice >= incoming.UnitPrice)
                   .OrderByDescending(o => o.UnitPrice).ThenBy(o => o.CreatedAt).ToList();

        foreach (var maker in Candidates())
        {
            if (incoming.Remaining == 0) break;
            if (maker.PlayerId == incoming.PlayerId) continue; // 자전거래(self-trade) 방지

            var qty = Math.Min(incoming.Remaining, maker.Remaining);
            var execPrice = maker.UnitPrice; // 체결은 메이커 가격에

            var (buy, sell) = incoming.Side == OrderSide.Buy ? (incoming, maker) : (maker, incoming);

            // 체결 후 잔량을 "계산만" 해서 정산 tx에 넘기고, 인메모리 반영은
            // 커밋 성공 후에 한다. 선반영하면 정산 실패 시 인메모리 호가창이
            // DB와 어긋난 채 남아 이후 모든 매칭이 낙관적 가드에 걸린다.
            var buyRemaining = buy.Remaining - qty;
            var sellRemaining = sell.Remaining - qty;
            var buyStatus = buyRemaining == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
            var sellStatus = sellRemaining == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
            var tradeId = Guid.NewGuid();
            var executedAt = DateTimeOffset.UtcNow;

            try
            {
                await repo.SettleFillAsync(new SettleFillArgs(
                    tradeId, templateId, buy.Id, sell.Id, buy.PlayerId, sell.PlayerId,
                    execPrice, qty, buy.UnitPrice, _feeBps, sell.InstanceId, _stackable,
                    buyRemaining, buyStatus, sellRemaining, sellStatus, executedAt));
            }
            catch (Exception ex)
            {
                // 정산 실패: DB가 소스오브트루스. 인메모리 뷰가 어긋났을 가능성이
                // 있으므로 활성화를 버리고 다음 호출에서 DB로부터 재수화한다.
                // (이미 커밋된 이전 fills는 유효하며 재수화에 반영된다.)
                log.LogError(ex, "정산 실패 — 호가창 grain(template={TemplateId}) 재수화를 위해 비활성화", templateId);
                deactivateOnIdle();
                throw;
            }

            // 커밋 성공 후에만 인메모리 반영.
            incoming.Remaining -= qty;
            maker.Remaining -= qty;

            var fee = MarketRepository.CalcFee(execPrice, qty, _feeBps);
            fills.Add(new TradeDto(
                tradeId, templateId, execPrice, qty, buy.PlayerId, sell.PlayerId,
                buy.Id, sell.Id, sell.InstanceId, fee, executedAt));

            if (maker.Remaining == 0) _book.Remove(maker);
        }

        return fills;
    }

    // ======================================================================
    //  취소(에스크로 환불)
    // ======================================================================
    public async Task<OrderDto> CancelOrderAsync(Guid playerId, Guid orderId, bool isAdmin)
    {
        var order = await repo.GetOrderAsync(orderId)
            ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
        // 이 엔진(범위)의 주문이 아니면 취소 거부 — 다른 grain의 인메모리 호가창에
        // 잔존한 주문을 여기서 지우면 두 뷰가 어긋난다.
        if (order.TemplateId != templateId)
            throw new DomainException(ErrorCode.OrderNotFound, "이 템플릿의 주문이 아닙니다.");
        if (!isAdmin && order.PlayerId != playerId)
            throw new DomainException(ErrorCode.OrderNotOwned, "본인 주문만 취소할 수 있습니다.");

        var result = await repo.CancelOrderAsync(orderId); // 에스크로 환불 + 상태 갱신(단일 tx)
        _book.RemoveAll(o => o.Id == orderId);
        return result;
    }

    // ======================================================================
    //  호가창 스냅샷(가격대별 집계)
    // ======================================================================
    public OrderBookSnapshotDto GetSnapshot()
    {
        var bids = _book.Where(o => o.Side == OrderSide.Buy)
            .GroupBy(o => o.UnitPrice)
            .Select(g => new OrderBookLevelDto(g.Key, g.Sum(o => o.Remaining), g.Count()))
            .OrderByDescending(l => l.UnitPrice).ToList();

        var asks = _book.Where(o => o.Side == OrderSide.Sell)
            .GroupBy(o => o.UnitPrice)
            .Select(g => new OrderBookLevelDto(g.Key, g.Sum(o => o.Remaining), g.Count()))
            .OrderBy(l => l.UnitPrice).ToList();

        return new OrderBookSnapshotDto(templateId, bids, asks);
    }
}
