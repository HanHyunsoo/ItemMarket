using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Wallet;

namespace ItemMarket.Grains.Data;

/// <summary>
/// DB(Postgres, 대문자 SNAKE_CASE 텍스트) ↔ 계약(C# enum) 사이의 매핑.
/// DDL은 열거값을 TEXT로 저장하므로 여기서 단일 지점 변환한다.
/// </summary>
public static class Enums
{
    // --- OrderSide ---------------------------------------------------------
    public static string ToDb(this OrderSide s) => s == OrderSide.Buy ? "BUY" : "SELL";
    public static OrderSide ToSide(string s) => s == "BUY" ? OrderSide.Buy : OrderSide.Sell;

    // --- OrderStatus -------------------------------------------------------
    public static string ToDb(this OrderStatus s) => s switch
    {
        OrderStatus.Open => "OPEN",
        OrderStatus.PartiallyFilled => "PARTIALLY_FILLED",
        OrderStatus.Filled => "FILLED",
        OrderStatus.Cancelled => "CANCELLED",
        _ => "OPEN"
    };

    public static OrderStatus ToStatus(string s) => s switch
    {
        "OPEN" => OrderStatus.Open,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELLED" => OrderStatus.Cancelled,
        _ => OrderStatus.Open
    };

    // --- WalletLedgerReason -----------------------------------------------
    public static string ToDb(this WalletLedgerReason r) => r switch
    {
        WalletLedgerReason.OrderEscrow => "ORDER_ESCROW",
        WalletLedgerReason.OrderRefund => "ORDER_REFUND",
        WalletLedgerReason.TradePayment => "TRADE_PAYMENT",
        WalletLedgerReason.TradeProceeds => "TRADE_PROCEEDS",
        WalletLedgerReason.Fee => "FEE",
        WalletLedgerReason.AdminAdjust => "ADMIN_ADJUST",
        _ => "ADMIN_ADJUST"
    };

    public static WalletLedgerReason ToReason(string r) => r switch
    {
        "ORDER_ESCROW" => WalletLedgerReason.OrderEscrow,
        "ORDER_REFUND" => WalletLedgerReason.OrderRefund,
        "TRADE_PAYMENT" => WalletLedgerReason.TradePayment,
        "TRADE_PROCEEDS" => WalletLedgerReason.TradeProceeds,
        "FEE" => WalletLedgerReason.Fee,
        "ADMIN_ADJUST" => WalletLedgerReason.AdminAdjust,
        _ => WalletLedgerReason.AdminAdjust
    };

    // --- ItemCategory ------------------------------------------------------
    public static ItemCategory ToCategory(string c) => c switch
    {
        "FOOD" => ItemCategory.Food,
        "MEDICAL" => ItemCategory.Medical,
        "MELEE" => ItemCategory.Melee,
        "GUN" => ItemCategory.Gun,
        "AMMO" => ItemCategory.Ammo,
        _ => ItemCategory.Food
    };

    // --- ItemRarity --------------------------------------------------------
    public static ItemRarity ToRarity(string r) => r switch
    {
        "COMMON" => ItemRarity.Common,
        "UNCOMMON" => ItemRarity.Uncommon,
        "RARE" => ItemRarity.Rare,
        "EPIC" => ItemRarity.Epic,
        "LEGENDARY" => ItemRarity.Legendary,
        _ => ItemRarity.Common
    };
}
