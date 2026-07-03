using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Trades;
using ItemMarket.Grains.Abstractions;
using ItemMarket.Grains.Data;

namespace ItemMarket.Grains.Grains;

/// <summary>
/// 매칭 엔진 grain(키 = templateId). ===== 동시성 스토리의 핵심 =====
///
/// Orleans는 grain 활성화를 클러스터 전체에서 "단일"로 보장하고(single activation),
/// 기본 non-reentrant라 한 번에 하나의 요청만 처리한다. 따라서 특정 템플릿의
/// 매칭은 실로가 몇 개든 사실상 단일 스레드로 직렬화되어 매칭 레이스(중복 체결,
/// 이중 판매)가 원천 차단된다.
///
/// 상태(호가창)는 활성화 시 Postgres에서 재수화하고, 이후 인메모리로 유지하되
/// 모든 실제 자산 이동은 Postgres 트랜잭션으로 커밋한다(소스오브트루스=DB).
/// </summary>
public sealed class OrderBookGrain(MarketRepository repo) : Grain, IOrderBookGrain
{
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

    private int TemplateId => (int)this.GetPrimaryKeyLong();

    private readonly List<Resting> _book = [];
    private int _feeBps = 500;
    private bool _stackable;

    // 활성화 시 DB에서 미체결 주문과 설정을 로드해 인메모리 호가창을 재수화.
    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _feeBps = await repo.GetFeeBpsAsync();
        var tmpl = await repo.GetTemplateAsync(TemplateId);
        _stackable = tmpl?.Stackable ?? true;

