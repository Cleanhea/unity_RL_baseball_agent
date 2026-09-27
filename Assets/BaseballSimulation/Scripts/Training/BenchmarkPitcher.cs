using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 고정 상대 평가용 기준 스크립트 투수(docs/training-curriculum.md "고정 상대 평가"). 학습 중인 타자를 같은 잣대로 재기 위해
    /// 구종 비율·구속·위치 분포를 고정한다. 위치는 연속 분포이고 ML-Agents 타입에 의존하지 않는다.
    /// 1단계 스크립트 투수도 같은 위치 분포(<see cref="SampleLocation"/>)를 쓴다.
    /// </summary>
    public static class BenchmarkPitcher
    {
        /// <summary>
        /// 목표 위치가 플레이트·존 밖으로 벗어날 수 있는 여유(m). 투수 Agent가 연속 좌표를 쓰던 때의 행동 범위이며,
        /// 이전 학습 실행과 평가 지표를 비교할 수 있게 그대로 둔다.
        /// </summary>
        public const float LocationMargin = 0.25f;

        /// <summary>구종별 비율. MLB 구종 비율을 이 환경의 5종으로 어림했다(커터·스플리터 없음, 스위퍼는 슬라이더로 합침).</summary>
        public static readonly (PitchType type, float weight)[] Mix =
        {
            (PitchType.FourSeam, 0.35f), (PitchType.TwoSeam, 0.17f), (PitchType.Slider, 0.23f),
            (PitchType.Changeup, 0.13f), (PitchType.Curve, 0.12f),
        };

        /// <summary>존 중앙을 중심으로 한 목표 위치의 표준편차(m, 좌우·높이). 존 통과 비율이 약 절반이 되게 정했다.</summary>
        public static readonly Vector2 LocationSpread = new Vector2(0.25f, 0.28f);

        /// <summary>
        /// 다음 투구 명령. 구종은 <see cref="Mix"/> 비율, 구속은 구종 프로필 범위의 균등 분포, 위치는 <see cref="SampleLocation"/>
        /// (<see cref="LocationSpread"/>)다.
        /// </summary>
        public static PitchCommand Next(System.Random random, BaseballEnvironmentConfig config, Vector2 zoneCenter)
        {
            double roll = random.NextDouble();
            PitchType type = Mix[Mix.Length - 1].type;
            float cumulative = 0f;
            foreach (var (candidate, weight) in Mix)
            {
                cumulative += weight;
                if (roll < cumulative)
                {
                    type = candidate;
                    break;
                }
            }
            PitchTypeProfile profile = config.GetPitchProfile(type);
            float speed = Mathf.Lerp(profile.MinSpeed, profile.MaxSpeed, (float)random.NextDouble());
            return new PitchCommand(type, speed, SampleLocation(random, config, zoneCenter, LocationSpread));
        }

        /// <summary>
        /// 존 중앙 기준 정규 분포(표준편차 <paramref name="spread"/>, 좌우·높이) 목표 위치. 플레이트·존 ± <see cref="LocationMargin"/>으로 자른다.
        /// 표준편차가 0이면 존 중앙이다.
        /// </summary>
        public static Vector2 SampleLocation(System.Random random, BaseballEnvironmentConfig config, Vector2 zoneCenter, Vector2 spread)
        {
            Vector2 half = LocationHalfRange(config);
            float x = Mathf.Clamp(spread.x * Gaussian(random), -half.x, half.x);
            float y = Mathf.Clamp(spread.y * Gaussian(random), -half.y, half.y);
            return new Vector2(x, zoneCenter.y + y);
        }

        /// <summary>존 중앙에서 목표 위치가 벗어날 수 있는 최대 거리(m, 좌우·높이).</summary>
        public static Vector2 LocationHalfRange(BaseballEnvironmentConfig config) => new Vector2(
            StrikeZone.PlateWidth * 0.5f + LocationMargin, 0.5f * (config.StrikeZoneTop - config.StrikeZoneBottom) + LocationMargin);

        private static float Gaussian(System.Random random)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            return (float)(System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2));
        }
    }
}
