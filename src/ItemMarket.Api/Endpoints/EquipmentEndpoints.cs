using System.Security.Claims;
using ItemMarket.Contracts.Equipment;
using ItemMarket.Grains.Abstractions;
using static ItemMarket.Api.Infrastructure.ApiResults;

namespace ItemMarket.Api.Endpoints;

/// <summary>
/// 장비(equipment) — 슬롯(HELMET/ARMOR/WEAPON/BACKPACK/RIG) 조회/장착/해제.
/// 백팩·리그는 장착 시 내부(중첩) 그리드를 제공하며, 그 그리드 배치는 /api/stash/move에
/// From/ToContainer=Container + From/ToContainerInstanceId(컨테이너 인스턴스 id)로 조작한다.
/// </summary>
public static class EquipmentEndpoints
{
    public static IEndpointRouteBuilder MapEquipmentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Equipment");

        // 장비 전체 스냅샷: 슬롯→인스턴스 + 장착된 백팩/리그의 중첩 그리드.
        api.MapGet("/equipment", (ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).GetEquipment()));

        // 장착: template.equip_slot == slot 검증. 불일치/슬롯 점유 시 SlotMismatch(400).
        api.MapPost("/equipment/equip", (EquipRequest req, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).Equip(req)));

        // 해제: 슬롯을 비운다. 해제 아이템(및 백팩/리그 내용물)은 소유로 남아 STASH로 회수된다.
        api.MapPost("/equipment/unequip", (UnequipRequest req, ClaimsPrincipal u, IGrainFactory gf) =>
            Exec(() => gf.GetGrain<IStashGrain>(CurrentPlayer(u)).Unequip(req)));

        return app;
    }
}
