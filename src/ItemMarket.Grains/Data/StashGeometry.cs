namespace ItemMarket.Grains.Data;

/// <summary>스태시 그리드 위 사각형(좌상단 X,Y + W×H footprint). 겹침/경계 판정용.</summary>
public readonly record struct Rect(int X, int Y, int W, int H);

/// <summary>
/// 스태시 그리드 기하 계산의 단일 지점. 그리드 크기 상수 + 경계/겹침/first-fit 판정.
/// 순수 함수라 grain 없이도 단위 테스트가 가능하다.
/// </summary>
public static class StashGeometry
{
    /// <summary>플레이어별 고정 스태시 그리드 크기(칸). 좌상단 (0,0) 기준.</summary>
    public const int GridW = 10;
    public const int GridH = 12;

    /// <summary>footprint가 그리드 경계 안에 완전히 들어오는가.</summary>
    public static bool InBounds(Rect r)
        => r.X >= 0 && r.Y >= 0 && r.W >= 1 && r.H >= 1
           && r.X + r.W <= GridW && r.Y + r.H <= GridH;

    /// <summary>두 사각형이 한 칸이라도 겹치는가(경계 맞닿음은 겹침 아님).</summary>
    public static bool Overlaps(Rect a, Rect b)
        => a.X < b.X + b.W && b.X < a.X + a.W
           && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>
    /// w×h footprint를 놓을 수 있는 첫 자리를 좌상단→오른쪽→아래로 스캔해 찾는다.
    /// occupied와 겹치지 않고 경계 안이면 그 (x,y)를 반환. 없으면 null.
    /// </summary>
    public static (int X, int Y)? FirstFit(IReadOnlyCollection<Rect> occupied, int w, int h)
    {
        for (var y = 0; y + h <= GridH; y++)
        {
            for (var x = 0; x + w <= GridW; x++)
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
