using ItemMarket.Contracts.Equipment;
using ItemMarket.Contracts.Items;
using ItemMarket.Contracts.Orders;
using ItemMarket.Contracts.Raid;
using ItemMarket.Contracts.Stash;
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

    // --- StashEntryKind ----------------------------------------------------
    public static string ToDb(this StashEntryKind k) => k == StashEntryKind.Instance ? "INSTANCE" : "STACK";
    public static StashEntryKind ToStashKind(string k) => k == "INSTANCE" ? StashEntryKind.Instance : StashEntryKind.Stack;

    // --- GridContainer -----------------------------------------------------
    public static string ToDb(this GridContainer c) => c switch
    {
        GridContainer.Loadout => "LOADOUT",
        GridContainer.Container => "CONTAINER",
        _ => "STASH"
    };

    public static GridContainer ToContainer(string c) => c switch
    {
        "LOADOUT" => GridContainer.Loadout,
        "CONTAINER" => GridContainer.Container,
        _ => GridContainer.Stash
    };

    // --- EquipSlot ---------------------------------------------------------
    public static string ToDb(this EquipSlot s) => s switch
    {
        EquipSlot.Helmet => "HELMET",
        EquipSlot.Armor => "ARMOR",
        EquipSlot.Weapon => "WEAPON",
        EquipSlot.Backpack => "BACKPACK",
        EquipSlot.Rig => "RIG",
        _ => "WEAPON"
    };

    public static EquipSlot ToEquipSlot(string s) => s switch
    {
        "HELMET" => EquipSlot.Helmet,
        "ARMOR" => EquipSlot.Armor,
        "WEAPON" => EquipSlot.Weapon,
        "BACKPACK" => EquipSlot.Backpack,
        "RIG" => EquipSlot.Rig,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "알 수 없는 장착 슬롯")
    };

    public static EquipSlot? ToEquipSlotOrNull(string? s) => s is null ? null : ToEquipSlot(s);

    // --- ItemCategory ------------------------------------------------------
    public static ItemCategory ToCategory(string c) => c switch
    {
        "FOOD" => ItemCategory.Food,
        "MEDICAL" => ItemCategory.Medical,
        "MELEE" => ItemCategory.Melee,
        "GUN" => ItemCategory.Gun,
        "AMMO" => ItemCategory.Ammo,
        "GEAR" => ItemCategory.Gear,
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

    // --- RaidStatus --------------------------------------------------------
    public static string ToDb(this RaidStatus s) => s switch
    {
        RaidStatus.Active => "ACTIVE",
        RaidStatus.Extracted => "EXTRACTED",
        RaidStatus.Died => "DIED",
        _ => "ACTIVE"
    };

    public static RaidStatus ToRaidStatus(string s) => s switch
    {
        "ACTIVE" => RaidStatus.Active,
        "EXTRACTED" => RaidStatus.Extracted,
        "DIED" => RaidStatus.Died,
        _ => RaidStatus.Active
    };

    // --- RaidItemSource ----------------------------------------------------
    public static string ToDb(this RaidItemSource s) => s == RaidItemSource.Looted ? "LOOTED" : "BROUGHT";
    public static RaidItemSource ToRaidSource(string s) => s == "LOOTED" ? RaidItemSource.Looted : RaidItemSource.Brought;

    // --- ItemLedgerReason (아이템 원장 태그) --------------------------------
    public static string ToDb(this ItemLedgerReason r) => r switch
    {
        ItemLedgerReason.RaidBrought => "RAID_BROUGHT",
        ItemLedgerReason.RaidExtract => "RAID_EXTRACT",
        ItemLedgerReason.RaidLoot => "RAID_LOOT",
        ItemLedgerReason.RaidLoss => "RAID_LOSS",
        ItemLedgerReason.AdminGrant => "ADMIN_GRANT",
        _ => "ADMIN_GRANT"
    };
}
