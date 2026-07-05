using ItemMarket.Contracts.Stash;

namespace ItemMarket.Grains.Data;

/// <summary>스태시 그리드 위 사각형(좌상단 X,Y + W×H footprint). 겹침/경계 판정용.</summary>
public readonly record struct Rect(int X, int Y, int W, int H);

/// <summary>
/// 그리드 기하 계산의 단일 지점. 컨테이너별 크기 상수 + 경계/겹침/first-fit 판정.
/// 순수 함수라 grain 없이도 단위 테스트가 가능하다.
/// </summary>
public static class StashGeometry
{
    /// <summary>STASH(안전 보관소) 크기(칸). 좌상단 (0,0) 기준.</summary>
    public const int StashW = 10;
    public const int StashH = 12;

    /// <summary>LOADOUT(레이드 반입) 크기(칸). STASH보다 작다.</summary>
    public const int LoadoutW = 6;
    public const int LoadoutH = 8;

    /// <summary>컨테이너의 (폭, 높이). 컨테이너별 크기의 단일 조회 지점.</summary>
    public static (int W, int H) Dims(GridContainer container) => container switch
    {
        GridContainer.Loadout => (LoadoutW, LoadoutH),
        _ => (StashW, StashH)
    };

    /// <summary>footprint가 컨테이너 경계 안에 완전히 들어오는가.</summary>
    public static bool InBounds(GridContainer container, Rect r)
    {
        var (gw, gh) = Dims(container);
        return InBounds(gw, gh, r);
    }

    /// <summary>footprint가 gw×gh 그리드 경계 안에 완전히 들어오는가(중첩 컨테이너 등 동적 크기용).</summary>
    public static bool InBounds(int gw, int gh, Rect r)
        => r.X >= 0 && r.Y >= 0 && r.W >= 1 && r.H >= 1
           && r.X + r.W <= gw && r.Y + r.H <= gh;

    /// <summary>두 사각형이 한 칸이라도 겹치는가(경계 맞닿음은 겹침 아님).</summary>
    public static bool Overlaps(Rect a, Rect b)
        => a.X < b.X + b.W && b.X < a.X + a.W
           && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>
    /// w×h footprint를 놓을 수 있는 첫 자리를 좌상단→오른쪽→아래로 스캔해 찾는다.
    /// occupied와 겹치지 않고 컨테이너 경계 안이면 그 (x,y)를 반환. 없으면 null.
    /// </summary>
    public static (int X, int Y)? FirstFit(GridContainer container, IReadOnlyCollection<Rect> occupied, int w, int h)
        => FirstFit(Dims(container).W, Dims(container).H, occupied, w, h);

    /// <summary>w×h footprint를 gw×gh 그리드에서 first-fit으로 찾는다(동적 크기용). 없으면 null.</summary>
    public static (int X, int Y)? FirstFit(int gw, int gh, IReadOnlyCollection<Rect> occupied, int w, int h)
    {
        for (var y = 0; y + h <= gh; y++)
        {
            for (var x = 0; x + w <= gw; x++)
            {
                var candidate = new Rect(x, y, w, h);
                var clash = false;
                foreach (var o in occupied)
                {
                    if (Overlaps(candidate, o)) { clash = true; break; }
                }
                if (!clash) return (x, y);
            }
        }
        return null;
    }
}