        foreach (var o in await repo.GetLiveOrdersAsync(TemplateId))
        {
            _book.Add(new Resting
            {
                Id = o.Id, PlayerId = o.PlayerId, Side = o.Side, UnitPrice = o.UnitPrice,
                Remaining = o.RemainingQuantity, InstanceId = o.InstanceId, CreatedAt = o.CreatedAt
            });
        }
        await base.OnActivateAsync(ct);
    }

    // ======================================================================
    //  주문 등록 + 즉시 매칭
    // ======================================================================
    public async Task<PlaceOrderResult> PlaceOrder(Guid playerId, PlaceOrderRequest req)
    {
        // ---- 검증 ---------------------------------------------------------
        if (req.ItemTemplateId != TemplateId)
            throw new DomainException(ErrorCode.ValidationError, "템플릿 ID가 주문서와 일치하지 않습니다.");
        var tmpl = await repo.GetTemplateAsync(TemplateId)
            ?? throw new DomainException(ErrorCode.TemplateNotFound, "아이템 템플릿을 찾을 수 없습니다.");
        _stackable = tmpl.Stackable;

        if (req.Quantity < 1) throw new DomainException(ErrorCode.ValidationError, "수량은 1 이상이어야 합니다.");
        if (req.UnitPrice <= 0) throw new DomainException(ErrorCode.ValidationError, "단가는 양수여야 합니다.");

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
            escrowCaps = req.UnitPrice * req.Quantity;
            var ok = await GrainFactory.GetGrain<IWalletGrain>(playerId).TryEscrow(escrowCaps, orderId);
            if (!ok) throw new DomainException(ErrorCode.InsufficientFunds, "병뚜껑 잔액이 부족합니다.");
        }
        else if (_stackable)
        {
            var ok = await GrainFactory.GetGrain<IPlayerInventoryGrain>(playerId).TryEscrowStack(TemplateId, req.Quantity);
            if (!ok) throw new DomainException(ErrorCode.InsufficientQuantity, "인벤토리 수량이 부족합니다.");
        }
        else
        {
            var outcome = await GrainFactory.GetGrain<IPlayerInventoryGrain>(playerId)
                .TryEscrowInstance(req.InstanceId!.Value, TemplateId);
            switch (outcome)
            {
                case EscrowInstanceOutcome.NotFound: throw new DomainException(ErrorCode.InstanceNotFound, "인스턴스를 찾을 수 없습니다.");
                case EscrowInstanceOutcome.NotOwned: throw new DomainException(ErrorCode.InstanceNotOwned, "소유하지 않은 인스턴스입니다.");
                case EscrowInstanceOutcome.TemplateMismatch: throw new DomainException(ErrorCode.StackableMismatch, "인스턴스의 템플릿이 일치하지 않습니다.");
            }
        }

        // ---- 주문 영속화(OPEN, 전량 잔존 상태로) --------------------------
        await repo.InsertOrderAsync(new OrderRow(
            orderId, playerId, req.Side, TemplateId, req.UnitPrice, req.Quantity,
            req.Quantity, req.Side == OrderSide.Sell && !_stackable ? req.InstanceId : null,
            OrderStatus.Open, escrowCaps, createdAt));

        var incoming = new Resting
        {
            Id = orderId, PlayerId = playerId, Side = req.Side, UnitPrice = req.UnitPrice,
            Remaining = req.Quantity, InstanceId = req.Side == OrderSide.Sell && !_stackable ? req.InstanceId : null,
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
            orderId, playerId, req.Side, TemplateId, req.UnitPrice, req.Quantity,
            incoming.Remaining, finalStatus, incoming.InstanceId, createdAt);

        return new PlaceOrderResult(orderDto, fills);
    }

    /// <summary>
    /// 매칭 루프. 반대편 호가를 가격-시간 우선으로 훑으며 교차하는 만큼 체결한다.
    /// 체결가 = 메이커(호가창 잔존 주문)의 가격 → 테이커에게 가격 개선(차익)이 돌아간다.
    /// 각 체결은 SettleFill로 단일 Postgres 트랜잭션 정산.
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

            var qty = Math.Min(incoming.Remaining, maker.Remaining);
            var execPrice = maker.UnitPrice; // 체결은 메이커 가격에

            var (buy, sell) = incoming.Side == OrderSide.Buy ? (incoming, maker) : (maker, incoming);

            // 잔량 선반영(정산 tx에 넘길 최종값 계산).
            incoming.Remaining -= qty;
            maker.Remaining -= qty;

            var buyStatus = buy.Remaining == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
            var sellStatus = sell.Remaining == 0 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
            var tradeId = Guid.NewGuid();
            var executedAt = DateTimeOffset.UtcNow;

            await repo.SettleFillAsync(new SettleFillArgs(
                tradeId, TemplateId, buy.Id, sell.Id, buy.PlayerId, sell.PlayerId,
                execPrice, qty, buy.UnitPrice, _feeBps, sell.InstanceId, _stackable,
                buy.Remaining, buyStatus, sell.Remaining, sellStatus, executedAt));

            var fee = execPrice * qty * _feeBps / 10000;
            fills.Add(new TradeDto(
                tradeId, TemplateId, execPrice, qty, buy.PlayerId, sell.PlayerId,
                buy.Id, sell.Id, sell.InstanceId, fee, executedAt));

            if (maker.Remaining == 0) _book.Remove(maker);
        }

        return fills;
    }

    // ======================================================================
    //  취소(에스크로 환불)
    // ======================================================================
    public async Task<OrderDto> CancelOrder(Guid playerId, Guid orderId, bool isAdmin)
    {
        var order = await repo.GetOrderAsync(orderId)
            ?? throw new DomainException(ErrorCode.OrderNotFound, "주문을 찾을 수 없습니다.");
        if (!isAdmin && order.PlayerId != playerId)
            throw new DomainException(ErrorCode.OrderNotOwned, "본인 주문만 취소할 수 있습니다.");

        var result = await repo.CancelOrderAsync(orderId); // 에스크로 환불 + 상태 갱신(단일 tx)
        _book.RemoveAll(o => o.Id == orderId);
        return result;
    }

    // ======================================================================
    //  호가창 스냅샷(가격대별 집계)
    // ======================================================================
    public Task<OrderBookSnapshotDto> GetSnapshot()
    {
        var bids = _book.Where(o => o.Side == OrderSide.Buy)
            .GroupBy(o => o.UnitPrice)
            .Select(g => new OrderBookLevelDto(g.Key, g.Sum(o => o.Remaining), g.Count()))
            .OrderByDescending(l => l.UnitPrice).ToList();

        var asks = _book.Where(o => o.Side == OrderSide.Sell)
            .GroupBy(o => o.UnitPrice)
            .Select(g => new OrderBookLevelDto(g.Key, g.Sum(o => o.Remaining), g.Count()))
            .OrderBy(l => l.UnitPrice).ToList();

        return Task.FromResult(new OrderBookSnapshotDto(TemplateId, bids, asks));
    }
}
