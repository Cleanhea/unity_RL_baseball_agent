using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 공식 규칙의 스트라이크존: 오각형 홈플레이트 바로 위, 설정한 아래·위 높이 사이의 입체 공간.
    /// 공의 어느 부분이라도 이 공간에 닿으면 존 통과다.
    /// 홈 좌표는 파울선이 만나는 플레이트 뒤 꼭짓점이고, 앞 모서리가 투수 쪽으로 17 in 떨어져 있다.
    /// 플레이트 좌표는 (x 좌우, y 투수 쪽 깊이, z 지면 기준 높이)이며 x 양수가 1루 쪽이다.
    /// </summary>
    public static class StrikeZone
    {
        /// <summary>앞 모서리 폭 17 in(m).</summary>
        public const float PlateWidth = 0.4318f;

        /// <summary>뒤 꼭짓점에서 앞 모서리까지 17 in(m).</summary>
        public const float PlateDepth = 0.4318f;

        private const float Half = PlateWidth * 0.5f;

        // 반시계 방향. 앞 모서리 17 in, 옆변 8.5 in, 뒤 두 변 12 in이 뒤 꼭짓점(홈)에서 만난다.
        private static readonly Vector2[] Outline =
        {
            new Vector2(0f, 0f), new Vector2(Half, Half), new Vector2(Half, PlateDepth),
            new Vector2(-Half, PlateDepth), new Vector2(-Half, Half),
        };

        /// <summary>월드 위치를 플레이트 좌표로 바꾼다. 깊이 방향은 홈에서 투수판 쪽이다.</summary>
        public static Vector3 ToPlate(FieldLayout field, Vector3 world)
        {
            Vector3 home = field.HomePosition;
            Vector3 forward = field.PitcherPlatePosition - home;
            forward.y = 0f;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 local = world - home;
            return new Vector3(Vector3.Dot(local, right), Vector3.Dot(local, forward), local.y);
        }

        /// <summary>플레이트 좌표 중심의 공(반지름 <paramref name="radius"/>)이 존에 닿으면 true다.</summary>
        public static bool Intersects(Vector3 plate, float radius, float bottom, float top)
        {
            float horizontal = DistanceToPlate(new Vector2(plate.x, plate.y));
            float vertical = plate.z < bottom ? bottom - plate.z : plate.z > top ? plate.z - top : 0f;
            return horizontal * horizontal + vertical * vertical <= radius * radius;
        }

        /// <summary>플레이트 오각형까지 수평 거리(m). 플레이트 위면 0이다.</summary>
        public static float DistanceToPlate(Vector2 point)
        {
            bool inside = true;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < Outline.Length; i++)
            {
                Vector2 a = Outline[i];
                Vector2 edge = Outline[(i + 1) % Outline.Length] - a;
                Vector2 offset = point - a;
                if (edge.x * offset.y - edge.y * offset.x < 0f) inside = false;
                float t = Mathf.Clamp01(Vector2.Dot(offset, edge) / edge.sqrMagnitude);
                nearest = Mathf.Min(nearest, (offset - edge * t).magnitude);
            }
            return inside ? 0f : nearest;
        }
    }
}
